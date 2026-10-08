# Workspace symbols

Status: approved (2026-10-08). Part of the goal of first-class language support.

## Problem

The language server implements no `workspace/symbol`, so Ctrl+T in VS Code and Search Everywhere →
Symbols in Rider find nothing in Nitrogen languages. Finding a declaration in another file means
opening it first. For a language author, the most common search is a grammar rule, and a closed
`.ngr` grammar isn't even bound: the workspace index (`_closed`) holds only files of `nitrogen.json`
languages.

## Goal

Searching symbols finds the declarations of every Nitrogen file in the workspace, open or closed,
including the grammars `nitrogen.json` compiles and tagged strings in open C# files, with no
per-language code.

Out of scope:
- **Closed C# files:** tagged strings are found only while their `.cs` file is open.
- **Cross-file grammar navigation:** closed grammars are bound for search only (section 2), so
  definition and references between `.ngr` files don't change.
- **Rider's C# strings client:** it keeps workspace symbols off (section 4).

## 1. Service: `WorkspaceSymbols(query)`

```csharp
public sealed record WorkspaceSymbol(string Name, string Kind, OutlineKind Outline, DocumentLocation Location, string? Container);

public IReadOnlyList<WorkspaceSymbol> WorkspaceSymbols(string query);
```

1. **Sources.** Each document is searched once, open taking precedence over closed:
   - open documents of every language;
   - closed files of `nitrogen.json` languages (`_closed`);
   - tagged strings in open C# files;
   - closed grammars (section 2).
2. **Symbols.** Every declaration in a document's binding, the set the outline shows, flattened:
   - **Location:** the declaration's name, so the editor puts the cursor on it. A tagged string's
     location is mapped back into its `.cs` file (`OutOf`).
   - **Container:** the name of the nearest declaration whose node contains this one, as the
     outline nests them (a rule's container is its module); null at the top level.
   - **Kind and outline kind:** the declaration's kind and its language's `Presentation.StyleOf(kind).Outline`.
   - Built-ins have no location and are left out.
3. **Matching.** A symbol matches when the query's characters appear in its name in order, ignoring
   case (`dcexp` matches `DateCalcExpression`). The empty query matches every symbol.
4. **Order and cap.** Results are ordered by name (ordinal, ignoring case), then location (URI, line,
   column), and at most 1,000 are returned.

## 2. Closed grammars

- **Which:** the grammar files of each `nitrogen.json` language (`GrammarLanguage.Files()`) that
  aren't open.
- **How:** each is parsed with the `.ngr` language and bound in a project of its own, used only for
  search. Binding them in the shared `.ngr` project would report two grammars exporting the same
  module as duplicates in open files, and change their references.
- **When:** they are reloaded whenever their language compiles (`Compile`), which runs when
  `nitrogen.json` is read, when a grammar changes on disk (`FileChanged`), and when a grammar is
  opened or closed. A grammar that is open is served from its open document instead. A grammar no
  longer in `nitrogen.json`, or deleted, is dropped.
- **Limits:** the workspace index's limits apply: files over 1 MiB, and unreadable files, are skipped.

## 3. LSP

- `ServerCapabilities` advertises `workspaceSymbolProvider: true`.
- `workspace/symbol` (`query`) returns `SymbolInformation[]`: `name`, `kind` (the outline kind's
  `SymbolKind` value, as `textDocument/documentSymbol` uses), `location` and, when there is one,
  `containerName`.

## 4. Editors

- **VS Code:** no change. Ctrl+T, and `#` in the command palette, use it.
- **Rider:** the language-file client uses the platform's default (Search Everywhere → Symbols). The
  C# strings client runs a separate server and keeps `LspWorkspaceSymbolDisabled`, so results aren't
  duplicated.
- **Feature matrix:** `docs/editor-support.md` gains a *Workspace symbols* row: ✓ in language files,
  ✓ in VS Code C# strings (open files), — in Rider C# strings.

## 5. Tests

- **Sources:** declarations of an open file and of a closed indexed file are found; a file that is
  open is found once.
- **Grammars:** a closed grammar's rule is found with its module as container; opening the grammar
  still finds it once; a grammar changed on disk updates its symbols; one removed drops them.
- **Diagnostics:** an open `.ngr` file declaring the same module as a closed grammar gets no new
  diagnostics.
- **Tagged strings:** a declaration inside a tagged string in an open `.cs` file has a location in
  the `.cs` file.
- **Matching:** a subsequence ignoring case matches; a non-subsequence doesn't; the empty query
  matches everything; results are ordered and capped at 1,000.
- **Built-ins:** no built-in is returned.
- **LSP:** the capability, and a `workspace/symbol` round trip with `kind` and `containerName`.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
