# Workspace Indexing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The language service binds a workspace grammar language's files on disk as well as the open ones, so references into closed files resolve in the editor.

**Architecture:** `NitrogenLanguageService` keeps closed documents read from disk in the same per-language `Project` as open ones. `IndexWorkspace` loads them; open/close and `FileChanged` keep them current; location features look in both maps. `LspServer` separates the client's workspace root (indexed) from the `--config` root (languages), ignores client responses, and asks clients that support it to watch the indexed extensions.

**Tech Stack:** .NET 10 / C#, xUnit, LSP 3.17 (`client/registerCapability`, `workspace/didChangeWatchedFiles`).

**Spec:** [2026-09-30-workspace-indexing-design.md](../specs/2026-09-30-workspace-indexing-design.md)

---

## Background for the implementer

- Work in `../Nitrogen-indexing` (branch `workspace-indexing`, from `origin/main` at 22d96e6), checked out with LF.
- Build/test: `dotnet build Nitrogen.slnx -warnaserror`; `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj [--filter FullyQualifiedName~Name]`.
- `NitrogenLanguageService` is a partial class: `NitrogenLanguageService.cs` (documents, features), `GrammarLanguages.cs` (`nitrogen.json` languages: `ConfigureWorkspace`, `FileChanged`, `LoadConfiguration`, `Compile`, `Reregister`, `Unregister`). `_documents` holds open documents; `_projects[entry]` is a language's `Project`, keyed by URI; `Registry.TryFind(uri, out entry, out start)` maps a URI's extension to its language.
- Workspace grammar languages are `_grammarLanguages`; each has `Extensions` and, once compiled, `Entry`.
- `LspServer` (`Nitrogen.LanguageService/Lsp/LspServer.cs`) handles one message at a time; protocol records and the `LspJson` source-generated context are in `Lsp/LspMessages.cs` (camelCase, nulls omitted).

## File structure

| Path | Change |
| --- | --- |
| `Nitrogen.LanguageService/NitrogenLanguageService.cs` | `_closed` map, lookup for location features, open/close hand-off. |
| `Nitrogen.LanguageService/WorkspaceIndex.cs` (new) | `IndexWorkspace`, `Reindex`, `LoadClosed`, `RemoveClosed`, scanning rules, `IndexedExtensions`. |
| `Nitrogen.LanguageService/GrammarLanguages.cs` | `FileChanged` for indexed files; re-index after configuration; move closed documents on re-register/unregister. |
| `Nitrogen.LanguageService/Lsp/LspMessages.cs` | Client capabilities on `InitializeParams`. |
| `Nitrogen.LanguageService/Lsp/LspServer.cs` | Workspace vs configuration root, ignore responses, register watchers. |
| `Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs` (new) | Service and protocol tests. |

---

### Task 1: Closed documents and indexing in the service

