# Folding and expand-selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every Nitrogen language gets folding ranges and expand-selection from its syntax tree, served over LSP, with no per-language code.

**Architecture:**
- A new `NitrogenLanguageService.Structure.cs` computes both from `Document.Parsed.Tree`.
- **Folding:** multi-line nodes (outermost per start line, closers left visible) plus multi-line comment gaps from `SyntaxTree.Trivia`.
- **Selection:** the chain of enclosing nodes with equal extents merged.
- **Hosts:** C# hosts get nothing.
- **LSP:** `LspServer` answers `textDocument/foldingRange` and `textDocument/selectionRange`.

**Tech Stack:** C# / .NET 10, xUnit, LSP 3.17.

**Spec:** `docs/superpowers/specs/2026-10-08-folding-selection-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- **Local `sample.datecalc`:** the working copy may hold uncommitted edits to `examples/DateCalc/sample.datecalc`. They make `DateCalcTests.Sample_shows_each_statement_value` fail on full runs. For full runs, back the file up, write the committed version, run, then copy the backup back and `cmp` it. Never commit that file.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `folding-selection`.

**Findings from a probe while planning** (a throwaway test, since deleted), on `DateCalc.ngr`:
- **Root extent.** The root's extent starts at the first token: lines 4 to 83, after three comment lines. The `syntax module DateCalc { … }` node has the same extent. The spec's rule that "the root, and any node with the root's extent, never fold" would suppress that module's fold. It would also suppress the fold of a file holding one multi-line statement, which the spec itself tests. **Change, recorded in the spec by Task 4:** the root's extent folds like any other node (as JSON and XML folds their root). Only folds that end on their start line are dropped. For a flat file such as `sample.datecalc`, this gives one fold over the statement list.
- **Comments.** The leading comment block is one trivia gap (`[0:0..4:0]`), so it folds as one comment block, lines 0 to 2.
- **Skipped input.** It is `SyntaxTree.SkippedSpans` (the spec says `Skipped`).
- **Node extents** (`SyntaxTree.Span`) don't include leading trivia. Their end is trimmed of trailing whitespace here to get the extent.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.LanguageService/ServiceTypes.cs` | `ServiceFoldingRange` | 1 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Structure.cs` | `FoldingRanges`, `SelectionRanges` | 1, 2 |
| `Nitrogen.Tests/LanguageService/StructureTests.cs` | tests | 1–3 |
| `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `LspServer.cs` | LSP | 3 |
| `docs/editor-support.md`, the spec | matrix rows; root rule; status | 4 |

---

### Task 1: Folding

