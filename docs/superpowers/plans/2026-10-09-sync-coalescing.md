# Incremental Sync, Coalescing and Cancellation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A burst of edits costs one re-check, a request cancelled while queued isn't computed, and editors send only what changed.

**Architecture:** A reader task moves messages into a channel; the loop handles everything queued at once as a batch. A pure planner (`LspBatch.Plan`) merges consecutive `didChange` notifications per document and turns requests cancelled in the same batch into cancelled steps. Changes carry ranges (sync kind 2) applied by `TextEdits.Apply` to the service's current text (`TextOf`). Diagnostics are published once per batch. The service stays single-threaded.

**Tech Stack:** C# / .NET 10, `System.Threading.Channels`, xUnit, the repo's LSP server.

Spec: `docs/superpowers/specs/2026-10-09-sync-coalescing-design.md`.

---

## Files

- Create `Nitrogen.LanguageService/TextEdits.cs`: `TextChange`, `TextEdits.Apply`.
- Modify `Nitrogen.LanguageService/NitrogenLanguageService.cs`: `TextOf`.
- Create `Nitrogen.LanguageService/Lsp/LspBatch.cs`: the batch planner.
- Modify `Nitrogen.LanguageService/Lsp/LspMessages.cs`: `TextDocumentContentChangeEvent.Range`.
- Modify `Nitrogen.LanguageService/Lsp/LspServer.cs`: read-ahead, `Idle`, batches, sync kind 2, `RequestCancelled`.
- Create `Nitrogen.Tests/LanguageService/TextEditsTests.cs`, `LspBatchTests.cs`, `IncrementalSyncTests.cs`, `LockstepInput.cs`.
- Modify tests that drive the server: `LspServerTests.cs` (`Session`, sync kind), `GrammarLoopTests.cs`, `TodayValueTests.cs`, `WorkspaceIndexTests.cs`.
- Modify the spec (status).

Commands (repo root):
- `dotnet build Nitrogen.slnx -warnaserror`
- `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Class>"`
- `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`

`examples/DateCalc/sample.datecalc` may carry the user's uncommitted edits and is copied into some test fixtures. Before running the whole suite, `git stash push examples/DateCalc/sample.datecalc`, and `git stash pop` after. Never commit it.

**Why lockstep test input (Task 4):** with read-ahead, a test that feeds all its messages from a `MemoryStream` gets them handled in batches whose size depends on timing. Tests that assert what happens after each message (a publish per change, a file edited "between" messages) would become flaky. `LockstepInput` hands the server one message at a time, each only when the server is idle (`LspServer.Idle`), which reproduces today's one-message-at-a-time behavior deterministically. Tests of batching itself feed a plain `MemoryStream` and assert only timing-independent outcomes; the planner's rewrites are tested directly and deterministically.

---

### Task 1: `TextEdits.Apply`

**Files:**
- Create: `Nitrogen.LanguageService/TextEdits.cs`
- Test: `Nitrogen.Tests/LanguageService/TextEditsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Applying didChange content changes to a text (LSP positions: 0-based line, UTF-16 column).</summary>
public class TextEditsTests
{
    static DocumentRange R(int line, int character, int endLine, int endCharacter) =>
        new(new DocumentPosition(line, character), new DocumentPosition(endLine, endCharacter));

    [Fact]
    public void An_insert_a_delete_and_a_multi_line_replace()
    {
        Assert.Equal("let x = 41;", TextEdits.Apply("let x = 1;", [new TextChange(R(0, 8, 0, 8), "4")]));
        Assert.Equal("abc", TextEdits.Apply("abc def", [new TextChange(R(0, 3, 0, 7), "")]));
        Assert.Equal("oXree", TextEdits.Apply("one\ntwo\nthree", [new TextChange(R(0, 1, 2, 2), "X")]));
    }

    [Fact]
    public void Changes_apply_in_order_each_to_the_text_before_it_produced()
    {
        Assert.Equal("abcde", TextEdits.Apply("abc", [new TextChange(R(0, 3, 0, 3), "d"), new TextChange(R(0, 4, 0, 4), "e")]));
    }

    [Fact]
    public void A_change_without_a_range_replaces_the_whole_text()
    {
        Assert.Equal("new", TextEdits.Apply("old", [new TextChange(null, "new")]));
        Assert.Equal("abc", TextEdits.Apply("old", [new TextChange(null, "ab"), new TextChange(R(0, 2, 0, 2), "c")]));
    }

    [Fact]
    public void Columns_count_utf16_units()
    {
        Assert.Equal("a😀Xb", TextEdits.Apply("a😀b", [new TextChange(R(0, 3, 0, 3), "X")])); // 😀 is two UTF-16 units
    }

    [Fact]
    public void Positions_past_a_line_or_the_text_clamp()
    {
        Assert.Equal("ab", TextEdits.Apply("ab\ncd", [new TextChange(R(0, 5, 9, 0), "")]));
        Assert.Equal("aX\r\nb", TextEdits.Apply("a\r\nb", [new TextChange(R(0, 5, 0, 5), "X")]));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TextEditsTests"`