**Files:**
- Create: `Nitrogen.LanguageService/WorkspaceIndex.cs`, `Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs`
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs`, `Nitrogen.LanguageService/GrammarLanguages.cs`

- [ ] **Step 1: Write the failing tests**

`Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs`:

```csharp
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Workspace files of a nitrogen.json language are bound while closed (spec: workspace indexing).</summary>
public sealed class WorkspaceIndexTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-index-").FullName;

    internal const string Config = """{ "languages": [ { "name": "links", "extensions": [".links"], "grammars": ["links.ngr"], "start": "Links.File" } ] }""";

    internal const string Grammar = """
        syntax module Links
        {
          symbols { item }
          token Word = ['a'..'z']+;
          syntax File = Lines:Line*;
          syntax Line = Decl / Use;
          syntax Decl = "def" Name:Word ";" declares item Name export;
          syntax Use  = "use" Target:Word ";" references item Target;
        }
        """;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(bool index = true)
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var service = new NitrogenLanguageService(new LanguageRegistry());
        service.ConfigureWorkspace(_root);
        if (index) service.IndexWorkspace(_root);
        return service;
    }

    static IReadOnlyList<string> Codes(NitrogenLanguageService service, string uri) => service.Diagnostics(uri).Select(d => d.Code).ToList();

    [Fact]
    public void Without_indexing_a_closed_declaration_is_unresolved()
    {
        Write("a.links", "def alpha;");
        using var service = Service(index: false);
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));
    }

    [Fact]
    public void A_closed_declaration_resolves()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Empty(Codes(service, Uri("b.links")));
        Assert.False(service.IsOpen(Uri("a.links")));
    }

    [Fact]
    public void Definition_references_and_rename_reach_the_closed_file()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        var at = new DocumentPosition(0, 5);

        var definition = Assert.Single(service.Definition(Uri("b.links"), at));
        Assert.Equal((Uri("a.links"), 0, 4, 9), (definition.Uri, definition.Range.Start.Line, definition.Range.Start.Character, definition.Range.End.Character));
        Assert.Contains(service.References(Uri("b.links"), at, includeDeclaration: true), l => l.Uri == Uri("a.links"));
        var edits = service.Rename(Uri("b.links"), at, "beta");
        Assert.Equal(new[] { Uri("a.links"), Uri("b.links") }, edits.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Closing_an_edited_file_goes_back_to_its_disk_text()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        service.Open(Uri("a.links"), 1, "def gamma;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));

        Assert.Contains(Uri("b.links"), service.Close(Uri("a.links")));
        Assert.Empty(Codes(service, Uri("b.links")));
    }

    [Fact]
    public void Files_created_changed_and_deleted_on_disk_follow()
    {
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));

        string a = Write("a.links", "def alpha;");
        Assert.Contains(Uri("b.links"), service.FileChanged(a));
        Assert.Empty(Codes(service, Uri("b.links")));

        File.WriteAllText(a, "def gamma;");
        service.FileChanged(a);
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));

        File.WriteAllText(a, "def alpha;");
        service.FileChanged(a);
        File.Delete(a);
        Assert.Contains(Uri("b.links"), service.FileChanged(a));
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));
    }

    [Theory]
    [InlineData("bin/a.links")]
    [InlineData("obj/a.links")]
    [InlineData("node_modules/a.links")]
    [InlineData(".git/a.links")]
    [InlineData("a.txt")]
    public void Build_output_hidden_folders_and_other_extensions_are_not_indexed(string path)
    {
        Write(path, "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));
    }

    [Fact]
    public void A_recompiled_grammar_keeps_closed_files_bound()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");

        string grammar = Write("links.ngr", Grammar + "\n// edited\n");
        service.FileChanged(grammar);

        Assert.Empty(Codes(service, Uri("b.links")));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~WorkspaceIndexTests`
Expected: compile error — `IndexWorkspace` does not exist.

- [ ] **Step 3: Implement the index**

`Nitrogen.LanguageService/WorkspaceIndex.cs`:

```csharp
namespace Nitrogen.LanguageService;

/// <summary>
/// Closed workspace files of nitrogen.json languages (spec: workspace indexing): read from disk and bound
/// in their language's project beside the open documents, so references into them resolve.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    const int MaxIndexedFiles = 10_000;
    const long MaxIndexedFileBytes = 1 << 20;

    static readonly HashSet<string> s_skippedDirectories = new(StringComparer.Ordinal) { "bin", "obj", "node_modules" };

    readonly Dictionary<string, Document> _closed = new(StringComparer.Ordinal);
    string? _workspaceRoot;

    /// <summary>Binds the files under <paramref name="root"/> of the workspace grammar languages; the open documents that may change.</summary>
    public IReadOnlyList<string> IndexWorkspace(string root)
    {
        _workspaceRoot = Path.GetFullPath(root);
        return Reindex();
    }

    /// <summary>The file extensions indexed now, for clients that watch files for the server.</summary>
    public IReadOnlyList<string> IndexedExtensions() => _grammarLanguages
        .Where(l => l.Entry is not null).SelectMany(l => l.Extensions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>Drops every closed document and reads the workspace again.</summary>
    IReadOnlyList<string> Reindex()
    {
        foreach (var uri in _closed.Keys.ToList()) RemoveClosed(uri);
        if (_workspaceRoot is null) return [];
        var languages = new HashSet<LanguageEntry>();
        int count = 0;
        foreach (string path in WorkspaceFiles(_workspaceRoot))
        {
            if (count == MaxIndexedFiles) break;
            if (LoadClosed(path) is { } language)
            {
                languages.Add(language);
                count++;
            }
        }
        return languages.SelectMany(OpenDocuments).Distinct().ToList();
    }

    static IEnumerable<string> WorkspaceFiles(string directory)
    {
        string[] files, directories;
        try
        {
            files = Directory.GetFiles(directory);
            directories = Directory.GetDirectories(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string file in files.Order(StringComparer.Ordinal)) yield return file;
        foreach (string child in directories.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || s_skippedDirectories.Contains(name)) continue;
            foreach (string file in WorkspaceFiles(child)) yield return file;
        }
    }

    /// <summary>The language of an indexed file, or null for a file this index does not keep.</summary>
    LanguageEntry? IndexedLanguage(string uri, out Rule start)
    {
        start = default;
        if (!Registry.TryFind(uri, out var language, out start)) return null;
        return _grammarLanguages.Any(l => l.Entry == language) ? language : null;
    }

    bool InWorkspace(string path) =>
        _workspaceRoot is not null && Path.GetFullPath(path).StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Reads <paramref name="path"/> into the index unless it is open, too big, unreadable, or of no indexed language; its language when read.</summary>
    LanguageEntry? LoadClosed(string path)
    {
        string uri = new Uri(path).AbsoluteUri;
        if (_documents.ContainsKey(uri) || IndexedLanguage(uri, out var start) is not { } language) return null;
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxIndexedFileBytes) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var document = new Document(uri, 0, text, language, start);
        if (!_projects.TryGetValue(language, out var project)) _projects[language] = project = new Project(language.Language);
        project.Set(uri, document.Parsed.Tree);
        if (_closed.Remove(uri, out var previous)) previous.Dispose();
        _closed[uri] = document;
        _inspection.Remove(uri);
        return language;
    }

    /// <summary>Removes a closed document from the index and its project; its language, or null when it was not indexed.</summary>
    LanguageEntry? RemoveClosed(string uri)
    {
        if (!_closed.Remove(uri, out var document)) return null;
        if (_projects.TryGetValue(document.Language, out var project)) project.Remove(uri);
        document.Dispose();
        return document.Language;
    }

    /// <summary>An open document, else a closed one.</summary>
    bool TryDocument(string uri, out Document document) =>
        _documents.TryGetValue(uri, out document!) || _closed.TryGetValue(uri, out document!);

    Document DocumentAt(string uri) => TryDocument(uri, out var document) ? document : throw new KeyNotFoundException(uri);

    void DisposeClosed()
    {
        foreach (var document in _closed.Values) document.Dispose();
        _closed.Clear();
    }
}
```

In `Nitrogen.LanguageService/NitrogenLanguageService.cs`:

- In `Update`, after `_documents[uri] = document;`, add:

```csharp
        if (_closed.Remove(uri, out var closed)) closed.Dispose(); // the editor's text now stands for the file
