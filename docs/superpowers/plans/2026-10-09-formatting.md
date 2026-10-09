# Formatting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Format Document, Format Selection and format-on-type (`}`) for every Nitrogen language, fixing indentation and the edges of lines only.

**Architecture:** A pure `Formatter` computes per-line whitespace edits from a syntax tree's leaf tokens: `{ }` blocks, list items (`SyntaxKinds.List` children) for continuations, and trivia for comment lines. The service refuses documents with syntax errors, re-parses the formatted text and returns no edits unless the token sequence is unchanged. The LSP server exposes the three formatting requests.

**Tech Stack:** C# / .NET 10, xUnit, the repo's LSP server.

Spec: `docs/superpowers/specs/2026-10-09-formatting-design.md`.

---

## Files

- Create `Nitrogen.LanguageService/Formatter.cs`: `FormattingOptions`, `Formatter` (tokens, edits, apply).
- Create `Nitrogen.LanguageService/NitrogenLanguageService.Formatting.cs`: `Format`, `FormatRange`, `FormatOnType`.
- Modify `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`.
- Create `Nitrogen.Tests/LanguageService/FormattingTests.cs`.
- Modify `docs/editor-support.md`, the spec (status and any limitation found in Task 3).

Commands (repo root): `dotnet build Nitrogen.slnx -warnaserror`; `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~FormattingTests"`; the whole suite.

The tests format `.ngr` grammar text: the `.ngr` language (served by `LspCommand.Registry()`) has `{ }` blocks, lists of members and alternatives, and `//` comments.

---

### Task 1: The formatter and its rules

**Files:**
- Create: `Nitrogen.LanguageService/Formatter.cs`
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.Formatting.cs`
- Test: `Nitrogen.Tests/LanguageService/FormattingTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Nitrogen.Tests/LanguageService/FormattingTests.cs`:

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Formatting: indentation and the edges of lines, from the syntax tree.</summary>
public class FormattingTests
{
    const string Uri = "file:///w/a.ngr";
    static readonly FormattingOptions TwoSpaces = new(2, true);

    /// <summary>The document's text after its formatting edits.</summary>
    static string Formatted(string text, FormattingOptions? options = null)
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, text);
        return Apply(text, service.Format(Uri, options ?? TwoSpaces));
    }

    internal static string Apply(string text, IReadOnlyList<DocumentEdit> edits)
    {
        var lines = new LineMap(text);
        foreach (var edit in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
        {
            int start = lines.OffsetOf(edit.Range.Start), end = lines.OffsetOf(edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }
        return text;
    }

    [Fact]
    public void Block_contents_indent_one_level_and_alignment_within_a_line_stays()
    {
        Assert.Equal("""
            syntax module M
            {
              token Word = ['a'..'z']+;
              syntax A   = "a"  Name:Word;
            }

            """, Formatted("""
            syntax module M
            {
            token Word = ['a'..'z']+;
                  syntax A   = "a"  Name:Word;
            }

            """));
    }

    [Fact]
    public void Nested_blocks_indent_from_the_line_holding_their_brace()
    {
        Assert.Equal("""
            syntax module M
            {
              token Word = ['a'..'z']+;
              extensible syntax E
              {
                | W = Word
              }
            }

            """, Formatted("""
            syntax module M
            {
            token Word = ['a'..'z']+;
            extensible syntax E
            {
            | W = Word
            }
            }

            """));
    }

    [Fact]
    public void A_continuation_keeps_its_offset_from_its_item()
    {
        // The semantics block one level in (DateCalc.ngr's style), and a hand-aligned continuation.
        Assert.Equal("""
            syntax module M
            {
              token Word = ['a'..'z']+;
              syntax A = Name:Word
                {
                  out Size : int = 0;
                }
              syntax B = "b"
                         Name:Word;
            }

            """, Formatted("""
            syntax module M
            {
            token Word = ['a'..'z']+;
            syntax A = Name:Word
              {
            out Size : int = 0;
                  }
            syntax B = "b"
                       Name:Word;
            }

            """));
    }

    [Fact]
    public void Comment_runs_move_as_a_block_to_the_next_code_line()
    {
        Assert.Equal("""
            // header
            syntax module M
            {
              // inside
              //   indented more
              token Word = ['a'..'z']+;
              // before the closer
            }
            // after

            """, Formatted("""
              // header
            syntax module M
            {
            // inside
            //   indented more
            token Word = ['a'..'z']+;
                 // before the closer
            }
               // after

            """));
    }

    [Fact]
    public void Trailing_whitespace_blank_lines_and_the_end_of_the_file()
    {
        Assert.Equal("syntax module M\n{\n\n  token Word = ['a'..'z']+;\n}\n",
            Formatted("\n\nsyntax module M   \n{\n\n\n  token Word = ['a'..'z']+;  \n}"));
        Assert.Equal("syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n",
            Formatted("syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n\n\n"));
        Assert.Equal("syntax module M\r\n{\r\n  token Word = ['a'..'z']+;\r\n}\r\n",
            Formatted("syntax module M\r\n{\r\n  token Word = ['a'..'z']+;\r\n}\r\n\r\n"));
    }

    [Fact]
    public void Tabs_and_a_tab_size_come_from_the_options()
    {
        const string text = "syntax module M\n{\ntoken Word = ['a'..'z']+;\n}\n";
        Assert.Equal("syntax module M\n{\n\ttoken Word = ['a'..'z']+;\n}\n", Formatted(text, new FormattingOptions(4, false)));
        Assert.Equal("syntax module M\n{\n    token Word = ['a'..'z']+;\n}\n", Formatted(text, new FormattingOptions(4, true)));
    }

    [Fact]
    public void Edits_are_per_line()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, "syntax module M\n{\ntoken Word = ['a'..'z']+;\ntoken Other = ['b'..'c']+;\n}\n");
        var edits = service.Format(Uri, TwoSpaces);
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(e.Range.Start.Line, e.Range.End.Line));
    }

    [Fact]
    public void A_formatted_document_gets_no_edits()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, "syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n");
        Assert.Empty(service.Format(Uri, TwoSpaces));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~FormattingTests"`
