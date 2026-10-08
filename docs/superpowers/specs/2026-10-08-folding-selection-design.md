# Folding and expand-selection

Status: approved design (2026-10-08). Part of the goal of first-class language support.

## Problem

The language server offers no folding ranges, so VS Code folds Nitrogen languages by indentation
alone. It offers no selection ranges either, so expand-selection (Alt+Shift+→ in VS Code, Ctrl+W in
Rider) grows by words and lines, not by syntax. Both are standard in mature tooling, and both can come
from the syntax tree every Nitrogen language already has.

## Goal

Every Nitrogen language gets folding and expand-selection from its syntax tree, with no per-language
code. Once a server provides folding ranges, VS Code uses them instead of indentation folding, so the
folds must be at least as useful.

Out of scope: `#region`-style markers, per-language folding configuration, and both features inside
tagged C# strings.

## 1. Folding: `FoldingRanges(uri)`

```csharp
public sealed record ServiceFoldingRange(int StartLine, int EndLine, bool IsComment);

public IReadOnlyList<ServiceFoldingRange> FoldingRanges(string uri);
```

1. **Candidates.** Every syntax node whose extent spans 2 or more lines folds. The extent runs from the
   node's first character to its last non-whitespace character. The root node, and any node with the
   same extent as the root, never fold.
2. **One fold per line.** Among candidates starting on the same line, only the outermost (the one
   ending on the latest line) is kept.
3. **Closers stay visible.** If a fold's last line, trimmed, consists only of punctuation (no letters,
   digits or `_`), such as `}`, `)` or `];`, the fold ends one line earlier. A fold that then ends on its
   start line is dropped.
4. **Comments.** For each gap between tokens (`SyntaxTree.Trivia`) that isn't skipped input
   (`SyntaxTree.Skipped`), the lines holding non-whitespace text are its comment lines. If the first and
   last of them differ, they form a fold with `IsComment`.
5. **Order.** Folds are returned by start line. A comment fold and a node fold never start on the same
   line, because a comment line holds no token's start.
6. **Tagged C# strings:** for a C# host, the result is empty; C# folds the file.

## 2. Expand-selection: `SelectionRanges(uri, positions)`

```csharp
public IReadOnlyList<IReadOnlyList<DocumentRange>> SelectionRanges(string uri, IReadOnlyList<DocumentPosition> positions);
```

For each position, a list of ranges from innermost to outermost:

1. **Start.** Start at the innermost non-empty node whose extent contains the position. A token is
   preferred, and the position may sit at the token's end.
2. **Climb.** Add each enclosing node, up to the root.
3. **Collapse.** Skip a step whose extent equals the previous step's, and skip empty nodes.
4. **Edge cases.** A position in a comment or whitespace starts at the innermost node containing it.
   With nothing containing it, the list holds only the whole document.
5. **Tagged C# strings:** for a C# host, every list is empty; C# handles selection.

Extents here are the same as for folding: from the first character to the last non-whitespace
character.

## 3. LSP

- `ServerCapabilities` advertises `foldingRangeProvider: true` and `selectionRangeProvider: true`.
- `textDocument/foldingRange` returns `FoldingRange[]`: `startLine`, `endLine`, and `kind: "comment"`
  for comment folds.
- `textDocument/selectionRange` (`positions[]`) returns `SelectionRange[]`, one per position: each
  `{ range, parent }`, nested from innermost to outermost. A position with an empty list gets
  `{ range: <the position as an empty range> }`.

## 4. Editors

- **VS Code:** no change.
- **Rider:** language files use the platform defaults. The C# client keeps `LspFoldingRangeDisabled`
  and `LspSelectionRangeDisabled`.
- **Feature matrix:** `docs/editor-support.md` gains two rows:
  - *Folding*: ✓ in language files, — (left to C#) in C# strings in VS Code, — (left to Rider) in C#
    strings in Rider;
  - *Expand selection*: the same.

## 5. Tests

- **Folding:**
  - a DateCalc call split over two lines (`max(start,\n  2026-12-25);`) → one fold, lines 0 to 1;
  - a `.ngr` grammar's `syntax module … { … }` → a fold ending on the line before the closing `}`;
  - a multi-line semantics block `{ … }` folds with its `}` visible;
  - two consecutive `//` lines → one comment fold; a single comment line → none;
  - no two folds start on the same line;
  - the root doesn't fold;
  - a C# host → none.
- **Expand-selection:**
  - the cursor on `3` in `weekday(start + 3 * sprint);` → `3`, `3 * sprint`, `start + 3 * sprint`,
    `weekday(start + 3 * sprint)`, then larger extents, ending with the whole document;
  - no two consecutive steps are equal;
  - two positions in one request → two lists;
  - a C# host → empty lists.
- **LSP:** both capabilities, a `textDocument/foldingRange` round trip, and a
  `textDocument/selectionRange` round trip with parent nesting.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
