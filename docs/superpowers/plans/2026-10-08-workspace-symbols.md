# Workspace Symbols Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `workspace/symbol` finds the declarations of every Nitrogen file in the workspace: open and closed language files, tagged strings in open C# files, and the closed grammars `nitrogen.json` compiles.

**Architecture:** A new partial `NitrogenLanguageService.Symbols.cs` flattens each document's binding declarations (with the outline's nesting for containers), filters by a case-insensitive subsequence match, orders and caps. Closed grammars are parsed with the `.ngr` language and bound one per project (search only), refreshed whenever a workspace language compiles or a `.ngr` file changes on disk. The LSP server advertises `workspaceSymbolProvider` and answers with `SymbolInformation[]`.

**Tech Stack:** C# / .NET 10, xUnit, the repo's hand-rolled LSP server (`System.Text.Json` source generation).

Spec: `docs/superpowers/specs/2026-10-08-workspace-symbols-design.md`.

---

## Files

- Create `Nitrogen.LanguageService/NitrogenLanguageService.Symbols.cs`: `WorkspaceSymbols`, symbol flattening, matching, closed grammars.
- Modify `Nitrogen.LanguageService/ServiceTypes.cs`: the `WorkspaceSymbol` record.
- Modify `Nitrogen.LanguageService/GrammarLanguages.cs`: refresh closed grammars in `Compile`, `LoadConfiguration` and `FileChangedCore`.
- Modify `Nitrogen.LanguageService/NitrogenLanguageService.cs`: dispose closed grammars.
- Modify `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`: capability, `workspace/symbol`, `.ngr` in dynamic watchers.
- Create `Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs`.
- Modify `Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs`: the watcher patterns now include `**/*.ngr`.
- Modify `docs/editor-support.md`, the spec (status).

Build and test commands (from the repo root):
- `dotnet build Nitrogen.slnx -warnaserror`
- `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests"`
- `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`

Note: `examples/DateCalc/sample.datecalc` may carry the user's uncommitted edits. Never commit or revert it; the tests below don't use it.

---

### Task 1: `WorkspaceSymbols` over open, closed and embedded documents

**Files:**
- Modify: `Nitrogen.LanguageService/ServiceTypes.cs`
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.Symbols.cs`
- Create: `Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs`

The tests use the small `links` language of `WorkspaceIndexTests` (`def x;` declares an `item`, `use x;` references one) and `LspCommand.Registry()`, which also serves `.ngr`. Until Task 2, closed grammars aren't searched, so these tests look only at `item` symbols.

- [ ] **Step 1: Write the failing tests**

Create `Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs`:

```csharp
using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Workspace symbol search across open, closed and embedded documents and closed grammars.</summary>
public sealed class WorkspaceSymbolTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-symbols-").FullName;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service()
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", WorkspaceIndexTests.Grammar);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.IndexWorkspace(_root);
        return service;
    }

    static List<WorkspaceSymbol> Items(NitrogenLanguageService service, string query = "") =>
        service.WorkspaceSymbols(query).Where(s => s.Kind == "item").ToList();

    [Fact]
    public void Open_and_closed_files_are_searched()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "def beta;\nuse alpha;");

        var items = Items(service);
        Assert.Equal(["alpha", "beta"], items.Select(s => s.Name));
        Assert.Equal(Uri("a.links"), items[0].Location.Uri);
        Assert.Equal(new DocumentLocation(Uri("b.links"), new DocumentRange(new DocumentPosition(0, 4), new DocumentPosition(0, 8))), items[1].Location);
        Assert.All(items, s => Assert.Null(s.Container));
    }

    [Fact]
    public void An_open_file_is_found_once()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("a.links"), 1, "def alpha;");
        Assert.Single(Items(service));
    }

    [Fact]
    public void The_query_matches_a_subsequence_ignoring_case()
    {
        using var service = Service();
        service.Open(Uri("a.links"), 1, "def alphabet;\ndef beta;\ndef gamma;");

        Assert.Equal(["alphabet"], Items(service, "ABT").Select(s => s.Name));
        Assert.Equal(["alphabet", "gamma"], Items(service, "aa").Select(s => s.Name));
        Assert.Empty(Items(service, "tb"));
        Assert.Equal(3, Items(service, "").Count);
    }

    static string Letters(int i) => new([(char)('a' + i / 676 % 26), (char)('a' + i / 26 % 26), (char)('a' + i % 26)]);

    [Fact]
    public void Results_are_ordered_by_name_and_capped()
    {
        using var service = Service();
        // In reverse, so the order must come from sorting.
        service.Open(Uri("a.links"), 1, string.Concat(Enumerable.Range(0, 1_005).Reverse().Select(i => $"def {Letters(i)};\n")));

        var all = service.WorkspaceSymbols("");
        Assert.Equal(1_000, all.Count);
        Assert.Equal(all.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase), all.Select(s => s.Name));
    }

    [Fact]
    public void A_tagged_string_symbol_is_located_in_its_csharp_file()
    {
        using var service = Service();
        const string host = "class C\n{\n    object R = Run(/*lang=links*/ \"def alpha;\");\n}\n";
        service.Open(Uri("C.cs"), 1, host);

        var alpha = Assert.Single(Items(service));
        Assert.Equal(Uri("C.cs"), alpha.Location.Uri);
        int column = host.Split('\n')[2].IndexOf("alpha", StringComparison.Ordinal);
        Assert.Equal(new DocumentRange(new DocumentPosition(2, column), new DocumentPosition(2, column + 5)), alpha.Location.Range);
    }

    [Fact]
    public void Built_ins_are_not_returned()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            if (Path.GetFileName(file) != "sample.datecalc") File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("a.datecalc"), 1, "let start = 2026-10-05;\nweekday(start);");

        var names = service.WorkspaceSymbols("").Where(s => s.Location.Uri == Uri("a.datecalc")).Select(s => s.Name).ToList();
        Assert.Equal(["start"], names);
        Assert.DoesNotContain(service.WorkspaceSymbols("weekday"), s => s.Name == "weekday");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests"`