Expected: build error, `TextEdits` and `TextChange` don't exist.

- [ ] **Step 3: Implement**

Create `Nitrogen.LanguageService/TextEdits.cs`:

```csharp
namespace Nitrogen.LanguageService;

/// <summary>A replacement of a range of a text (LSP positions), or of the whole text when the range is null.</summary>
public sealed record TextChange(DocumentRange? Range, string Text);

/// <summary>Applies a didChange's content changes (spec: incremental sync).</summary>
public static class TextEdits
{
    /// <summary>The text after the changes, applied in order: each sees the text the previous ones produced. Positions past a line's or the text's end clamp.</summary>
    public static string Apply(string text, IReadOnlyList<TextChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Range is not { } range)
            {
                text = change.Text;
                continue;
            }
            var lines = new LineMap(text);
            int start = lines.OffsetOf(range.Start);
            int end = Math.Max(start, lines.OffsetOf(range.End));
            text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
        }
        return text;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TextEditsTests"`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/TextEdits.cs Nitrogen.Tests/LanguageService/TextEditsTests.cs
git commit -m "Apply ranged content changes to a text

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `TextOf`

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs` (after `VersionOf`)
- Test: `Nitrogen.Tests/LanguageService/IncrementalSyncTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Nitrogen.Tests/LanguageService/IncrementalSyncTests.cs`:

```csharp
using System.Text.Json;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Incremental sync, coalescing and cancellation (spec: sync coalescing).</summary>
public class IncrementalSyncTests
{
    [Fact]
    public void TextOf_gives_the_text_of_open_documents_hosts_and_unserved_files()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { }");
        service.Open("file:///w/C.cs", 1, "class C { }");
        service.Open("file:///w/x.unknown", 1, "anything");

        Assert.Equal("unit a { }", service.TextOf("file:///w/a.scopes"));
        Assert.Equal("class C { }", service.TextOf("file:///w/C.cs"));
        Assert.Equal("anything", service.TextOf("file:///w/x.unknown"));
        Assert.Null(service.TextOf("file:///w/closed.scopes"));
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~IncrementalSyncTests"`
Expected: build error, `TextOf` doesn't exist.

- [ ] **Step 3: Implement**

In `Nitrogen.LanguageService/NitrogenLanguageService.cs`, after `VersionOf`:

```csharp
    /// <summary>The current text of an open document, C# host or open file of no served language; null otherwise. Tagged strings are not documents the editor changes.</summary>
    public string? TextOf(string uri) =>
        _hosts.TryGetValue(uri, out var host) ? host.Text
        : _documents.TryGetValue(uri, out var document) && !IsEmbedded(uri) ? document.Text
        : _unserved.TryGetValue(uri, out var unserved) ? unserved.Text
        : null;
```

- [ ] **Step 4: Run it**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~IncrementalSyncTests"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.cs Nitrogen.Tests/LanguageService/IncrementalSyncTests.cs
git commit -m "Give the current text of an open document

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `LspBatch.Plan`

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs` (`TextDocumentContentChangeEvent`)
- Create: `Nitrogen.LanguageService/Lsp/LspBatch.cs`
- Test: `Nitrogen.Tests/LanguageService/LspBatchTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Planning a batch of messages read together: coalescing changes and cancelling requests.</summary>
public class LspBatchTests
{
    static JsonElement M(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static JsonElement Change(string uri, int version, string text) => M(
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"version\":" + version
        + "},\"contentChanges\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":0}},\"text\":\"" + text + "\"}]}}");

    static JsonElement Request(int id) => M("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"textDocument/hover\",\"params\":{}}");