Expected: build error, `FormattingOptions` and `Format` don't exist.

- [ ] **Step 3: The formatter**

Create `Nitrogen.LanguageService/Formatter.cs`:

```csharp
namespace Nitrogen.LanguageService;

/// <summary>The editor's indentation settings (LSP's FormattingOptions): a level is <paramref name="TabSize"/> columns.</summary>
public sealed record FormattingOptions(int TabSize, bool InsertSpaces);

/// <summary>
/// Formatting (spec: formatting): whitespace at the start and end of lines only, from a syntax tree's
/// leaf tokens. Lines starting with a token are indented by their block (<c>{ }</c>) or keep their
/// offset from the list item they continue; runs of comment lines move with the next code line; trailing
/// whitespace, blank-line runs and the end of the file are tidied. Spaces within a line, line breaks and
/// tokens never change.
/// </summary>
internal static class Formatter
{
    internal readonly record struct Token(int Node, int Start, int End, string Text);

    /// <summary>A replacement of [Start, End) of the original text.</summary>
    internal readonly record struct Edit(int Start, int End, string Text);

    /// <summary>The tree's leaf tokens in text order: non-Missing nodes with no children and a non-empty span, one per start (an ambiguity's candidates share theirs).</summary>
    internal static List<Token> Tokens(SyntaxTree tree, string text)
    {
        var tokens = new List<Token>();
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.ChildCount(node) != 0 || (tree.Flags(node) & NodeFlags.Missing) != 0) continue;
            var span = tree.Span(node);
            if (span.Length > 0) tokens.Add(new Token(node, span.Start, span.End, text.Substring(span.Start, span.Length)));
        }
        tokens.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Node.CompareTo(b.Node));
        var distinct = new List<Token>(tokens.Count);
        foreach (var token in tokens)
            if (distinct.Count == 0 || distinct[^1].Start != token.Start) distinct.Add(token);
        return distinct;
    }

    /// <summary>The edits formatting lines <paramref name="first"/> to <paramref name="last"/>; the end-of-file rule applies when the last line is among them.</summary>
    internal static List<Edit> Edits(SyntaxTree tree, string text, FormattingOptions options, int first, int last)
    {
        var lines = new LineMap(text);
        int count = lines.LineCount;
        last = Math.Min(last, count - 1);
        int level = Math.Max(1, options.TabSize);
        var tokens = Tokens(tree, text);

        // Each line: its span, its first non-whitespace offset (-1 when none), and its indentation in columns.
        var start = new int[count];
        var end = new int[count];
        var firstChar = new int[count];
        var oldCols = new int[count];
        for (int line = 0; line < count; line++)
        {
            start[line] = lines.OffsetOf(new DocumentPosition(line, 0));
            end[line] = start[line] + lines.LineLength(line);
            int i = start[line], cols = 0;
            for (; i < end[line] && (text[i] == ' ' || text[i] == '\t'); i++) cols += text[i] == '\t' ? level - cols % level : 1;
            firstChar[line] = i < end[line] ? i : -1;
            oldCols[line] = cols;
        }

        int TokenStartingAt(int offset)
        {
            int low = 0, high = tokens.Count;
            while (low < high) { int middle = (low + high) >>> 1; if (tokens[middle].Start < offset) low = middle + 1; else high = middle; }
            return low < tokens.Count && tokens[low].Start == offset ? low : -1;
        }

        bool InsideToken(int offset)
        {
            int low = 0, high = tokens.Count;
            while (low < high) { int middle = (low + high) >>> 1; if (tokens[middle].Start < offset) low = middle + 1; else high = middle; }
            return low > 0 && tokens[low - 1].End > offset; // the last token starting before the offset still covers it
        }

        // The innermost open "{" before each token (its token index, or -1).
        var blockBefore = new int[tokens.Count];
        var open = new Stack<int>();
        for (int k = 0; k < tokens.Count; k++)
        {
            blockBefore[k] = open.Count > 0 ? open.Peek() : -1;
            if (tokens[k].Text == "{") open.Push(k);
            else if (tokens[k].Text == "}" && open.Count > 0) open.Pop();
        }

        int LineOf(int offset) => lines.PositionOf(offset).Line;

        // Line kinds: kept (starts inside a token), blank, code (starts with a token), comment (starts in trivia).
        var kept = new bool[count];
        var blank = new bool[count];
        var codeToken = new int[count];
        for (int line = 0; line < count; line++)
        {
            kept[line] = InsideToken(start[line]);
            blank[line] = !kept[line] && firstChar[line] < 0;
            codeToken[line] = kept[line] || blank[line] ? -1 : TokenStartingAt(firstChar[line]);
        }

        var newCols = (int[])oldCols.Clone();
        for (int line = 0; line < count; line++)
        {
            int k = codeToken[line];
            if (k < 0) continue;
            int block = blockBefore[k];
            int? continued = ContinuedItem(tree, tokens[k].Node, line, block);
            if (continued is int itemLine)
                newCols[line] = Math.Max(0, newCols[itemLine] + Math.Max(0, oldCols[line] - oldCols[itemLine]));
            else if (tokens[k].Text == "}")
                newCols[line] = block < 0 ? 0 : newCols[LineOf(tokens[block].Start)];
            else
                newCols[line] = block < 0 ? 0 : newCols[LineOf(tokens[block].Start)] + level;
        }

        // The innermost list item containing the node that started on an earlier line, when it started in the same block: its first line.
        int? ContinuedItem(SyntaxTree syntax, int node, int line, int block)
        {
            for (int child = node, parent = syntax.Parent(node); parent >= 0; child = parent, parent = syntax.Parent(parent))
            {
                if (syntax.Kind(parent) != SyntaxKinds.List) continue;
                int itemStart = syntax.Span(child).Start;
                int itemLine = LineOf(itemStart);
                if (itemLine >= line) continue;
                int itemToken = TokenStartingAt(itemStart);
                return itemToken >= 0 && blockBefore[itemToken] == block ? itemLine : null;
            }
            return null;
        }

        // Comment runs move with the next code line, one level in when it starts with "}".
        for (int line = 0; line < count; line++)
        {
            if (kept[line] || blank[line] || codeToken[line] >= 0) continue;
            int runEnd = line;
            while (runEnd + 1 < count && !kept[runEnd + 1] && !blank[runEnd + 1] && codeToken[runEnd + 1] < 0) runEnd++;
            int next = runEnd + 1;
            while (next < count && codeToken[next] < 0) next++;
            int target = next >= count ? 0 : newCols[next] + (tokens[codeToken[next]].Text == "}" ? level : 0);
            int delta = target - oldCols[line];
            for (int comment = line; comment <= runEnd; comment++) newCols[comment] = Math.Max(0, oldCols[comment] + delta);
            line = runEnd;
        }

        string Indent(int cols) => options.InsertSpaces ? new string(' ', cols) : new string('\t', cols / level) + new string(' ', cols % level);

        var edits = new List<Edit>();
        bool endOfFile = last == count - 1;
        int lastContent = count - 1;
        while (lastContent >= 0 && blank[lastContent]) lastContent--;
        int firstContent = 0;
        while (firstContent < count && blank[firstContent]) firstContent++;

        for (int line = first; line <= last; line++)
        {
            if (endOfFile && line > lastContent) break; // the end-of-file edit covers the rest
            if (blank[line])
            {
                // Leading blank lines go, as does every blank line after the first of a run; a kept one loses its whitespace.
                if (line < firstContent || (line > 0 && blank[line - 1]))
                {
                    int to = line + 1 < count ? start[line + 1] : end[line];
                    edits.Add(new Edit(start[line], to, ""));
                }
                else if (end[line] > start[line]) edits.Add(new Edit(start[line], end[line], ""));
                continue;
            }
            if (!kept[line])
            {
                string indent = Indent(newCols[line]);
                if (text.AsSpan(start[line], firstChar[line] - start[line]).SequenceEqual(indent) is false)
                    edits.Add(new Edit(start[line], firstChar[line], indent));
            }
            if (endOfFile && line == lastContent) continue; // its trailing whitespace goes with the end-of-file edit
            int trailing = TrailingStart(line);
            if (trailing < end[line]) edits.Add(new Edit(trailing, end[line], ""));
        }

        if (endOfFile && lastContent >= 0)
        {
            int contentEnd = TrailingStart(lastContent);
            int lineBreak = text.IndexOf('\n');
            string newline = lineBreak > 0 && text[lineBreak - 1] == '\r' ? "\r\n" : "\n";
            if (text.AsSpan(contentEnd).SequenceEqual(newline) is false) edits.Add(new Edit(contentEnd, text.Length, newline));
        }
        return edits;

        // Where a line's trailing whitespace starts; its end when the line ends inside a token.
        int TrailingStart(int line)
        {
            if (InsideToken(end[line])) return end[line];
            int i = end[line];
            while (i > start[line] && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
            return i;
        }
    }

    /// <summary>The text after the edits (non-overlapping, against the original).</summary>
    internal static string Apply(string text, IEnumerable<Edit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            text = string.Concat(text.AsSpan(0, edit.Start), edit.Text, text.AsSpan(edit.End));
        return text;
    }
}
```