```

- Replace `Close` with:

```csharp
    /// <returns>The other open documents of the closed document's language, and a grammar's samples when a grammar closes.</returns>
    public IReadOnlyList<string> Close(string uri)
    {
        _inspection.Remove(uri);
        _unserved.Remove(uri);
        var affected = new List<string>();
        if (_documents.Remove(uri, out var document))
        {
            _projects[document.Language].Remove(uri);
            document.Dispose();
            // An indexed workspace file goes back to its text on disk.
            if (System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile && InWorkspace(parsed.LocalPath) && File.Exists(parsed.LocalPath))
                LoadClosed(parsed.LocalPath);
            affected.AddRange(OpenDocuments(document.Language));
        }
        affected.AddRange(GrammarHook(uri)); // a closed grammar goes back to its text on disk
        return affected.Distinct().ToList();
    }
```

- Replace the two `LocationOf` methods, and use `DocumentAt` in `Rename`'s `Edit` and in `CheckName`:

```csharp
    DocumentLocation LocationOf(Symbol symbol) => new(symbol.Path!, DocumentAt(symbol.Path!).Lines.RangeOf(symbol.NameSpan));

    DocumentLocation LocationOf(Reference reference) => new(reference.Path, DocumentAt(reference.Path).Lines.RangeOf(reference.NameSpan));
```

```csharp
            var range = DocumentAt(path).Lines.RangeOf(span);
```

```csharp
        var document = DocumentAt(symbol.Path!);