**Files:**
- Modify: `Nitrogen.LanguageService/ServiceTypes.cs`
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.Structure.cs`
- Create: `Nitrogen.Tests/LanguageService/StructureTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Folding and expand-selection from the syntax tree.</summary>
public sealed class StructureTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-structure-").FullName;

    public StructureTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        return service;
    }

    IReadOnlyList<ServiceFoldingRange> Folds(NitrogenLanguageService service, string name, string text)
    {
        service.Open(Uri(name), 1, text);
        return service.FoldingRanges(Uri(name));
    }

    [Fact]
    public void A_call_split_over_two_lines_folds()
    {
        using var service = Service();
        Assert.Equal([new ServiceFoldingRange(0, 1, false)], Folds(service, "a.datecalc", "max(2026-10-05,\n  2026-12-25);"));
    }

    [Fact]
    public void A_grammar_folds_its_module_and_blocks_leaving_closers_visible()
    {
        using var service = Service();
        string grammar = File.ReadAllText(Path.Combine(_root, "DateCalc.ngr"));
        string[] lines = grammar.Split('\n');
        var folds = Folds(service, "DateCalc.ngr", grammar);

        int module = Array.FindIndex(lines, l => l.StartsWith("syntax module DateCalc", StringComparison.Ordinal));
        Assert.Contains(new ServiceFoldingRange(module, lines.Length - 3, false), folds); // ends before the closing "}" (then a final empty line)
        int day = Array.FindIndex(lines, l => l.Contains("| Day ", StringComparison.Ordinal));
        Assert.Contains(new ServiceFoldingRange(day + 1, day + 3, false), folds);          // the { … } semantics block, its "}" visible
        Assert.Contains(new ServiceFoldingRange(0, 2, true), folds);                       // the leading comment block
        Assert.All(folds, f => Assert.True(f.EndLine > f.StartLine));
        Assert.Equal(folds.Count, folds.Select(f => f.StartLine).Distinct().Count());       // one fold per start line
    }

    [Fact]
    public void Two_comment_lines_fold_and_one_does_not()
    {
        using var service = Service();
        Assert.Contains(new ServiceFoldingRange(0, 1, true), Folds(service, "a.datecalc", "// one\n// two\n1 + 1;"));
        Assert.DoesNotContain(Folds(service, "b.datecalc", "// one\n1 + 1;"), f => f.IsComment);
    }

    [Fact]
    public void A_csharp_file_gets_no_folds_from_nitrogen()
    {
        using var service = Service();
        Assert.Empty(Folds(service, "C.cs", "class C\n{\n    const string D = /*lang=datecalc*/ \"\"\"\n        1 + 1;\n        2 + 2;\n        \"\"\";\n}\n"));
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~StructureTests"`
Expected: build error, `ServiceFoldingRange` not found.

- [ ] **Step 3: Add the type.** Append to `ServiceTypes.cs`:

```csharp
/// <summary>A foldable region by lines (0-based, both inclusive): a syntax node, or a block of comments.</summary>
public sealed record ServiceFoldingRange(int StartLine, int EndLine, bool IsComment);
```

- [ ] **Step 4: Implement.** Create `Nitrogen.LanguageService/NitrogenLanguageService.Structure.cs`:

```csharp
namespace Nitrogen.LanguageService;

/// <summary>
/// Folding and expand-selection from the syntax tree, for every language. A node's extent runs from its
/// first character to its last non-whitespace one. Every node spanning two or more lines folds (the
/// outermost per start line), leaving a last line of only punctuation (a closing brace) visible, and so
/// does a gap between tokens holding comments on two or more lines. Selection climbs from the innermost
/// node at a position through its enclosing nodes, skipping repeated extents. C# hosts get neither.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    public IReadOnlyList<ServiceFoldingRange> FoldingRanges(string uri)
    {
        if (_hosts.ContainsKey(uri) || !_documents.TryGetValue(uri, out var document)) return [];
        var tree = document.Parsed.Tree;
        var text = document.Text;
        var lines = document.Lines;
        var byStart = new Dictionary<int, ServiceFoldingRange>();

        void Offer(ServiceFoldingRange fold)
        {
            if (fold.EndLine <= fold.StartLine) return;
            if (!byStart.TryGetValue(fold.StartLine, out var kept) || fold.EndLine > kept.EndLine) byStart[fold.StartLine] = fold;
        }

        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (Extent(tree, text, node) is not { } extent) continue;
            int start = lines.PositionOf(extent.Start).Line, end = lines.PositionOf(extent.End).Line;
            if (end > start) Offer(new ServiceFoldingRange(start, end, false));
        }
        // Closers stay visible: applied after choosing the outermost per line, so it never picks a different node.
        foreach (var (start, fold) in byStart.ToList())
        {
            if (fold.IsComment) continue;
            if (IsOnlyPunctuation(LineText(text, lines, fold.EndLine)))
            {
                if (fold.EndLine - 1 > start) byStart[start] = fold with { EndLine = fold.EndLine - 1 };
                else byStart.Remove(start);
            }
        }
        var skipped = tree.SkippedSpans.ToArray();
        foreach (var gap in tree.Trivia.ToArray())
        {
            int first = -1, last = -1;
            for (int i = gap.Start; i < gap.End && i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]) || skipped.Any(s => s.Start <= i && i < s.End)) continue;
                int line = lines.PositionOf(i).Line;
                if (first < 0) first = line;
                last = line;
            }
            if (first >= 0) Offer(new ServiceFoldingRange(first, last, true));
        }
        return byStart.Values.OrderBy(f => f.StartLine).ToList();
    }

    /// <summary>A node's extent: its first character to its last non-whitespace one; null when empty.</summary>
    static TextSpan? Extent(SyntaxTree tree, string text, int node)
    {
        var span = tree.Span(node);
        int end = Math.Min(span.End, text.Length);
        while (end > span.Start && char.IsWhiteSpace(text[end - 1])) end--;
        return end > span.Start ? new TextSpan(span.Start, end - span.Start) : null;
    }

    static string LineText(string text, LineMap lines, int line)
    {
        int start = lines.OffsetOf(new DocumentPosition(line, 0));
        return text.Substring(start, lines.LineLength(line));
    }

    static bool IsOnlyPunctuation(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(c => !char.IsLetterOrDigit(c) && c != '_');
    }
}
```

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~StructureTests"`
Expected: all pass. If `A_grammar_folds_its_module_and_blocks_leaving_closers_visible` fails on the module fold's end line, print `lines.Length` and the last three lines of `DateCalc.ngr`. The test assumes the file ends with `}` followed by a newline. Correct the expected end line to "the line before the last `}`", but don't change the rule.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/ServiceTypes.cs Nitrogen.LanguageService/NitrogenLanguageService.Structure.cs Nitrogen.Tests/LanguageService/StructureTests.cs
git commit -m "Fold every language's multi-line syntax and comments

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Expand-selection

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Structure.cs`
- Test: `Nitrogen.Tests/LanguageService/StructureTests.cs`

- [ ] **Step 1: Write the failing tests.** Add to `StructureTests`:

```csharp
    [Fact]
    public void Selection_grows_through_the_enclosing_syntax()
    {
        using var service = Service();
        const string text = "let sprint = 2 weeks;\nweekday(2026-10-05 + 3 * sprint);";
        service.Open(Uri("a.datecalc"), 1, text);
        var lines = new LineMap(text);

        var steps = Assert.Single(service.SelectionRanges(Uri("a.datecalc"), [lines.PositionOf(text.IndexOf('3'))]));
        string[] texts = steps.Select(r => text[lines.OffsetOf(r.Start)..lines.OffsetOf(r.End)]).ToArray();

        Assert.Equal(["3", "3 * sprint", "2026-10-05 + 3 * sprint", "weekday(2026-10-05 + 3 * sprint)", "weekday(2026-10-05 + 3 * sprint);"], texts[..5]);
        Assert.Equal(text, texts[^1]);
        Assert.All(texts.Zip(texts.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void Each_position_gets_its_own_steps()
    {
        using var service = Service();
        const string text = "1 + 2;\nmax(3, 4);";
        service.Open(Uri("a.datecalc"), 1, text);

        var result = service.SelectionRanges(Uri("a.datecalc"), [new DocumentPosition(0, 0), new DocumentPosition(1, 4)]);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 1)), result[0][0]);
        Assert.Equal(new DocumentRange(new DocumentPosition(1, 4), new DocumentPosition(1, 5)), result[1][0]);
    }

    [Fact]
    public void A_csharp_file_gets_no_selection_steps_from_nitrogen()
    {
        using var service = Service();
        service.Open(Uri("C.cs"), 1, "const string D = /*lang=datecalc*/ \"1 + 1;\";");
        Assert.All(service.SelectionRanges(Uri("C.cs"), [new DocumentPosition(0, 37)]), Assert.Empty);
    }
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~StructureTests"`
Expected: build error, `SelectionRanges` not found.

- [ ] **Step 3: Implement.** Add to `NitrogenLanguageService.Structure.cs`:

```csharp
    /// <summary>For each position, the ranges of the nodes around it, innermost first, without repeating a range; empty for a C# host.</summary>
    public IReadOnlyList<IReadOnlyList<DocumentRange>> SelectionRanges(string uri, IReadOnlyList<DocumentPosition> positions)
    {
        if (_hosts.ContainsKey(uri) || !_documents.TryGetValue(uri, out var document))
            return positions.Select(_ => (IReadOnlyList<DocumentRange>)[]).ToList();
        var tree = document.Parsed.Tree;
        return positions.Select(position => (IReadOnlyList<DocumentRange>)Steps(document, tree, document.Lines.OffsetOf(position))).ToList();
    }

    List<DocumentRange> Steps(Document document, SyntaxTree tree, int offset)
    {
        // The innermost node whose extent holds the offset (its end included, so a cursor just after a token selects it).
        int innermost = -1, innermostLength = int.MaxValue;
        for (int node = 0; node < tree.NodeCount; node++)
            if (Extent(tree, document.Text, node) is { } extent && extent.Start <= offset && offset <= extent.End &&
                (extent.Length < innermostLength || extent.Length == innermostLength && tree.ChildCount(node) == 0))
                (innermost, innermostLength) = (node, extent.Length);

        var steps = new List<DocumentRange>();
        TextSpan? previous = null;
        for (int node = innermost; node >= 0; node = tree.Parent(node))
        {
            if (Extent(tree, document.Text, node) is not { } extent || extent == previous) continue;
            steps.Add(document.Lines.RangeOf(extent));
            previous = extent;
        }
        if (steps.Count == 0) steps.Add(document.Lines.RangeOf(new TextSpan(0, document.Text.Length)));
        return steps;
    }
```

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~StructureTests"`
Expected: all pass. If `Selection_grows_through_the_enclosing_syntax` fails, print `texts`. An extra step between the listed ones (a wrapper node with a different extent) is fine: assert that the expected five appear in order, rather than as a prefix, and keep the no-repeats assertion.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.Structure.cs Nitrogen.Tests/LanguageService/StructureTests.cs
git commit -m "Grow the selection through the enclosing syntax

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: LSP

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/StructureTests.cs`

- [ ] **Step 1: Write the failing test.** Add to `StructureTests` (with `using System.Text.Json;`):

```csharp
    [Fact]
    public async Task The_server_answers_folding_and_selection_ranges()
    {
        string doc = Uri("a.datecalc");
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri + "\",\"capabilities\":{}}}";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":" + JsonSerializer.Serialize("// one\n// two\nmax(2026-10-05,\n  2026-12-25);") + "}}}";
        string folding = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/foldingRange\",\"params\":{\"textDocument\":{\"uri\":\"" + doc + "\"}}}";
        string selection = "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"textDocument/selectionRange\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\"},\"positions\":[{\"line\":2,\"character\":5}]}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service, initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            open, folding, selection, """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var capabilities = messages[0].GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("foldingRangeProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("selectionRangeProvider").GetBoolean());
        JsonElement Result(int id) => messages.Single(m => m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id).GetProperty("result");

        var folds = Result(5).EnumerateArray().ToList();
        Assert.Contains(folds, f => f.GetProperty("startLine").GetInt32() == 0 && f.GetProperty("endLine").GetInt32() == 1 && f.GetProperty("kind").GetString() == "comment");
        Assert.Contains(folds, f => f.GetProperty("startLine").GetInt32() == 2 && f.GetProperty("endLine").GetInt32() == 3 && !f.TryGetProperty("kind", out _));

        var innermost = Assert.Single(Result(6).EnumerateArray());
        Assert.Equal(4, innermost.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()); // the date token
        Assert.True(innermost.TryGetProperty("parent", out var parent));
        Assert.Equal(JsonValueKind.Object, parent.ValueKind);
    }
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~StructureTests"`
Expected: the new test fails with `KeyNotFoundException` (no `foldingRangeProvider`).

- [ ] **Step 3: Messages.** In `LspMessages.cs`:
  - Add the last two `ServerCapabilities` parameters, `bool? FoldingRangeProvider = null` and `bool? SelectionRangeProvider = null`, after `SignatureHelpProvider`.
  - Add the records:

```csharp
public sealed record LspFoldingRange(int StartLine, int EndLine, string? Kind = null);

public sealed record SelectionRangeParams(TextDocumentIdentifier TextDocument, LspPosition[] Positions);

/// <summary>A selection range, inside its <paramref name="Parent"/> (the next larger range), if any.</summary>
public sealed record LspSelectionRange(LspRange Range, LspSelectionRange? Parent = null);
```

  - Register `[JsonSerializable(typeof(LspFoldingRange[]))]`, `[JsonSerializable(typeof(SelectionRangeParams))]` and `[JsonSerializable(typeof(LspSelectionRange[]))]`.

- [ ] **Step 4: Server.** In `LspServer.cs`:
  - Add `FoldingRangeProvider: true, SelectionRangeProvider: true` to the `initialize` capabilities, after `SignatureHelpProvider: …`.
  - Add the cases before `default:`:

```csharp
            case "textDocument/foldingRange":
            {
                string uri = Params(parameters, LspJson.Default.TextDocumentParams).TextDocument.Uri;
                var folds = service.FoldingRanges(uri).Select(f => new LspFoldingRange(f.StartLine, f.EndLine, f.IsComment ? "comment" : null)).ToArray();
                await RespondAsync(id, folds, LspJson.Default.LspFoldingRangeArray, cancel);
                break;
            }
            case "textDocument/selectionRange":
            {
                var request = Params(parameters, LspJson.Default.SelectionRangeParams);
                var positions = request.Positions.Select(Position).ToArray();
                var steps = service.SelectionRanges(request.TextDocument.Uri, positions);
                var ranges = steps.Select((list, i) =>
                {
                    if (list.Count == 0) return new LspSelectionRange(Range(new DocumentRange(positions[i], positions[i])));
                    LspSelectionRange? outer = null;
                    for (int k = list.Count - 1; k >= 0; k--) outer = new LspSelectionRange(Range(list[k]), outer);
                    return outer!;
                }).ToArray();
                await RespondAsync(id, ranges, LspJson.Default.LspSelectionRangeArray, cancel);
                break;
            }
```

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~StructureTests|FullyQualifiedName~LspServerTests|FullyQualifiedName~SignatureHelpTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp Nitrogen.Tests/LanguageService/StructureTests.cs
git commit -m "Serve folding and selection ranges over LSP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Docs, full check, probe

**Files:**
- Modify: `docs/editor-support.md`, `docs/superpowers/specs/2026-10-08-folding-selection-design.md`

- [ ] **Step 1: Feature matrix.** In `docs/editor-support.md`, add after the *Outline* row:

```markdown
| Folding | ✓ | — (left to C#) | — (left to Rider) |
| Expand selection | ✓ | — (left to C#) | — (left to Rider) |
```

- [ ] **Step 2: Spec.**
  - Status line: `Status: implemented (YYYY-MM-DD). Part of the goal of first-class language support.`, using the date of this step.
  - In section 1, replace step 1's last sentence ("The root node, and any node with the same extent as the root, never fold.") with: "The root folds like any other node, as JSON and XML editors fold their root: excluding it would also exclude a construct that is the whole file, such as a grammar's one `syntax module`, because a node's extent starts at its first token."
  - In step 4, change `SyntaxTree.Skipped` to `SyntaxTree.SkippedSpans`.
  - Replace step 5's second sentence with: "Folds starting on the same line, of either kind, keep the one ending latest."
  - In section 5, change "the root doesn't fold" to "a file holding one multi-line statement folds it".

- [ ] **Step 3: Full build, tests and link check**, with the `sample.datecalc` backup and restore from the Conventions.

Run: `dotnet build Nitrogen.slnx -warnaserror`. Expected: `Build succeeded.` with 0 warnings.
Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`. Expected: all pass: 1,106 plus 4 + 3 + 1 = 1,114.
Run: `python3 eng/check-links.py`. Expected: every link resolves.

- [ ] **Step 4: Probe the built server.** Build the CLI with `dotnet build Nitrogen.Cli -c Release`. Over stdio, initialize with `examples/DateCalc` as the root and open `examples/DateCalc/DateCalc.ngr` with its file text. Then:
  - request `textDocument/foldingRange` and print each fold with its first line's text;
  - request `textDocument/selectionRange` at the `D` of `DC0001` and print each step's text.

  Report the output.

- [ ] **Step 5: Commit**

```bash
git add docs/editor-support.md docs/superpowers/specs/2026-10-08-folding-selection-design.md
git commit -m "Document folding and expand-selection

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