Expected: build error, `WorkspaceSymbol` and `WorkspaceSymbols` don't exist.

- [ ] **Step 3: Add the record**

In `Nitrogen.LanguageService/ServiceTypes.cs`, after the `OutlineSymbol` record:

```csharp
/// <summary>A declaration found by workspace symbol search: its name's location, and the name of the declaration enclosing it.</summary>
public sealed record WorkspaceSymbol(string Name, string Kind, OutlineKind Outline, DocumentLocation Location, string? Container);
```

- [ ] **Step 4: Implement the search**

Create `Nitrogen.LanguageService/NitrogenLanguageService.Symbols.cs`:

```csharp
using Nitrogen.Binding;

namespace Nitrogen.LanguageService;

/// <summary>
/// Workspace symbol search: the declarations of the open documents (tagged strings included, located in
/// their C# file), of the closed workspace files, and of the closed grammars, matched by a subsequence of
/// their name ignoring case, ordered by name and capped.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    const int MaxWorkspaceSymbols = 1_000;

    public IReadOnlyList<WorkspaceSymbol> WorkspaceSymbols(string query)
    {
        var symbols = new List<WorkspaceSymbol>();
        foreach (var document in _documents.Values.Concat(_closed.Values)) symbols.AddRange(SymbolsOf(document, _projects[document.Language]));
        return symbols.Where(s => Matches(query, s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Location.Uri, StringComparer.Ordinal)
            .ThenBy(s => s.Location.Range.Start.Line)
            .ThenBy(s => s.Location.Range.Start.Character)
            .Take(MaxWorkspaceSymbols)
            .ToList();
    }

    /// <summary>A document's declarations, each with the name of the nearest declaration whose node contains it, as the outline nests them.</summary>
    IEnumerable<WorkspaceSymbol> SymbolsOf(Document document, Project project)
    {
        var tree = document.Parsed.Tree;
        var presentation = document.Language.Presentation;
        var open = new Stack<(TextSpan Span, string Name)>();
        var declarations = project[document.Uri].Declarations
            .OrderBy(d => tree.Span(d.Node).Start)
            .ThenByDescending(d => tree.Span(d.Node).Length);
        foreach (var declaration in declarations)
        {
            var span = tree.Span(declaration.Node);
            while (open.Count > 0 && !(open.Peek().Span.Start <= span.Start && span.End <= open.Peek().Span.End)) open.Pop();
            string? container = open.Count > 0 ? open.Peek().Name : null;
            open.Push((span, declaration.Name));
            yield return new WorkspaceSymbol(declaration.Name, declaration.Kind, presentation.StyleOf(declaration.Kind).Outline,
                OutOf(new DocumentLocation(document.Uri, document.Lines.RangeOf(declaration.NameSpan))), container);
        }
    }

    /// <summary>Whether the query's characters appear in the name in order, ignoring case; the empty query matches every name.</summary>
    static bool Matches(string query, string name)
    {
        int matched = 0;
        foreach (char c in name)
            if (matched < query.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[matched])) matched++;
        return matched == query.Length;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests"`