```

- In `Dispose`, after `_documents.Clear();`, add `DisposeClosed();`.

In `Nitrogen.LanguageService/GrammarLanguages.cs`:

- In `FileChanged`, before the final `return`, add:

```csharp
        string uri = new Uri(path).AbsoluteUri;
        if (InWorkspace(path) && !_documents.ContainsKey(uri) && IndexedLanguage(uri, out _) is not null)
        {
            var language = File.Exists(path) ? LoadClosed(path) : RemoveClosed(uri);
            return language is null ? [] : OpenDocuments(language);
        }
```

and change the final line to reuse `uri`: `return _documents.ContainsKey(uri) ? [] : GrammarHookPath(path);`

- At the end of `LoadConfiguration`, change `return affected.Distinct().ToList();` to:

```csharp
        affected.AddRange(Reindex()); // the languages may have changed
        return affected.Distinct().ToList();
```

- In `Reregister`, after the loop that re-creates open documents in the new `project` and before disposing `previous`, move closed documents:

```csharp
        foreach (var closed in _closed.Values.Where(d => d.Language == old).ToList())
        {
            Registry.TryFind(closed.Uri, out _, out var closedStart);
            var moved = new Document(closed.Uri, 0, closed.Text, replacement, closedStart);
            project.Set(closed.Uri, moved.Parsed.Tree);
            _closed[closed.Uri] = moved;
            closed.Dispose();
        }
```

(Guard with `if (old is not null)`; when `old` is null the language is new and `Reindex` in `LoadConfiguration` loads its files.)

- In `Unregister`, before `_projects.Remove(entry);`, add:

```csharp
        foreach (var closed in _closed.Values.Where(d => d.Language == entry).ToList()) RemoveClosed(closed.Uri);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceIndexTests|FullyQualifiedName~LanguageService"`
Expected: PASS. If `A_recompiled_grammar_keeps_closed_files_bound` fails because a closed grammar change on disk reaches `Compile` without `Reregister` running, trace `GrammarHookPath` → `Compile` → `Reregister` and make sure the closed-document move runs for that path.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs
git commit -m "Bind closed workspace files of nitrogen.json languages"
```

---

### Task 2: Protocol — workspace root, responses, watchers

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `WorkspaceIndexTests` (add `using System.Text.Json;`, `using Nitrogen.Cli;`, and `using Nitrogen.LanguageService.Lsp;`):

```csharp
    async Task<List<JsonElement>> Session(string? fixedRoot, string workspace, string capabilities, params string[] after)
    {
        string[] bodies =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(workspace).AbsoluteUri + "\",\"capabilities\":" + capabilities + "}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}",
            .. after,
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"shutdown\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
        ];
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        var input = new MemoryStream(bodies.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
        var output = new MemoryStream();
        await new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null, fixedRoot).RunAsync(CancellationToken.None);
        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return messages;
    }

    string Open(string relative, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri(relative) + "\",\"languageId\":\"links\",\"version\":1,\"text\":\"" + text + "\"}}}";

    static int LastDiagnosticCount(List<JsonElement> messages, string uri) => messages
        .Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && m.GetProperty("params").GetProperty("uri").GetString() == uri)
        .Select(m => m.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).Last();

    [Fact]
    public async Task With_a_fixed_config_the_client_workspace_is_still_indexed()
    {
        Write("bundle/nitrogen.json", Config);
        Write("bundle/links.ngr", Grammar);
        Write("workspace/a.links", "def alpha;");
        var messages = await Session(Path.Combine(_root, "bundle"), Path.Combine(_root, "workspace"), "{}", Open("workspace/b.links", "use alpha;"));
        Assert.Equal(0, LastDiagnosticCount(messages, Uri("workspace/b.links")));
    }

    [Fact]
    public async Task A_client_with_dynamic_registration_is_asked_to_watch_the_indexed_extensions()
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var messages = await Session(null, _root, """{"workspace":{"didChangeWatchedFiles":{"dynamicRegistration":true}}}""");
        var request = Assert.Single(messages, m => m.TryGetProperty("method", out var method) && method.GetString() == "client/registerCapability");
        var registration = request.GetProperty("params").GetProperty("registrations")[0];
        Assert.Equal("workspace/didChangeWatchedFiles", registration.GetProperty("method").GetString());
        Assert.Equal(new[] { "**/*.links", "**/nitrogen.json" },
            registration.GetProperty("registerOptions").GetProperty("watchers").EnumerateArray().Select(w => w.GetProperty("globPattern").GetString()));
    }

    [Fact]
    public async Task A_client_without_dynamic_registration_is_not_asked()
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var messages = await Session(null, _root, "{}");
        Assert.DoesNotContain(messages, m => m.TryGetProperty("method", out var method) && method.GetString() == "client/registerCapability");
    }

    [Fact]
    public async Task A_response_from_the_client_gets_no_reply()
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var messages = await Session(null, _root, "{}", """{"jsonrpc":"2.0","id":"nitrogen-watch","result":null}""");
        Assert.DoesNotContain(messages, m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "nitrogen-watch");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~WorkspaceIndexTests`
Expected: the four new tests fail (fixed root ignores the client root and nothing is indexed; no registration; the response is answered with `MethodNotFound`).

- [ ] **Step 3: Implement**

`Nitrogen.LanguageService/Lsp/LspMessages.cs` — replace `InitializeParams` and add the capability records:

```csharp
public sealed record DidChangeWatchedFilesClientCapabilities(bool? DynamicRegistration);