    static JsonElement Cancel(int id) => M("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":" + id + "}}");

    [Fact]
    public void Adjacent_changes_to_one_document_merge()
    {
        var steps = LspBatch.Plan([Change("file:///a", 2, "x"), Change("file:///a", 3, "y"), Change("file:///a", 4, "z")]);
        var change = Assert.IsType<LspBatch.Change>(Assert.Single(steps));
        Assert.Equal(("file:///a", 4), (change.Uri, change.Version));
        Assert.Equal(["x", "y", "z"], change.Changes.Select(c => c.Text));
        Assert.NotNull(change.Changes[0].Range);
    }

    [Fact]
    public void Adjacent_changes_to_two_documents_give_one_step_each_in_first_change_order()
    {
        var steps = LspBatch.Plan([Change("file:///b", 2, "1"), Change("file:///a", 2, "2"), Change("file:///b", 3, "3")]);
        Assert.Equal([("file:///b", 3, "13"), ("file:///a", 2, "2")],
            steps.Cast<LspBatch.Change>().Select(c => (c.Uri, c.Version, string.Concat(c.Changes.Select(x => x.Text)))));
    }

    [Fact]
    public void A_request_between_changes_keeps_them_apart()
    {
        var steps = LspBatch.Plan([Change("file:///a", 2, "x"), Request(5), Change("file:///a", 3, "y")]);
        Assert.Collection(steps,
            s => Assert.Equal(2, Assert.IsType<LspBatch.Change>(s).Version),
            s => Assert.IsType<LspBatch.Message>(s),
            s => Assert.Equal(3, Assert.IsType<LspBatch.Change>(s).Version));
    }

    [Fact]
    public void A_request_cancelled_in_the_same_batch_is_a_cancelled_step()
    {
        var steps = LspBatch.Plan([Request(5), Request(6), Cancel(5)]);
        Assert.Collection(steps,
            s => Assert.Equal(5, Assert.IsType<LspBatch.Cancelled>(s).Id.GetInt32()),
            s => Assert.Equal(6, Assert.IsType<LspBatch.Message>(s).Element.GetProperty("id").GetInt32()));
    }

    [Fact]
    public void A_cancel_without_its_request_gives_no_steps()
    {
        Assert.Empty(LspBatch.Plan([Cancel(42)]));
    }

    [Fact]
    public void Steps_after_exit_are_dropped()
    {
        var steps = LspBatch.Plan([M("""{"jsonrpc":"2.0","method":"exit"}"""), Request(5)]);
        Assert.Equal("exit", Assert.IsType<LspBatch.Message>(Assert.Single(steps)).Element.GetProperty("method").GetString());
    }

    [Fact]
    public void A_change_that_cannot_be_read_is_a_message_step()
    {
        var steps = LspBatch.Plan([M("""{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":5}}""")]);
        Assert.IsType<LspBatch.Message>(Assert.Single(steps));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~LspBatchTests"`
Expected: build error, `LspBatch` doesn't exist.

- [ ] **Step 3: The change's range**

In `Nitrogen.LanguageService/Lsp/LspMessages.cs`, replace

```csharp
public sealed record TextDocumentContentChangeEvent(string Text);
```

with

```csharp
/// <summary>A content change: <paramref name="Text"/> replaces <paramref name="Range"/>, or the whole text when there is no range.</summary>
public sealed record TextDocumentContentChangeEvent(string Text, LspRange? Range = null);
```

- [ ] **Step 4: The planner**

Create `Nitrogen.LanguageService/Lsp/LspBatch.cs`:

```csharp
using System.Text.Json;

namespace Nitrogen.LanguageService.Lsp;

/// <summary>
/// Plans a batch of messages read together (spec: sync coalescing). A run of consecutive didChange
/// notifications becomes one change per document, in order of each document's first change, and a
/// request whose $/cancelRequest is in the batch becomes a cancelled step. Every other message is a
/// step of its own, in order; the steps after exit are dropped.
/// </summary>
internal static class LspBatch
{
    internal abstract record Step;

    /// <summary>A message handled as it is.</summary>
    internal sealed record Message(JsonElement Element) : Step;

    /// <summary>A document's content changes from consecutive didChange notifications, in order, at the last version.</summary>
    internal sealed record Change(string Uri, int Version, IReadOnlyList<TextDocumentContentChangeEvent> Changes) : Step;

    /// <summary>A request whose $/cancelRequest is in the same batch.</summary>
    internal sealed record Cancelled(JsonElement Id) : Step;

    public static IReadOnlyList<Step> Plan(IReadOnlyList<JsonElement> messages)
    {
        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
            if (MethodOf(message) == "$/cancelRequest" && message.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id))
                cancelled.Add(id.GetRawText());

        var steps = new List<Step>();
        var run = new List<Change>(); // the current run of didChange notifications, one entry per document
        foreach (var message in messages)
        {
            string method = MethodOf(message);
            if (method == "textDocument/didChange" && ChangeOf(message) is { } change)
            {
                int i = run.FindIndex(c => c.Uri == change.Uri);
                if (i < 0) run.Add(change);
                else run[i] = run[i] with { Version = change.Version, Changes = [.. run[i].Changes, .. change.Changes] };
                continue;
            }
            steps.AddRange(run);
            run.Clear();
            if (method == "$/cancelRequest") continue;
            if (method.Length > 0 && message.TryGetProperty("id", out var requestId) && cancelled.Contains(requestId.GetRawText()))
                steps.Add(new Cancelled(requestId.Clone()));
            else
                steps.Add(new Message(message));
            if (method == "exit") return steps;
        }
        steps.AddRange(run);
        return steps;
    }

    static string MethodOf(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "";

    /// <summary>The change a didChange notification makes; null when it can't be read (it is then handled, and reported, as a message).</summary>
    static Change? ChangeOf(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters)) return null;
        try
        {
            var change = parameters.Deserialize(LspJson.Default.DidChangeTextDocumentParams);
            if (change?.TextDocument?.Uri is null || change.ContentChanges is null) return null;
            return new Change(change.TextDocument.Uri, change.TextDocument.Version, change.ContentChanges);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~LspBatchTests"`
Expected: 7 passed. Then `dotnet build Nitrogen.slnx -warnaserror` (0 warnings; a nullable warning on the `is null` checks means the compiler sees them as always false: then drop that part of the check).

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspMessages.cs Nitrogen.LanguageService/Lsp/LspBatch.cs Nitrogen.Tests/LanguageService/LspBatchTests.cs
git commit -m "Plan a batch of messages: merge changes, drop cancelled requests

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Read-ahead, with lockstep test input

Behavior stays message by message in this task: the loop reads ahead into a channel and exposes `Idle`; the next task changes how a batch is handled.

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs` (`RunAsync`)
- Create: `Nitrogen.Tests/LanguageService/LockstepInput.cs`
- Modify: `Nitrogen.Tests/LanguageService/LspServerTests.cs` (`Session`), `GrammarLoopTests.cs`, `TodayValueTests.cs`, `WorkspaceIndexTests.cs`

- [ ] **Step 1: Lockstep input**

Create `Nitrogen.Tests/LanguageService/LockstepInput.cs`:

```csharp
namespace Nitrogen.Tests;

/// <summary>
/// Framed messages handed to the server one at a time, each only when the server is idle (it has
/// handled everything before and waits), as a client typing slowly sends them. An <see cref="Action"/>
/// among the items runs at such a moment, before the next message. Set <see cref="Idle"/> to the
/// server's before it runs.
/// </summary>
internal sealed class LockstepInput(params object[] items) : Stream
{
    readonly Queue<object> _items = new(items);
    MemoryStream? _current;
    bool _started;

    public WaitHandle? Idle { get; set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_current is not null)
            {
                int read = _current.Read(buffer, offset, count);
                if (read > 0) return read;
                _current = null;
            }
            if (_items.Count == 0) return 0;
            if (_started) Idle!.WaitOne();
            _started = true;
            switch (_items.Dequeue())
            {
                case Action action:
                    action();
                    break;
                case string body:
                    _current = new MemoryStream(JsonRpcConnectionTests.Frame(body));
                    break;
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 2: Drive the existing server tests with it**

1. `LspServerTests.Session`: replace its first lines up to `RunAsync` with:

```csharp
        var input = new LockstepInput(bodies);
        var output = new MemoryStream();
        var log = new StringWriter();
        var server = new LspServer(new JsonRpcConnection(input, output), service, log);
        input.Idle = server.Idle;
        int code = await server.RunAsync(CancellationToken.None);
```

2. `GrammarLoopTests` (the test building `bodies` and running with `Path.Combine(_root, "bundle")`): replace the `input` line and the server run with:

```csharp
        var input = new LockstepInput(bodies);
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null, Path.Combine(_root, "bundle"));
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
```

3. `GrammarLoopTests` (the `BlockingInput` test): replace the `BlockingInput` construction and run with:

```csharp
        var input = new LockstepInput(
            initialize, "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}", open,
            (Action)(() => File.WriteAllText(grammar, WorkspaceTests.Greet.Replace("Name:Word;", "Name:Word \"!\";"))),
            watched, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"shutdown\"}", "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null);
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
```

   Then delete the `BlockingInput` class and its doc comment.

4. `TodayValueTests.The_server_refreshes_hints_once_when_the_day_changes`: replace the `BlockingInput` construction and run with:

```csharp
        var input = new LockstepInput(
            initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            (Action)(() => clock.Advance(TimeSpan.FromHours(2))), // past midnight, while the server waits for the next message
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null);
        input.Idle = server.Idle;
        int code = await server.RunAsync(CancellationToken.None);
```

5. `WorkspaceIndexTests` (the session helper that builds `bodies` with `.. after`): replace the `input` line and the run with:

```csharp
        var input = new LockstepInput(bodies);
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null, fixedRoot);
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
```

`Broken_framing_ends_the_session` keeps its raw `MemoryStream`.

- [ ] **Step 3: See it fail**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: build error, `LspServer.Idle` doesn't exist.

- [ ] **Step 4: Read ahead**

In `Nitrogen.LanguageService/Lsp/LspServer.cs`:

1. Add `using System.Threading.Channels;` at the top.
2. Add fields after `int _refreshes;`:

```csharp
    readonly object _gate = new();            // orders the reader's enqueue against the loop's idle check
    readonly ManualResetEventSlim _idle = new(false);
    volatile Exception? _readError;           // why reading stopped early: logged when the loop reaches it
```

3. Add after the fields:

```csharp
    /// <summary>Set while the loop waits with nothing queued, so every message read so far is handled; tests feed input in lockstep with it.</summary>
    internal WaitHandle Idle => _idle.WaitHandle;
```

4. Replace `RunAsync` with:

```csharp
    /// <returns>The process exit code: 0 after <c>shutdown</c> then <c>exit</c>; 1 for <c>exit</c> without shutdown, end of input or broken framing.</returns>
    public async Task<int> RunAsync(CancellationToken cancel)
    {
        var queue = Channel.CreateUnbounded<JsonDocument>(); // not single-reader: that kind cannot count its items
        _ = Task.Run(() => ReadAllAsync(queue.Writer, cancel), CancellationToken.None);
        Task? dayChange = null;
        try
        {
            while (true)
            {
                // Armed before the wait starts, and checked first, so a day change during the wait is never missed.
                dayChange ??= DayChange(cancel);
                lock (_gate)
                    if (queue.Reader.Count == 0) _idle.Set();
                var waiting = queue.Reader.WaitToReadAsync(cancel).AsTask();
                if (dayChange is not null && await Task.WhenAny(dayChange, waiting) == dayChange && dayChange.IsCompletedSuccessfully)
                {
                    dayChange = null;
                    await SendInlayHintRefreshAsync(cancel);
                    continue;
                }
                if (!await waiting)
                {
                    if (_readError is { } error) log.WriteLine($"nitrogen lsp: {error.Message}");
                    return 1;
                }
                var batch = new List<JsonDocument>();
                while (queue.Reader.TryRead(out var message)) batch.Add(message);
                try
                {
                    if (await RunBatchAsync(batch, cancel) is int code) return code;
                }
                finally
                {
                    foreach (var message in batch) message.Dispose();
                }
            }
        }
        finally
        {
            _idle.Set(); // a lockstep reader waiting for the loop reads on to the end of its input
        }
    }

    /// <summary>Reads messages into the queue as they arrive; completes it at the end of input or when reading fails.</summary>
    async Task ReadAllAsync(ChannelWriter<JsonDocument> queue, CancellationToken cancel)
    {
        try
        {
            while (await connection.ReadAsync(cancel) is { } message)
                lock (_gate)
                {
                    _idle.Reset();
                    queue.TryWrite(message);
                }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _readError = error;
        }
        finally
        {
            queue.TryComplete();
        }
    }

    /// <returns>The exit code when the batch ends the session; null to go on.</returns>
    async Task<int?> RunBatchAsync(IReadOnlyList<JsonDocument> batch, CancellationToken cancel)
    {
        foreach (var message in batch)
            if (await HandleMessageAsync(message.RootElement, cancel) is int code) return code;
        return null;
    }

    /// <returns>The exit code for <c>exit</c>; null otherwise.</returns>
    async Task<int?> HandleMessageAsync(JsonElement root, CancellationToken cancel)
    {
        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        bool isRequest = root.TryGetProperty("id", out var idElement);
        var id = isRequest ? idElement.Clone() : default;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        if (isRequest && method.Length == 0) return null; // a response to our own request (client/registerCapability)
        if (method == "exit") return _shutdown ? 0 : 1;

        try
        {
            if (isRequest) await HandleRequestAsync(method, id, parameters, cancel);
            else
            {
                await HandleNotificationAsync(method, parameters, cancel);
                await RefreshInlayHintsAsync(cancel);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (isRequest) await RespondErrorAsync(id, error switch { RenameRefusedException => RequestFailed, JsonException => InvalidParams, _ => InternalError }, error.Message, cancel);
            else log.WriteLine($"nitrogen lsp: {method}: {error.Message}");
        }
        return null;
    }
```

5. Update the class's doc comment's first sentence: `The LSP server loop (issue 238): a reader task queues messages as they arrive, and the loop handles everything queued as a batch, in order.`

- [ ] **Step 5: Run everything**

Run: `dotnet build Nitrogen.slnx -warnaserror`, then the whole suite (with `sample.datecalc` stashed).
Expected: all pass, as before this task. A test that hangs points to a `LockstepInput` that never sees `Idle`: check it was set before `RunAsync`.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.Tests/LanguageService/LockstepInput.cs Nitrogen.Tests/LanguageService/LspServerTests.cs Nitrogen.Tests/LanguageService/GrammarLoopTests.cs Nitrogen.Tests/LanguageService/TodayValueTests.cs Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs
git commit -m "Read messages ahead into a queue

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Batches: coalescing, cancellation, incremental sync

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Modify: `Nitrogen.Tests/LanguageService/LspServerTests.cs` (sync kind)
- Test: `Nitrogen.Tests/LanguageService/IncrementalSyncTests.cs`

- [ ] **Step 1: Write the failing tests**

In `LspServerTests.A_session_initializes_publishes_diagnostics_and_shuts_down`, change the `textDocumentSync` assertion to `Assert.Equal(2, …)`.

Append to `IncrementalSyncTests`:

```csharp
    const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""";
    const string Shutdown = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""";
    const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";
    const string Doc = "file:///w/a.scopes";

    static string Open(string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Doc
        + "\",\"languageId\":\"scopes\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";

    static string Edit(int version, int line, int start, int end, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + Doc + "\",\"version\":" + version
        + "},\"contentChanges\":[{\"range\":{\"start\":{\"line\":" + line + ",\"character\":" + start + "},\"end\":{\"line\":" + line
        + ",\"character\":" + end + "}},\"text\":" + JsonSerializer.Serialize(text) + "}]}}";

    static List<JsonElement> Publishes(List<JsonElement> messages) => messages
        .Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && m.GetProperty("params").GetProperty("uri").GetString() == Doc)
        .Select(m => m.GetProperty("params")).ToList();

    /// <summary>A session over a plain stream: the server may take the messages in batches of any size.</summary>
    static async Task<List<JsonElement>> Batched(params string[] bodies)
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var input = new MemoryStream(bodies.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
        var output = new MemoryStream();
        await new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null).RunAsync(CancellationToken.None);
        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return messages;
    }

    [Fact]
    public async Task Ranged_changes_give_the_diagnostics_of_the_edited_text()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        // "unit a { let y = q; }": q (column 17) is unresolved; replace it with 1, then rename y to z.
        var (_, messages, _) = await LspServerTests.Session(service, Initialize, Open("unit a { let y = q; }"),
            Edit(2, 0, 17, 18, "1"), Edit(3, 0, 13, 14, "z"), Shutdown, Exit);

        Assert.Equal([1, 0, 0], Publishes(messages).Select(p => p.GetProperty("diagnostics").GetArrayLength()));
        Assert.Equal("unit a { let z = 1; }", service.TextOf(Doc));
    }

    [Fact]
    public async Task After_a_burst_of_changes_the_last_diagnostics_are_those_of_the_final_text()
    {
        // Twenty edits: nineteen add spaces before the closing brace, the last fixes q.
        var edits = Enumerable.Range(0, 19).Select(i => Edit(2 + i, 0, 20, 20, " ")).Append(Edit(21, 0, 17, 18, "1"));
        var messages = await Batched([Initialize, Open("unit a { let y = q; }"), .. edits, Shutdown, Exit]);

        var last = Publishes(messages)[^1];
        Assert.Equal(21, last.GetProperty("version").GetInt32());
        Assert.Equal(0, last.GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public async Task Every_request_gets_exactly_one_response()
    {
        static string Hover(int id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + Doc
            + "\"},\"position\":{\"line\":0,\"character\":13}}}";
        static string Cancel(int id) => "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":" + id + "}}";
        var messages = await Batched(Initialize, Open("unit a { let y = 1; }"), Hover(5), Cancel(5), Hover(6), Cancel(42), Shutdown, Exit);

        var responses = messages.Where(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number).ToList();
        Assert.Equal([1, 5, 6, 99], responses.Select(r => r.GetProperty("id").GetInt32()).Order());
        var five = responses.Single(r => r.GetProperty("id").GetInt32() == 5);
        Assert.True(five.TryGetProperty("result", out _)
            || five.GetProperty("error").GetProperty("code").GetInt32() == LspServer.RequestCancelled);
        Assert.True(responses.Single(r => r.GetProperty("id").GetInt32() == 6).TryGetProperty("result", out _));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~IncrementalSyncTests|FullyQualifiedName~LspServerTests"`
Expected: build error (`LspServer.RequestCancelled`); after adding only the constant, the ranged-change tests fail because a ranged change still replaces the whole text with its fragment.

- [ ] **Step 3: Handle batches**

In `Nitrogen.LanguageService/Lsp/LspServer.cs`:

1. Constants, after `RequestFailed`:

```csharp
    public const int RequestCancelled = -32800;
```

2. The initialize result: `new ServerCapabilities(1, …` becomes `new ServerCapabilities(2, …`. In `LspMessages.cs`, the `ServerCapabilities` doc comment `<param name="TextDocumentSync">1: full text on every change.</param>` becomes `<param name="TextDocumentSync">2: incremental, each change carrying the range it replaces.</param>`.

3. Replace `RunBatchAsync` and `HandleMessageAsync` (from Task 4) with:

```csharp
    /// <summary>
    /// Runs a batch's planned steps in order; the documents their changes affect get their diagnostics
    /// once, at the end, and the hint refresh check runs once.
    /// </summary>
    /// <returns>The exit code when the batch ends the session; null to go on.</returns>
    async Task<int?> RunBatchAsync(IReadOnlyList<JsonDocument> batch, CancellationToken cancel)
    {
        var affected = new List<string>();
        foreach (var step in LspBatch.Plan(batch.Select(m => m.RootElement).ToList()))
        {
            switch (step)
            {
                case LspBatch.Change change:
                    try
                    {
                        affected.AddRange(ApplyChanges(change.Uri, change.Version, change.Changes));
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        log.WriteLine($"nitrogen lsp: textDocument/didChange: {error.Message}");
                    }
                    break;
                case LspBatch.Cancelled cancelled:
                    await RespondErrorAsync(cancelled.Id, RequestCancelled, "cancelled", cancel);
                    break;
                case LspBatch.Message message:
                    var (exit, changed) = await HandleMessageAsync(message.Element, cancel);
                    if (exit is int code) return code;
                    affected.AddRange(changed);
                    break;
            }
        }
        await PublishAsync(affected.Distinct().Where(service.IsOpen).ToList(), cancel);
        await RefreshInlayHintsAsync(cancel);
        return null;
    }

    /// <returns>The exit code for <c>exit</c> (null otherwise), and the documents whose diagnostics may have changed.</returns>
    async Task<(int? Exit, IReadOnlyList<string> Affected)> HandleMessageAsync(JsonElement root, CancellationToken cancel)
    {
        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        bool isRequest = root.TryGetProperty("id", out var idElement);
        var id = isRequest ? idElement.Clone() : default;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        if (isRequest && method.Length == 0) return (null, []); // a response to our own request (client/registerCapability)
        if (method == "exit") return (_shutdown ? 0 : 1, []);

        try
        {
            if (!isRequest) return (null, await HandleNotificationAsync(method, parameters, cancel));
            await HandleRequestAsync(method, id, parameters, cancel);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (isRequest) await RespondErrorAsync(id, error switch { RenameRefusedException => RequestFailed, JsonException => InvalidParams, _ => InternalError }, error.Message, cancel);
            else log.WriteLine($"nitrogen lsp: {method}: {error.Message}");
        }
        return (null, []);
    }

    /// <summary>Applies a document's content changes, in order, to its current text; the documents whose diagnostics may have changed.</summary>
    IReadOnlyList<string> ApplyChanges(string uri, int version, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        if (service.TextOf(uri) is not { } text)
        {
            log.WriteLine($"nitrogen lsp: textDocument/didChange: '{uri}' is not open");
            return [];
        }
        var edits = changes.Select(c => new TextChange(c.Range is { } r ? new DocumentRange(Position(r.Start), Position(r.End)) : null, c.Text)).ToList();
        return service.Change(uri, version, TextEdits.Apply(text, edits));
    }
```

4. Replace `HandleNotificationAsync` with:

```csharp
    /// <returns>The documents whose diagnostics may have changed; they are published at the end of the batch.</returns>
    async Task<IReadOnlyList<string>> HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancel)
    {
        switch (method)
        {
            case "textDocument/didOpen":
            {
                var item = Params(parameters, LspJson.Default.DidOpenTextDocumentParams).TextDocument;
                return service.Open(item.Uri, item.Version, item.Text);
            }
            case "textDocument/didChange":
            {
                // Reached only when the batch plan could not read the change: reading it here reports why.
                var change = Params(parameters, LspJson.Default.DidChangeTextDocumentParams);
                return ApplyChanges(change.TextDocument.Uri, change.TextDocument.Version, change.ContentChanges);
            }
            case "textDocument/didClose":
            {
                string uri = Params(parameters, LspJson.Default.DidCloseTextDocumentParams).TextDocument.Uri;
                var others = service.Close(uri);
                await NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(uri, null, []),
                    LspJson.Default.PublishDiagnosticsParams, cancel);
                return others;
            }
            case "initialized":
            {
                var affected = new List<string>();
                if (_configRoot is not null) affected.AddRange(service.ConfigureWorkspace(_configRoot));
                if (_workspaceRoot is not null) affected.AddRange(service.IndexWorkspace(_workspaceRoot));
                _languagesSeen = service.LanguagesVersion; // the client has not asked for hints yet
                if (_watchDynamically) await RegisterWatchersAsync(service.IndexedExtensions(), cancel);
                return affected;
            }
            case "workspace/didChangeWatchedFiles":
            {
                var affected = new List<string>();
                foreach (var change in Params(parameters, LspJson.Default.DidChangeWatchedFilesParams).Changes)
                    if (System.Uri.TryCreate(change.Uri, UriKind.Absolute, out var uri) && uri.IsFile)
                        affected.AddRange(service.FileChanged(uri.LocalPath));
                return affected;
            }
            // $/cancelRequest (planned with its batch), $/setTrace and the rest: nothing to do.
            default:
                return [];
        }
    }
```

5. Update the class doc comment's diagnostics sentence to: `Diagnostics are pushed once per batch, for every open document its changes may affect.`

- [ ] **Step 4: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~IncrementalSyncTests|FullyQualifiedName~LspServerTests|FullyQualifiedName~LspBatchTests"`
Expected: all pass. Then `dotnet build Nitrogen.slnx -warnaserror` and the whole suite (stash `sample.datecalc`): all pass.

If a test that counts publishes per message fails, it is a test still fed by a plain stream: drive it with `LockstepInput` as in Task 4.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.LanguageService/Lsp/LspMessages.cs Nitrogen.Tests/LanguageService/IncrementalSyncTests.cs Nitrogen.Tests/LanguageService/LspServerTests.cs
git commit -m "Handle a batch at once: merged changes, cancelled requests, incremental sync

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Measure and document

**Files:**
- Modify: `docs/superpowers/specs/2026-10-09-sync-coalescing-design.md`

- [ ] **Step 1: Measure**

Build `dotnet build Nitrogen.Cli -c Release`, then run the probe from the design (a 2,000- and a 10,000-line DateCalc file opened through `Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll lsp` in `examples/DateCalc`; 20 `didChange` notifications sent without waiting, then `textDocument/semanticTokens/full`; time until its response; then one hover). With sync kind 2, send each change as a ranged insert of one space at the end of the text. Record the numbers for the PR. Don't commit the probe script; keep it in the scratchpad.

- [ ] **Step 2: Spec status**

Change `Status: approved (2026-10-09).` to `Status: implemented (2026-10-09).`, and add the measured numbers under the table in Problem as a sentence: `After: 20 quick changes, then semanticTokens/full, answered after X s (2,000 lines) and Y s (10,000 lines).`

- [ ] **Step 3: Verify and commit**

Run `dotnet build Nitrogen.slnx -warnaserror`, the whole suite (stash `sample.datecalc`), and `python3 eng/check-links.py`.

```bash
git add docs/superpowers/specs/2026-10-09-sync-coalescing-design.md
git commit -m "Record the measured effect of coalescing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