Note: a leading blank line before `firstContent` that is blank-but-has-whitespace is deleted whole by the first branch; a single blank line inside a run's first position keeps its line break and only loses whitespace.

- [ ] **Step 4: The service**

Create `Nitrogen.LanguageService/NitrogenLanguageService.Formatting.cs`:

```csharp
namespace Nitrogen.LanguageService;

/// <summary>
/// Formatting requests (spec: formatting). A document with syntax errors is not formatted, and the edits
/// are returned only when the formatted text has the same tokens: a formatter bug can never change code.
/// C# hosts are left to C#.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    public IReadOnlyList<DocumentEdit> Format(string uri, FormattingOptions options) => FormatLines(uri, options, 0, int.MaxValue);

    /// <summary>The edits for the lines <paramref name="range"/> touches; the indentation still follows the whole document.</summary>
    public IReadOnlyList<DocumentEdit> FormatRange(string uri, DocumentRange range, FormattingOptions options) =>
        FormatLines(uri, options, range.Start.Line, range.End.Line);

    /// <summary>After <c>}</c>, the edits re-indenting its line; nothing after any other character.</summary>
    public IReadOnlyList<DocumentEdit> FormatOnType(string uri, DocumentPosition position, string typed, FormattingOptions options) =>
        typed == "}" ? FormatLines(uri, options, position.Line, position.Line) : [];

    IReadOnlyList<DocumentEdit> FormatLines(string uri, FormattingOptions options, int first, int last)
    {
        if (_hosts.ContainsKey(uri) || IsEmbedded(uri) || !_documents.TryGetValue(uri, out var document)) return [];
        var parsed = document.Parsed;
        if (parsed.HasErrors || parsed.Tree.SkippedSpans.Length > 0) return [];
        var edits = Formatter.Edits(parsed.Tree, document.Text, options, first, last);
        if (edits.Count == 0) return [];

        string formatted = Formatter.Apply(document.Text, edits);
        using var reparsed = document.Language.Language.Parse(formatted, document.Start);
        if (reparsed.HasErrors || !Formatter.Tokens(reparsed.Tree, formatted).Select(t => t.Text)
                .SequenceEqual(Formatter.Tokens(parsed.Tree, document.Text).Select(t => t.Text)))
            return [];
        return edits.OrderBy(e => e.Start)
            .Select(e => new DocumentEdit(new DocumentRange(document.Lines.PositionOf(e.Start), document.Lines.PositionOf(e.End)), e.Text))
            .ToList();
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~FormattingTests"`
Expected: 8 passed. If a test fails, compare the formatted text with the expected one line by line before changing a rule: the expectations are the spec's rules applied by hand.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Formatter.cs Nitrogen.LanguageService/NitrogenLanguageService.Formatting.cs Nitrogen.Tests/LanguageService/FormattingTests.cs
git commit -m "Format indentation and line ends from the syntax tree

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Safety, range and on-type