public sealed record WorkspaceClientCapabilities(DidChangeWatchedFilesClientCapabilities? DidChangeWatchedFiles);

public sealed record ClientCapabilities(WorkspaceClientCapabilities? Workspace);

public sealed record InitializeParams(string? RootUri, WorkspaceFolder[]? WorkspaceFolders, ClientCapabilities? Capabilities = null);
```

`Nitrogen.LanguageService/Lsp/LspServer.cs`:

- Replace the field `string? _root;` with:

```csharp
    string? _workspaceRoot;   // the client's folder: its files are indexed
    string? _configRoot;      // whose nitrogen.json configures the languages: --config's directory, else the workspace
    bool _watchDynamically;
```

- In `RunAsync`, right after `var parameters = …;`, add:

```csharp
                if (isRequest && method.Length == 0) continue; // a response to our own request (client/registerCapability)
```

- Replace the `initialize` handling of the root with:

```csharp
            case "initialize":
                if (parameters.ValueKind == JsonValueKind.Object)
                {
                    var initialize = parameters.Deserialize(LspJson.Default.InitializeParams);
                    string? root = initialize?.RootUri ?? initialize?.WorkspaceFolders?.FirstOrDefault()?.Uri;
                    if (root is not null && System.Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.IsFile) _workspaceRoot = uri.LocalPath;
                    _watchDynamically = initialize?.Capabilities?.Workspace?.DidChangeWatchedFiles?.DynamicRegistration == true;
                }
                _configRoot = fixedRoot ?? _workspaceRoot;
```

(keep the rest of the case — the capabilities response — as it is).

- Replace the `initialized` case with:

```csharp
            case "initialized":
                if (_configRoot is not null) await PublishAsync(service.ConfigureWorkspace(_configRoot), cancel);
                if (_workspaceRoot is not null) await PublishAsync(service.IndexWorkspace(_workspaceRoot), cancel);
                if (_watchDynamically) await RegisterWatchersAsync(service.IndexedExtensions(), cancel);
                break;
```

- Add the request writer:

```csharp
    /// <summary>Asks the client to report changes to the indexed files and nitrogen.json (<c>workspace/didChangeWatchedFiles</c>).</summary>
    Task RegisterWatchersAsync(IReadOnlyList<string> extensions, CancellationToken cancel) =>
        connection.WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("id", "nitrogen-watch");
            w.WriteString("method", "client/registerCapability");
            w.WriteStartObject("params");
            w.WriteStartArray("registrations");
            w.WriteStartObject();
            w.WriteString("id", "nitrogen-watched-files");
            w.WriteString("method", "workspace/didChangeWatchedFiles");
            w.WriteStartObject("registerOptions");
            w.WriteStartArray("watchers");
            foreach (string pattern in extensions.Select(e => "**/*" + e).Append("**/nitrogen.json"))
            {
                w.WriteStartObject();
                w.WriteString("globPattern", pattern);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }, cancel);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceIndexTests|FullyQualifiedName~GrammarLoopTests|FullyQualifiedName~LspServerTests"`
