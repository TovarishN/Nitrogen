# Closed-File Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the diagnostics of closed workspace files and grammars, re-checked only while the server is idle.

**Architecture:** The service answers `Diagnostics(uri)` for closed indexed documents and closed grammars, and lists them (`ClosedDiagnosticFiles`). The LSP server runs an interruptible pass over that list between batches: one file at a time while the queue is empty, publishing only when a file's diagnostics changed. A batch touching documents restarts the pass; files that left the list get one empty publish.

**Tech Stack:** C# / .NET 10, xUnit, the repo's LSP server.

Spec: `docs/superpowers/specs/2026-10-09-closed-file-diagnostics-design.md`.

---

## Files

- Modify `Nitrogen.LanguageService/NitrogenLanguageService.cs`: `Diagnostics` for closed documents and grammars.
- Modify `Nitrogen.LanguageService/WorkspaceIndex.cs`: `ClosedDiagnosticFiles`.
- Modify `Nitrogen.LanguageService/Lsp/LspServer.cs`: the idle pass.
- Create `Nitrogen.Tests/LanguageService/ClosedDiagnosticsTests.cs`.
- Modify `docs/editor-support.md`, the spec (status).

Commands (repo root): `dotnet build Nitrogen.slnx -warnaserror`; `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ClosedDiagnosticsTests"`; the whole suite with `examples/DateCalc/sample.datecalc` stashed (`git stash push examples/DateCalc/sample.datecalc`, then `git stash pop`; never commit it).

The tests use the `links` language of `WorkspaceIndexTests` (`def x;` declares an item, `use x;` references one; an unresolved `use` is `NB0001`).

---