**Files:**
- Test: `Nitrogen.Tests/LanguageService/FormattingTests.cs`

- [ ] **Step 1: Write the tests**

Append to `FormattingTests`:

```csharp
    [Fact]
    public void A_document_with_a_syntax_error_gets_no_edits()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, "syntax module M\n{\ntoken = ;\n}\n");
        Assert.Empty(service.Format(Uri, TwoSpaces));
    }

    [Fact]
    public void A_range_formats_only_its_lines()
    {
        const string text = "syntax module M\n{\ntoken Word = ['a'..'z']+;\ntoken Other = ['b'..'c']+;\n}\n";
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, text);
        var edits = service.FormatRange(Uri, new DocumentRange(new DocumentPosition(3, 0), new DocumentPosition(3, 5)), TwoSpaces);
        Assert.Equal("syntax module M\n{\ntoken Word = ['a'..'z']+;\n  token Other = ['b'..'c']+;\n}\n", Apply(text, edits));
    }

    [Fact]
    public void Typing_a_closing_brace_reindents_its_line_and_other_characters_do_nothing()
    {
        const string text = "syntax module M\n{\n  token Word = ['a'..'z']+;\n    }\n";
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, text);
        Assert.Equal("syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n",
            Apply(text, service.FormatOnType(Uri, new DocumentPosition(3, 5), "}", TwoSpaces)));
        Assert.Empty(service.FormatOnType(Uri, new DocumentPosition(2, 27), ";", TwoSpaces));
    }

    [Fact]
    public void A_csharp_file_is_left_to_csharp()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open("file:///w/C.cs", 1, "class C\n{\nint x;\n}\n");
        Assert.Empty(service.Format("file:///w/C.cs", TwoSpaces));
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~FormattingTests"`
Expected: 12 passed. These guard behavior built in Task 1; if one fails, fix the service, not the test.