Expected: PASS, including the existing `--config` test in `GrammarLoopTests` (its workspace has a `nitrogen.json` whose language uses the same extension; the bundled language still wins because configuration comes only from the fixed root).

- [ ] **Step 5: Full gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings, all pass.

```bash
git add Nitrogen.LanguageService Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs
git commit -m "Index the client workspace under --config, ignore client responses, register file watchers"
```

---

### Task 3: End-to-end check with the catalog, and docs

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Package the catalog plugins with this branch**

```bash
dotnet build Nitrogen.Cli/Nitrogen.Cli.csproj -c Release
SCRATCH=$(mktemp -d)
dotnet Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll package --config ../Nitrogen.Concepts/nitrogen.json --output "$SCRATCH/dist" --vscode
unzip -q "$SCRATCH/dist/catalog-0.1.0.vsix" -d "$SCRATCH/vsix"
```

Expected: `built …/catalog-0.1.0.vsix`.

- [ ] **Step 2: Open only CatalogTool over LSP with the catalog as the workspace**

```bash
cat > "$SCRATCH/probe.py" <<'PY'
import json, os, subprocess, sys, threading, time
root, relative, cmd = sys.argv[1], sys.argv[2], sys.argv[3:]
p = subprocess.Popen(cmd, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
def send(m):
    b = json.dumps(m).encode(); p.stdin.write(b"Content-Length: %d\r\n\r\n" % len(b) + b); p.stdin.flush()
msgs = []
def read():
    while True:
        line = p.stdout.readline()
        if not line: return
        if line.lower().startswith(b"content-length"):
            n = int(line.split(b":")[1]); p.stdout.readline(); msgs.append(json.loads(p.stdout.read(n)))
threading.Thread(target=read, daemon=True).start()
path = os.path.join(root, relative); uri = "file://" + path; text = open(path).read()
send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"rootUri": "file://" + root, "capabilities": {}}})
time.sleep(2); send({"jsonrpc": "2.0", "method": "initialized", "params": {}}); time.sleep(6)
send({"jsonrpc": "2.0", "method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "ncat", "version": 1, "text": text}}})
line = next(i for i, l in enumerate(text.splitlines()) if "provides SemanticCatalog.ValidateRecords" in l)
time.sleep(3)
send({"jsonrpc": "2.0", "id": 2, "method": "textDocument/definition", "params": {"textDocument": {"uri": uri}, "position": {"line": line, "character": 14}}})
time.sleep(2)
diagnostics = [m["params"]["diagnostics"] for m in msgs if m.get("method") == "textDocument/publishDiagnostics" and m["params"]["uri"] == uri]
print("diagnostics:", [(d.get("code"), d["message"]) for d in diagnostics[-1]])
print("definition:", [l["uri"].rsplit("/", 1)[-1] for m in msgs if m.get("id") == 2 for l in (m.get("result") or [])])
p.kill()
PY
python3 "$SCRATCH/probe.py" "$(cd ../Nitrogen.Concepts && pwd)" realizations/SemanticCatalog.CatalogTool.ncat \
  dotnet "$SCRATCH/vsix/extension/bundle/server/nitrogen.dll" lsp --config "$SCRATCH/vsix/extension/bundle/language/nitrogen.json"
```

Expected: `diagnostics: []` and `definition: ['SemanticCatalog.ValidateRecords.ncat']`.

- [ ] **Step 3: Document**

In `README.md`, in "VS Code and generated language support", after the paragraph that introduces the extension, add:

```markdown
For a `nitrogen.json` language, the server also reads the language's files in the workspace folder that are not open (skipping `bin`, `obj`, `node_modules`, and hidden folders), so references into closed files resolve and rename edits them. Diagnostics are reported for open files. Clients that support dynamic registration are asked to report changes to those files.
```

- [ ] **Step 4: Final gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings, all pass.

```bash
git add README.md
git commit -m "Document workspace indexing"
```

- [ ] **Step 5: Report**

State what was verified (tests, the catalog probe) and the follow-up in Nitrogen.Concepts: bump `external/Nitrogen` and rebuild the plugins with `tools/editors/build.sh`.