### Task 1: Service

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs` (`Diagnostics`)
- Modify: `Nitrogen.LanguageService/WorkspaceIndex.cs`
- Test: `Nitrogen.Tests/LanguageService/ClosedDiagnosticsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Diagnostics of closed workspace files and grammars, checked while the server is idle.</summary>
public sealed class ClosedDiagnosticsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-closed-").FullName;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(string grammar = WorkspaceIndexTests.Grammar)
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", grammar);
        var service = new NitrogenLanguageService(new LanguageRegistry());
        service.ConfigureWorkspace(_root);
        service.IndexWorkspace(_root);
        return service;
    }

    [Fact]
    public void A_closed_file_reports_its_errors()
    {
        Write("a.links", "use alpha;");
        using var service = Service();
        Assert.Contains(service.Diagnostics(Uri("a.links")), d => d.Code == "NB0001");
    }

    [Fact]
    public void A_closed_grammar_reports_its_compile_errors()
    {
        using var service = Service(WorkspaceIndexTests.Grammar.Replace("token Word = ['a'..'z']+;", "token Word = ;"));
        Assert.Contains(service.Diagnostics(Uri("links.ngr")), d => d.Severity == ServiceSeverity.Error);
        Assert.Contains(Uri("links.ngr"), service.ClosedDiagnosticFiles());
    }

    [Fact]
    public void Closed_diagnostic_files_are_the_closed_files_and_grammars()
    {
        Write("a.links", "def alpha;");
        Write("b.links", "def beta;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "def beta;");
        Assert.Equal([Uri("a.links"), Uri("links.ngr")], service.ClosedDiagnosticFiles());
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ClosedDiagnosticsTests"`
Expected: build error, `ClosedDiagnosticFiles` doesn't exist.

- [ ] **Step 3: `Diagnostics` for closed documents and grammars**

In `Nitrogen.LanguageService/NitrogenLanguageService.cs`, `Diagnostics`, replace

```csharp
        if (!_documents.TryGetValue(uri, out var document)) return [];
```

with

```csharp
        // A closed indexed file is checked like an open one; any other closed file has only its grammar compile diagnostics, if it is a grammar.
        if (!TryDocument(uri, out var document)) return GrammarDiagnostics(uri).ToList();
```

Also change the method's doc: add above it

```csharp
    /// <summary>The diagnostics of an open document, C# host or closed indexed file, or the compile diagnostics of a grammar; empty for any other file.</summary>
```

- [ ] **Step 4: `ClosedDiagnosticFiles`**

In `Nitrogen.LanguageService/WorkspaceIndex.cs`, after `IndexedExtensions`:

```csharp
    /// <summary>The files whose diagnostics are published while closed: the closed indexed files and the workspace languages' grammars, minus the open documents; by URI.</summary>
    public IReadOnlyList<string> ClosedDiagnosticFiles() => _closed.Keys
        .Concat(_grammarLanguages.SelectMany(l => l.Files()).Select(path => new Uri(path).AbsoluteUri))
        .Where(uri => !IsOpen(uri))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ClosedDiagnosticsTests"`
Expected: 3 passed.

If `A_closed_file_reports_its_errors` throws from a later check (semantic, lowering or assist) that assumes an open document, look up what that check reads (`_documents[uri]`, a per-open-document cache) and make it read `TryDocument`/`DocumentAt` instead; the closed document has the same shape.

- [ ] **Step 6: Run everything and commit**

Run `dotnet build Nitrogen.slnx -warnaserror` and the whole suite (stashed). Expected: all pass.

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.cs Nitrogen.LanguageService/WorkspaceIndex.cs Nitrogen.Tests/LanguageService/ClosedDiagnosticsTests.cs
git commit -m "Check closed workspace files and grammars

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: The idle pass

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/ClosedDiagnosticsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `ClosedDiagnosticsTests`:

```csharp
    const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";

    static string Open(string uri, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri
        + "\",\"languageId\":\"links\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";

    static string Change(string uri, int version, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"version\":" + version
        + "},\"contentChanges\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]}}";

    static string Close(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"}}}";

    static string Hover(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"},\"position\":{\"line\":0,\"character\":4}}}";

    static string Deleted(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{\"changes\":[{\"uri\":\"" + uri + "\",\"type\":3}]}}";

    /// <summary>A session in lockstep (each item once the server is idle): initialize, initialized, the items, shutdown, exit.</summary>
    async Task<List<JsonElement>> Session(params object[] items)
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", WorkspaceIndexTests.Grammar);
        using var service = new NitrogenLanguageService(new LanguageRegistry());
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri + "\",\"capabilities\":{}}}";
        var input = new LockstepInput([initialize, Initialized, .. items,
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}"""]);
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null);
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return messages;
    }

    /// <summary>The publishes for one file, in order: its version (null when closed) and how many diagnostics.</summary>
    static List<(int? Version, int Count)> Publishes(List<JsonElement> messages, string uri) => messages
        .Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && m.GetProperty("params").GetProperty("uri").GetString() == uri)
        .Select(m => m.GetProperty("params"))
        .Select(p => (p.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null,
            p.GetProperty("diagnostics").GetArrayLength()))
        .ToList();

    [Fact]
    public async Task A_closed_file_with_an_error_is_published_after_initialization()
    {
        Write("a.links", "use alpha;");
        var messages = await Session();
        Assert.Equal(new (int?, int)[] { (null, 1) }, Publishes(messages, Uri("a.links")));
        Assert.Empty(Publishes(messages, Uri("links.ngr"))); // clean, never published: nothing sent
    }

    [Fact]
    public async Task Declaring_the_name_in_an_open_file_clears_the_closed_one()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("b.links"), "def beta;"), Change(Uri("b.links"), 2, "def alpha;"));
        Assert.Equal(new (int?, int)[] { (null, 1), (null, 0) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task A_batch_that_changes_nothing_publishes_nothing_for_closed_files()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("b.links"), "def beta;"), Hover(Uri("b.links")));
        Assert.Equal(new (int?, int)[] { (null, 1) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task A_deleted_closed_file_is_published_empty()
    {
        string path = Write("a.links", "use alpha;");
        var messages = await Session((Action)(() => File.Delete(path)), Deleted(Uri("a.links")));
        Assert.Equal(new (int?, int)[] { (null, 1), (null, 0) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task An_opened_closed_file_is_published_with_its_version_and_not_as_closed()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("a.links"), "use alpha;"), Change(Uri("a.links"), 2, "def alpha;\nuse alpha;"));
        Assert.Equal(new (int?, int)[] { (null, 1), (1, 1), (2, 0) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task A_closed_file_is_cleared_then_published_again_as_closed()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("a.links"), "use alpha;"), Close(Uri("a.links")));
        Assert.Equal(new (int?, int)[] { (null, 1), (1, 1), (null, 0), (null, 1) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task After_a_burst_the_last_publish_of_a_closed_file_is_its_final_state()
    {
        Write("a.links", "use alpha;");
        string b = Uri("b.links");
        var messages = await Session((object)new[] { Open(b, "def beta;"), Change(b, 2, "def gamma;"), Change(b, 3, "def alpha;") }); // one burst
        Assert.Equal(((int?)null, 0), Publishes(messages, Uri("a.links"))[^1]);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ClosedDiagnosticsTests"`
Expected: the seven server tests fail (no publishes for `a.links`, or only the open path's), except `A_batch_that_changes_nothing…` and similar ones may fail on the missing first `(null, 1)`.

- [ ] **Step 3: The pass**

In `Nitrogen.LanguageService/Lsp/LspServer.cs`:

1. Fields, after `volatile Exception? _readError;`:

```csharp
    // The idle pass over closed files (spec: closed-file diagnostics).
    List<string> _closedFiles = [];
    int _closedNext;
    bool _closedStale;
    readonly Dictionary<string, IReadOnlyList<ServiceDiagnostic>> _closedPublished = new(StringComparer.Ordinal);

    /// <summary>Notifications that may change any document's diagnostics: a batch holding one restarts the pass.</summary>
    static readonly HashSet<string> s_affecting = new(StringComparer.Ordinal)
    {
        "initialized", "textDocument/didOpen", "textDocument/didChange", "textDocument/didClose", "workspace/didChangeWatchedFiles",
    };
```

2. In `RunAsync`, replace

```csharp
                lock (_gate)
                    if (queue.Reader.Count == 0) _idle.Set();
```

with

```csharp
                await CheckClosedFilesAsync(queue.Reader, cancel);
                lock (_gate)
                    if (queue.Reader.Count == 0 && _closedNext >= _closedFiles.Count) _idle.Set();
```

3. In `RunBatchAsync`, mark the pass stale: in `case LspBatch.Change change:` add `_closedStale = true;` as its first statement, and in `case LspBatch.Message message:` add before `var (exit, changed) = …`:

```csharp
                    if (message.Element.TryGetProperty("method", out var method) && s_affecting.Contains(method.GetString() ?? "")) _closedStale = true;
```

4. In `HandleNotificationAsync`, `case "textDocument/didClose":`, after `var others = service.Close(uri);` add:

```csharp
                _closedPublished.Remove(uri); // cleared below; the idle pass publishes it again if it has errors
```

5. Extract the LSP conversion in `PublishAsync`: replace its body's `var diagnostics = service.Diagnostics(uri).Select(…).ToArray();` with `var diagnostics = Lsp(service.Diagnostics(uri));`, and add:

```csharp
    static LspDiagnostic[] Lsp(IEnumerable<ServiceDiagnostic> diagnostics) => diagnostics
        .Select(d => new LspDiagnostic(Range(d.Range), (int)d.Severity, d.Code, "nitrogen", d.Message))
        .ToArray();

    /// <summary>
    /// Checks closed files while nothing is queued: one at a time, publishing a file's diagnostics when
    /// they differ from what it last got (never published counts as empty). A stale pass restarts from
    /// the current list, first clearing the files that left it and aren't open.
    /// </summary>
    async Task CheckClosedFilesAsync(ChannelReader<JsonDocument> queue, CancellationToken cancel)
    {
        if (_closedStale)
        {
            _closedStale = false;
            _closedFiles = service.ClosedDiagnosticFiles().ToList();
            _closedNext = 0;
            var current = new HashSet<string>(_closedFiles, StringComparer.Ordinal);
            foreach (string gone in _closedPublished.Keys.Where(uri => !current.Contains(uri)).ToList())
            {
                _closedPublished.Remove(gone);
                if (!service.IsOpen(gone))
                    await NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(gone, null, []),
                        LspJson.Default.PublishDiagnosticsParams, cancel);
            }
        }
        while (_closedNext < _closedFiles.Count && queue.Count == 0)
        {
            string uri = _closedFiles[_closedNext++];
            if (service.IsOpen(uri)) continue;
            IReadOnlyList<ServiceDiagnostic> diagnostics;
            try
            {
                diagnostics = service.Diagnostics(uri);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.WriteLine($"nitrogen lsp: diagnostics of {uri}: {error.Message}");
                continue;
            }
            var last = _closedPublished.GetValueOrDefault(uri) ?? [];
            if (last.SequenceEqual(diagnostics)) continue;
            if (diagnostics.Count == 0) _closedPublished.Remove(uri);
            else _closedPublished[uri] = diagnostics;
            await NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(uri, null, Lsp(diagnostics)),
                LspJson.Default.PublishDiagnosticsParams, cancel);
        }
    }
```

   Note the removal when a file becomes clean: it keeps `_closedPublished` to the files currently showing errors, so a file that leaves the list while clean isn't cleared again.

6. Class doc comment: after `Diagnostics are pushed once per batch, for every open document its changes may affect.` add ` Between batches, while nothing is queued, closed workspace files and grammars are checked one at a time and published when their diagnostics change.`

- [ ] **Step 4: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ClosedDiagnosticsTests"`
Expected: 10 passed.

- [ ] **Step 5: Run everything**

Run `dotnet build Nitrogen.slnx -warnaserror` and the whole suite (stashed), then the server-driving tests five times:

```bash
for i in 1 2 3 4 5; do dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build --blame-hang-timeout 60s --filter "FullyQualifiedName~Lsp|FullyQualifiedName~GrammarLoop|FullyQualifiedName~TodayValue|FullyQualifiedName~WorkspaceIndex|FullyQualifiedName~InlayHint|FullyQualifiedName~IncrementalSync|FullyQualifiedName~ClosedDiagnostics" | grep -E "Passed!|Failed!|Failed "; done
```

Expected: all pass every time. An existing test that now sees extra publishes counts messages for every URI: make it count only the URI it checks.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.Tests/LanguageService/ClosedDiagnosticsTests.cs
git commit -m "Publish closed files' diagnostics while the server is idle

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Docs

**Files:**
- Modify: `docs/editor-support.md`
- Modify: `docs/superpowers/specs/2026-10-09-closed-file-diagnostics-design.md`

- [ ] **Step 1: Feature matrix and workspace paragraph**

In `docs/editor-support.md`:

1. After the `| Diagnostics | ✓ | ✓ | ✓ |` row:

```markdown
| Diagnostics in closed files | ✓ (Problems panel) | — (closed C# files aren't read) | — |
```

   The Rider column of language files stays as VS Code's until checked by hand (Step 2).

2. In the paragraph about closed files, replace `Diagnostics are reported for open files.` with `Their diagnostics, and those of the language's grammars, are reported too: checked while the server is idle, and published when they change.`

- [ ] **Step 2: Rider, by hand**

Ask the user to open a workspace with a closed file holding an error in Rider (built plugin from `editors/rider`) and say whether the error appears without opening the file (Problems tool window). If it doesn't, change the row's first cell to `✓ in VS Code (Problems panel); Rider shows them once the file is opened`. Until the user answers, keep the PR's test plan item unticked.

- [ ] **Step 3: Spec status**

Change `Status: approved (2026-10-09).` to `Status: implemented (2026-10-09).`

- [ ] **Step 4: Verify and commit**

Run `dotnet build Nitrogen.slnx -warnaserror`, the whole suite (stashed), and `python3 eng/check-links.py`.

```bash
git add docs/editor-support.md docs/superpowers/specs/2026-10-09-closed-file-diagnostics-design.md
git commit -m "Document diagnostics in closed files

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