- [ ] **Step 3: Commit**

```bash
git add Nitrogen.Tests/LanguageService/FormattingTests.cs
git commit -m "Cover formatting's safety, ranges and on-type

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The repository's own code

**Files:**
- Test: `Nitrogen.Tests/LanguageService/FormattingTests.cs`
- Modify (if needed): `Nitrogen.LanguageService/Formatter.cs`, the spec

- [ ] **Step 1: Write the test**

Append to `FormattingTests`:

```csharp
    static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Theory]
    [InlineData("Nitrogen.Ngr/Nitrogen.ngr")]
    [InlineData("examples/DateCalc/DateCalc.ngr")]
    [InlineData("Nitrogen.Tests/Grammars/Calc.ngr")]
    [InlineData("Nitrogen.Geometry/Geometry.ngr")]
    public void The_repository_grammars_are_already_formatted(string relative)
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), relative));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open("file:///w/" + Path.GetFileName(relative), 1, text);
        var edits = service.Format("file:///w/" + Path.GetFileName(relative), TwoSpaces);
        Assert.True(edits.Count == 0, string.Join("\n", edits.Select(e => $"line {e.Range.Start.Line + 1}: '{e.NewText}'")));
    }

    [Fact]
    public void The_datecalc_sample_is_already_formatted()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-format-").FullName;
        try
        {
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            using var service = new NitrogenLanguageService(LspCommand.Registry());
            service.ConfigureWorkspace(root);
            string uri = new System.Uri(Path.Combine(root, "sample.datecalc")).AbsoluteUri;
            service.Open(uri, 1, File.ReadAllText(Path.Combine(root, "sample.datecalc")));
            Assert.Empty(service.Format(uri, TwoSpaces));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
```

- [ ] **Step 2: Run and calibrate**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~FormattingTests"`
Expected: all pass. A failure lists the lines the formatter would change. For each:
1. If the file's layout follows a convention the rules miss, correct the rule in `Formatter.cs` (and the spec's section 2) and add a test for it in `FormattingTests`.
2. If the file is inconsistent with itself (a one-off stray indent the rules rightly fix), fix the file's whitespace in this commit, list it in the PR description, and keep the test strict.
3. If neither applies (a deliberate layout the rules can't express), record it in the spec under a new **Known limitations** heading at the end of section 2, with the file, the line and why, and leave the rule and the file as they are; the test then asserts exactly the edits for those lines.

- [ ] **Step 3: Commit**

```bash
git add Nitrogen.Tests/LanguageService/FormattingTests.cs Nitrogen.LanguageService/Formatter.cs docs/superpowers/specs/2026-10-09-formatting-design.md
git commit -m "Check that formatting leaves the repository's grammars and sample as they are

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: LSP

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/FormattingTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `FormattingTests` (add `using System.Text.Json;` at the top):

```csharp
    [Fact]
    public async Task The_server_answers_formatting_requests()
    {
        const string text = "syntax module M\n{\ntoken Word = ['a'..'z']+;\n    }\n";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri
            + "\",\"languageId\":\"ngr\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";
        const string options = "\"options\":{\"tabSize\":2,\"insertSpaces\":true}";
        string format = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/formatting\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\"}," + options + "}}";
        string range = "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"textDocument/rangeFormatting\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri
            + "\"},\"range\":{\"start\":{\"line\":2,\"character\":0},\"end\":{\"line\":2,\"character\":3}}," + options + "}}";
        string onType = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/onTypeFormatting\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri
            + "\"},\"position\":{\"line\":3,\"character\":5},\"ch\":\"}\"," + options + "}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""", open, format, range, onType,
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var capabilities = messages[0].GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("documentRangeFormattingProvider").GetBoolean());
        Assert.Equal("}", capabilities.GetProperty("documentOnTypeFormattingProvider").GetProperty("firstTriggerCharacter").GetString());
        JsonElement Result(int id) => messages.Single(m => m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id).GetProperty("result");
        Assert.Equal(2, Result(5).GetArrayLength());   // the token line and the closer
        Assert.Equal(1, Result(6).GetArrayLength());   // the token line
        var closer = Assert.Single(Result(7).EnumerateArray());
        Assert.Equal("", closer.GetProperty("newText").GetString());
        Assert.Equal(3, closer.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~The_server_answers_formatting"`
Expected: FAIL: no `documentFormattingProvider` capability.

- [ ] **Step 3: Messages**

In `Nitrogen.LanguageService/Lsp/LspMessages.cs`:

1. `ServerCapabilities`: after `bool? WorkspaceSymbolProvider = null` add (moving the closing `);`):

```csharp
    bool? WorkspaceSymbolProvider = null,
    bool? DocumentFormattingProvider = null,
    bool? DocumentRangeFormattingProvider = null,
    DocumentOnTypeFormattingOptions? DocumentOnTypeFormattingProvider = null);
```

2. After the `LspSymbolInformation` record:

```csharp
public sealed record DocumentOnTypeFormattingOptions(string FirstTriggerCharacter);

public sealed record LspFormattingOptions(int TabSize, bool InsertSpaces);

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, LspFormattingOptions Options);

public sealed record DocumentRangeFormattingParams(TextDocumentIdentifier TextDocument, LspRange Range, LspFormattingOptions Options);

public sealed record DocumentOnTypeFormattingParams(TextDocumentIdentifier TextDocument, LspPosition Position, string Ch, LspFormattingOptions Options);
```

3. After `[JsonSerializable(typeof(LspSymbolInformation[]))]`:

```csharp
[JsonSerializable(typeof(DocumentFormattingParams))]
[JsonSerializable(typeof(DocumentRangeFormattingParams))]
[JsonSerializable(typeof(DocumentOnTypeFormattingParams))]
[JsonSerializable(typeof(LspTextEdit[]))]
```

   (If `LspTextEdit[]` is already registered, skip that line: the build reports a duplicate.)

- [ ] **Step 4: Server**

In `Nitrogen.LanguageService/Lsp/LspServer.cs`:

1. The initialize result: after `WorkspaceSymbolProvider: true` add `, DocumentFormattingProvider: true, DocumentRangeFormattingProvider: true, DocumentOnTypeFormattingProvider: new DocumentOnTypeFormattingOptions("}")`.

2. Cases before `default:`:

```csharp
            case "textDocument/formatting":
            {
                var request = Params(parameters, LspJson.Default.DocumentFormattingParams);
                await RespondEditsAsync(id, service.Format(request.TextDocument.Uri, Options(request.Options)), cancel);
                break;
            }
            case "textDocument/rangeFormatting":
            {
                var request = Params(parameters, LspJson.Default.DocumentRangeFormattingParams);
                var range = new DocumentRange(Position(request.Range.Start), Position(request.Range.End));
                await RespondEditsAsync(id, service.FormatRange(request.TextDocument.Uri, range, Options(request.Options)), cancel);
                break;
            }
            case "textDocument/onTypeFormatting":
            {
                var request = Params(parameters, LspJson.Default.DocumentOnTypeFormattingParams);
                await RespondEditsAsync(id, service.FormatOnType(request.TextDocument.Uri, Position(request.Position), request.Ch, Options(request.Options)), cancel);
                break;
            }
```

3. Helpers, next to `Location(…)`:

```csharp
    static FormattingOptions Options(LspFormattingOptions options) => new(options.TabSize, options.InsertSpaces);

    Task RespondEditsAsync(JsonElement id, IReadOnlyList<DocumentEdit> edits, CancellationToken cancel) =>
        RespondAsync(id, edits.Select(edit => new LspTextEdit(Range(edit.Range), edit.NewText)).ToArray(), LspJson.Default.LspTextEditArray, cancel);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~FormattingTests"`
Expected: all pass. Then `dotnet build Nitrogen.slnx -warnaserror` and the whole suite.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspMessages.cs Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.Tests/LanguageService/FormattingTests.cs
git commit -m "Serve formatting over LSP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Docs

**Files:**
- Modify: `docs/editor-support.md`, `docs/superpowers/specs/2026-10-09-formatting-design.md`

- [ ] **Step 1: Feature matrix**

In `docs/editor-support.md`, after the `| Workspace symbols | … |` row:

```markdown
| Formatting (indentation and line ends) | ✓ | — (left to C#) | — (left to Rider) |
```

- [ ] **Step 2: Rider**

Confirm `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt` still sets `LspFormattingDisabled` and `LspOnTypeFormattingDisabled` for the C# client. No change expected.

- [ ] **Step 3: Spec status**

Change `Status: approved (2026-10-09).` to `Status: implemented (2026-10-09).`

- [ ] **Step 4: Verify and commit**

Run `python3 eng/check-links.py`, `dotnet build Nitrogen.slnx -warnaserror`, and the whole suite.

```bash
git add docs/editor-support.md docs/superpowers/specs/2026-10-09-formatting-design.md
git commit -m "Document formatting

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
