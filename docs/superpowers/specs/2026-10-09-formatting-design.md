# Formatting

Status: approved (2026-10-09). Part of the goal of first-class language support.

## Problem

Nitrogen languages have no formatting: Format Document, Format Selection and format-on-type do
nothing. Mature tooling has all three. The syntax tree already holds what a formatter needs: every token
is a leaf with its exact span, comments and whitespace sit in the gaps between tokens
(`SyntaxTree.Trivia`), and list nodes (`X*`, `(X; S)*`) mark statements, members and rules.

Hand alignment is a real style in Nitrogen code (`token Number     = …`, `| Add   = Expr "+" Expr
precedence 10 left` in the grammars), so a formatter must not rewrite spaces within a line.

## Goal

Every Nitrogen language gets formatting that fixes indentation and the edges of lines, derived from its
syntax tree, with no grammar changes; never touching anything else.

Out of scope:
- spaces within a line, line breaks, and line width (a later version can add grammar annotations);
- tagged C# strings (C#'s formatter owns the file);
- formatting a document with syntax errors.

## 1. What a pass changes

Only whitespace at the start and end of lines:

1. **Indentation:** each line's indentation becomes its depth (section 2) times the indent unit: a tab
   when the editor's `insertSpaces` is false, else `tabSize` spaces.
2. **Trailing whitespace** is removed from every line.
3. **Blank lines:** a run of blank lines becomes one; blank lines at the start of the file are removed.
4. **End of file:** the file ends with exactly one line break (the file's own style: `\r\n` when its first
   line break is `\r\n`, else `\n`).

A line that starts inside a token (a multi-line string) or inside a comment (a multi-line block comment)
is left exactly as it is, apart from trailing whitespace inside a comment, which also stays. Spaces
within a line, line breaks, and the text of every token never change.

## 2. Depth

Computed from the document's leaf tokens in text order (comments and whitespace are trivia, not tokens):

1. **Brackets.** A token whose text is `{`, `(` or `[` opens a level; `}`, `)` or `]` closes one (never
   below zero). Brackets inside strings and comments don't count: those are single tokens or trivia.
2. **A line's bracket depth** is the number of levels open before its first token. A line whose first
   token is a closer has one level less.
3. **Continuation.** A line gets one extra level when its first token continues a list item that started
   on an earlier line, and the bracket depth at the line equals the bracket depth where the item
   started. A list item is a child of a list node (`SyntaxKinds.List`): a statement, member or rule. So
   `let x = a +\n b;` indents `b;`, while `max(a,\n b)` is indented once, by its bracket.
4. **Comment lines.** A line holding only a comment takes the depth of the next line that holds a
   token; at the end of the file, depth 0.
5. **Blank lines** have no indentation.

## 3. Safety

- **Syntax errors:** a document whose parse has errors or skipped input gets no edits.
- **Token check:** after computing the edits, the service applies them, re-parses the result, and
  compares its leaf tokens (text and order) with the original's. If they differ, it returns no edits.
- **Minimal edits:** one edit per changed line region (its indentation, its trailing whitespace, or a
  removed run of blank lines) and one for the end of the file, never a whole-document replacement.

## 4. Service

```csharp
public sealed record FormattingOptions(int TabSize, bool InsertSpaces);

public IReadOnlyList<DocumentEdit> Format(string uri, FormattingOptions options);
public IReadOnlyList<DocumentEdit> FormatRange(string uri, DocumentRange range, FormattingOptions options);
public IReadOnlyList<DocumentEdit> FormatOnType(string uri, DocumentPosition position, string typed, FormattingOptions options);
```

- `FormatRange` applies the rules to the lines the range touches (depth is still computed over the whole
  document); the end-of-file rule applies only when the range reaches the last line.
- `FormatOnType` with `}` re-indents the line of the position; any other character gives no edits.
- For a C# host, or a document that isn't open, all three return no edits.

## 5. LSP

- Capabilities: `documentFormattingProvider: true`, `documentRangeFormattingProvider: true`,
  `documentOnTypeFormattingProvider: { firstTriggerCharacter: "}" }`.
- `textDocument/formatting`, `textDocument/rangeFormatting` and `textDocument/onTypeFormatting` return
  `TextEdit[]` (`options.tabSize`, `options.insertSpaces` read from the request).

## 6. Editors

- **VS Code:** no change; Format Document, Format Selection and format-on-type (`editor.formatOnType`)
  use the server.
- **Rider:** language files use the platform's defaults (Reformat Code). The C# strings client keeps
  `LspFormattingDisabled` and `LspOnTypeFormattingDisabled`.
- **Feature matrix:** `docs/editor-support.md` gains a *Formatting* row: ✓ in language files, — (left to
  C#) in VS Code C# strings, — (left to Rider) in Rider C# strings.

## 7. Tests

- **Rules:** nested blocks; a closer line; a call split over lines; a continuation line; a comment line
  before code and at the end; hand alignment within a line untouched; a multi-line block comment's
  inner lines untouched; trailing whitespace; a run of blank lines and leading blank lines; the final
  line break (added, deduplicated, `\r\n` kept); tabs (`insertSpaces: false`) and a `tabSize`.
- **Safety:** a document with a syntax error gets no edits; edits are per line, not whole-document.
- **Range and on-type:** a range formats only its lines; `}` re-indents its line; another character
  gives nothing; a C# host gets nothing.
- **The repository's own code:** formatting `Nitrogen.Ngr/Nitrogen.ngr`, `examples/DateCalc/DateCalc.ngr`,
  `Nitrogen.Tests/Grammars/Calc.ngr`, `Nitrogen.Geometry/Geometry.ngr` and `examples/DateCalc/sample.datecalc`
  changes nothing. Where the rules and a file disagree, the file is kept and the difference is recorded
  here as a known limitation, or the rule is corrected.
- **LSP:** the three capabilities and a round trip for each request.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