Expected: 6 passed.

If `Built_ins_are_not_returned` finds other names in `a.datecalc` (for example a derived declaration), check what they are before changing the expectation: built-ins have no path and never appear in a file's `Declarations`.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/ServiceTypes.cs Nitrogen.LanguageService/NitrogenLanguageService.Symbols.cs Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs
git commit -m "Search the workspace's declarations by name

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Closed grammars

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Symbols.cs`
- Modify: `Nitrogen.LanguageService/GrammarLanguages.cs` (`LoadConfiguration`, `FileChangedCore`, `Compile`)
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs` (`Dispose`)
- Test: `Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `WorkspaceSymbolTests`:

```csharp
    [Fact]
    public void A_closed_grammar_rule_is_found_with_its_module_as_container()
    {
        using var service = Service();

        var decl = Assert.Single(service.WorkspaceSymbols("Decl"), s => s.Name == "Decl");
        Assert.Equal(("rule", OutlineKind.Class, "Links", Uri("links.ngr")), (decl.Kind, decl.Outline, decl.Container, decl.Location.Uri));
        var module = Assert.Single(service.WorkspaceSymbols("Links"), s => s.Name == "Links");
        Assert.Equal(("module", (string?)null), (module.Kind, module.Container));
    }

    [Fact]
    public void An_open_grammar_is_found_once_and_again_after_closing()
    {
        using var service = Service();
        service.Open(Uri("links.ngr"), 1, WorkspaceIndexTests.Grammar);
        Assert.Single(service.WorkspaceSymbols("Decl"), s => s.Name == "Decl");
        service.Close(Uri("links.ngr"));
        Assert.Single(service.WorkspaceSymbols("Decl"), s => s.Name == "Decl");
    }

    [Fact]
    public void A_grammar_changed_on_disk_updates_its_symbols_and_a_deleted_one_drops_them()
    {
        using var service = Service();
        string path = Write("links.ngr", WorkspaceIndexTests.Grammar.Replace("Decl", "Define"));
        service.FileChanged(path);
        Assert.Contains(service.WorkspaceSymbols("Define"), s => s.Name == "Define");
        Assert.DoesNotContain(service.WorkspaceSymbols("Decl"), s => s.Name == "Decl");

        File.Delete(path);
        service.FileChanged(path);
        Assert.DoesNotContain(service.WorkspaceSymbols(""), s => s.Location.Uri == Uri("links.ngr"));
    }

    [Fact]
    public void A_closed_grammar_adds_no_diagnostics_to_an_open_one()
    {
        using var service = Service();
        service.Open(Uri("other.ngr"), 1, WorkspaceIndexTests.Grammar); // the same module, not in nitrogen.json
        Assert.DoesNotContain(service.Diagnostics(Uri("other.ngr")), d => d.Code.StartsWith("NB", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests"`
Expected: the first three fail (no `Decl` symbol: closed grammars aren't searched). `A_closed_grammar_adds_no_diagnostics_to_an_open_one` passes now and guards the design.

- [ ] **Step 3: Keep closed grammars**

In `NitrogenLanguageService.Symbols.cs`, add inside the class, after `MaxWorkspaceSymbols`:

```csharp
    /// <summary>The workspace languages' grammars that aren't open, each bound in a project of its own: search only, so two grammars exporting one module don't clash.</summary>
    readonly Dictionary<string, (Document Document, Project Project)> _closedGrammars = new(StringComparer.Ordinal);

    /// <summary>Reads the grammars of the workspace languages that aren't open; one whose text hasn't changed is kept.</summary>
    void RefreshClosedGrammars()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in _grammarLanguages)
            foreach (string path in language.Files())
            {
                string uri = new Uri(path).AbsoluteUri;
                if (_documents.ContainsKey(uri) || texts.ContainsKey(uri)) continue;
                try
                {
                    if (new FileInfo(path).Length <= MaxIndexedFileBytes) texts[uri] = File.ReadAllText(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        foreach (var (uri, kept) in _closedGrammars.ToList())
            if (!texts.TryGetValue(uri, out string? text) || text != kept.Document.Text)
            {
                _closedGrammars.Remove(uri);
                kept.Document.Dispose();
            }
        foreach (var (uri, text) in texts)
        {
            if (_closedGrammars.ContainsKey(uri) || !Registry.TryFind(uri, out var language, out var start)) continue;
            var document = new Document(uri, 0, text, language, start);
            var project = new Project(language.Language);
            project.Set(uri, document.Parsed.Tree);
            _closedGrammars[uri] = (document, project);
        }
    }

    void DisposeClosedGrammars()
    {
        foreach (var (document, _) in _closedGrammars.Values) document.Dispose();
        _closedGrammars.Clear();
    }
```

And in `WorkspaceSymbols`, after the `foreach` over `_documents` and `_closed`:

```csharp
        foreach (var (document, project) in _closedGrammars.Values) symbols.AddRange(SymbolsOf(document, project));
```

- [ ] **Step 4: Refresh them**

In `Nitrogen.LanguageService/GrammarLanguages.cs`:

1. `LoadConfiguration`, right after `_grammarLanguages.Clear();`:

```csharp
        RefreshClosedGrammars(); // none until the languages are read again
```

2. `Compile`, as its first statement (before `var files = language.Files();`). It runs when a language is read, when a grammar changes on disk, and when one is opened, edited or closed:

```csharp
        RefreshClosedGrammars();
```

3. `FileChangedCore`, replacing its last line `return _documents.ContainsKey(uri) ? [] : GrammarHookPath(path);` (a deleted grammar is no longer in `Files()`, so no compile runs for it):

```csharp
        if (path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase)) RefreshClosedGrammars();
        return _documents.ContainsKey(uri) ? [] : GrammarHookPath(path);
```

In `Nitrogen.LanguageService/NitrogenLanguageService.cs`, `Dispose`, after `DisposeGrammars();`:

```csharp
        DisposeClosedGrammars();
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests"`
Expected: 10 passed.

If the rule's kind or container differs, print `service.WorkspaceSymbols("")` and compare with `OutlineTests` for `.ngr` files (`NgrStyles` in `Nitrogen.Cli/LspCommand.cs` maps `module`, `rule`, `property`) before changing the expectation.

- [ ] **Step 6: Run everything**

Run: `dotnet build Nitrogen.slnx -warnaserror` (0 warnings), then `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj` (all pass).

- [ ] **Step 7: Commit**

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.Symbols.cs Nitrogen.LanguageService/GrammarLanguages.cs Nitrogen.LanguageService/NitrogenLanguageService.cs Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs
git commit -m "Search the workspace's closed grammars

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: LSP

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Modify: `Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs` (watcher patterns)
- Test: `Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `WorkspaceSymbolTests`:

```csharp
    [Fact]
    public async Task The_server_answers_workspace_symbol()
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", WorkspaceIndexTests.Grammar);
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri + "\",\"capabilities\":{}}}";
        const string symbols = """{"jsonrpc":"2.0","id":5,"method":"workspace/symbol","params":{"query":"decl"}}""";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service, initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            symbols, """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        Assert.True(messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("workspaceSymbolProvider").GetBoolean());
        var result = messages.Single(m => m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == 5).GetProperty("result");
        var decl = Assert.Single(result.EnumerateArray(), s => s.GetProperty("name").GetString() == "Decl");
        Assert.Equal((int)OutlineKind.Class, decl.GetProperty("kind").GetInt32());
        Assert.Equal("Links", decl.GetProperty("containerName").GetString());
        Assert.Equal(Uri("links.ngr"), decl.GetProperty("location").GetProperty("uri").GetString());
    }
```

And in `WorkspaceIndexTests.A_client_with_dynamic_registration_is_asked_to_watch_the_indexed_extensions`, change the expected patterns:

```csharp
        Assert.Equal(new[] { "**/*.links", "**/*.ngr", "**/nitrogen.json" },
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests|FullyQualifiedName~WorkspaceIndexTests"`
Expected: `The_server_answers_workspace_symbol` fails (no `workspaceSymbolProvider`), and the watcher test fails (no `**/*.ngr`).

- [ ] **Step 3: Messages**

In `Nitrogen.LanguageService/Lsp/LspMessages.cs`:

1. `ServerCapabilities`: after `bool? SelectionRangeProvider = null` add a parameter (move the closing `);`):

```csharp
    bool? SelectionRangeProvider = null,
    bool? WorkspaceSymbolProvider = null);
```

2. After the `LspSelectionRange` record:

```csharp
public sealed record WorkspaceSymbolParams(string? Query);

/// <summary>A symbol found by <c>workspace/symbol</c>; <paramref name="Kind"/> is LSP's SymbolKind.</summary>
public sealed record LspSymbolInformation(string Name, int Kind, LspLocation Location, string? ContainerName = null);
```

3. After `[JsonSerializable(typeof(LspSelectionRange[]))]`:

```csharp
[JsonSerializable(typeof(WorkspaceSymbolParams))]
[JsonSerializable(typeof(LspSymbolInformation[]))]
```

- [ ] **Step 4: Server**

In `Nitrogen.LanguageService/Lsp/LspServer.cs`:

1. The initialize result: change `FoldingRangeProvider: true, SelectionRangeProvider: true),` to:

```csharp
                        FoldingRangeProvider: true, SelectionRangeProvider: true, WorkspaceSymbolProvider: true),
```

2. A case before `default:`:

```csharp
            case "workspace/symbol":
            {
                string query = Params(parameters, LspJson.Default.WorkspaceSymbolParams).Query ?? "";
                var symbols = service.WorkspaceSymbols(query)
                    .Select(s => new LspSymbolInformation(s.Name, (int)s.Outline, Location(s.Location), s.Container)).ToArray();
                await RespondAsync(id, symbols, LspJson.Default.LspSymbolInformationArray, cancel);
                break;
            }
```

3. `RegisterWatchersAsync`: closed grammars feed search and compilation, so clients that register watchers dynamically (Rider) also report `.ngr` changes. Change the pattern list to:

```csharp
            foreach (string pattern in extensions.Select(e => "**/*" + e).Append("**/*.ngr").Append("**/nitrogen.json"))
```

Also update the doc comment above it to: `/// <summary>Asks the client to report changes to the indexed files, grammars and nitrogen.json (<c>workspace/didChangeWatchedFiles</c>).</summary>`

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceSymbolTests|FullyQualifiedName~WorkspaceIndexTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspMessages.cs Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.Tests/LanguageService/WorkspaceSymbolTests.cs Nitrogen.Tests/LanguageService/WorkspaceIndexTests.cs
git commit -m "Serve workspace symbols over LSP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Editors and docs

**Files:**
- Check: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt`
- Modify: `docs/editor-support.md`
- Modify: `docs/superpowers/specs/2026-10-08-workspace-symbols-design.md`

- [ ] **Step 1: Rider**

Confirm `NitrogenCSharpStrings.kt` still sets `override val workspaceSymbolCustomizer: LspWorkspaceSymbolCustomizer = LspWorkspaceSymbolDisabled`. No change is needed; the language-file client (`NitrogenLspSupport.kt`) uses the platform default.

- [ ] **Step 2: Feature matrix**

In `docs/editor-support.md`, after the `| Expand selection | … |` row, add:

```markdown
| Workspace symbols | ✓ | ✓ (open files) | — |
```

Check the table's column order first (language files, VS Code C# strings, Rider C# strings) and match the neighbouring rows' wording.

- [ ] **Step 3: Spec**

In the spec, change `Status: approved (2026-10-08).` to `Status: implemented (2026-10-08).`, and in section 2's **When** bullet add: "Clients that register file watchers dynamically (Rider) are asked to watch `**/*.ngr` too."

- [ ] **Step 4: Verify**

Run:
- `dotnet build Nitrogen.slnx -warnaserror` (0 warnings)
- `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj` (all pass)
- `python3 eng/check-links.py` (every link resolves)

- [ ] **Step 5: Commit**

```bash
git add docs/editor-support.md docs/superpowers/specs/2026-10-08-workspace-symbols-design.md
git commit -m "Document workspace symbols

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
