# Quick fixes for language diagnostics

Status: approved design (2026-10-07). Builds on
[inlay-hint evaluation](2026-10-07-inlay-hint-evaluation-design.md), which introduced discovering
helper-source objects for a workspace language.

## Problem

A language's `check` rules report errors such as DateCalc's DC0001 (`'2026-02-30' is not a calendar
date`) and DC0002 (`'+' does not apply to DateCalc.Date and DateCalc.Date`). The editor shows them,
but the server offers no way to fix them: it implements no `textDocument/codeAction`.

## Goal

A workspace language can supply quick fixes for its diagnostics, in C#. The server offers a fix only
after checking that it works: the error goes away and nothing new breaks. Proven with DateCalc:

- **DC0001:** `2026-02-30` → "Change to 2026-02-28".
- **DC0002:** `2026-12-25 + 2026-10-05` → "Use `-`", and nothing for `2 weeks + 2026-10-05`, where
  `-` doesn't fix it either.

Out of scope: DC0003 (a call with the wrong arguments, where no single edit is obvious), fixes for
parse errors, declaring fixes in `.ngr` grammars, and code actions in Rider's C# client.

## 1. Runtime types

In `Nitrogen.Runtime/Semantics/DiagnosticFixes.cs`:

```csharp
/// <summary>A diagnostic to fix: its code and span, and the document it is in.</summary>
public sealed record FixRequest(string Code, TextSpan Span, string Text, SyntaxTree Tree);

public sealed record TextEdit(TextSpan Span, string NewText);

/// <summary>A proposed fix: what the editor shows, and the edits it makes to the document.</summary>
public sealed record QuickFix(string Title, IReadOnlyList<TextEdit> Edits);

/// <summary>
/// A language's quick fixes, by diagnostic code. A workspace language's helper source exports one as a
/// public static field or property. A fixer only proposes; the host offers a fix after checking it.
/// </summary>
public sealed class DiagnosticFixes(IReadOnlyDictionary<string, Func<FixRequest, IEnumerable<QuickFix>>> byCode)
{
    public IEnumerable<QuickFix> Propose(FixRequest request);   // empty for a code without a fixer
}
```

## 2. Discovery

- `GrammarWorkspace.Compile` finds at most one public static `DiagnosticFixes` in the helper sources,
  with the same scan it uses for `EvaluationProfile`.
- Two or more are warning `NGR0004`, and none is used.
- `WorkspaceSnapshot.Fixes` and `LanguageEntry.Fixes` carry it; `NitrogenLanguageService.Compile`
  copies it across.

## 3. Service: `QuickFixes(uri, range)`

```csharp
public sealed record ServiceFix(string Title, ServiceDiagnostic Diagnostic, IReadOnlyList<DocumentEdit> Edits);

public IReadOnlyList<ServiceFix> QuickFixes(string uri, DocumentRange range);
```

1. Take the document's own diagnostics (`Diagnostics(uri)`) that overlap `range` and whose code the
   language has a fixer for. The client's list of diagnostics isn't used.
2. For each, call `Propose(new FixRequest(code, span, text, tree))`.
3. **Check each proposal** before offering it:
   - apply its edits to the document text, in reverse order of position (edits must not overlap, or
     the proposal is dropped);
   - parse the result with the document's start rule, and bind and check it in a scratch `Project` of
     the same language, holding only that document;
   - keep the fix only if no error with the same code overlaps the edited text, and the result has
     fewer errors than the original.

   **Limitation:** the check sees only this document, so in a language whose files refer to each
   other, a fix that relies on another file can be wrongly rejected. DateCalc files are self-contained.
4. A fixer that throws is treated as proposing nothing for that diagnostic.
5. **Tagged C# strings:** for a C# host, the request goes to the tagged string under `range`, as hover
   does. The resulting edits and diagnostic ranges are mapped back into the C# file.

## 4. LSP

- `ServerCapabilities` advertises `codeActionProvider: { codeActionKinds: ["quickfix"] }`.
- `textDocument/codeAction` (`CodeActionParams`: `textDocument`, `range`, `context`) returns
  `CodeAction[]`. Each has `title`, `kind: "quickfix"`, `diagnostics` (the one it fixes) and
  `edit: { changes: { uri: TextEdit[] } }`.
- New message records go in `LspMessages.cs` and the `LspJson` context.

## 5. Editors

- **VS Code:** no change. `vscode-languageclient` asks for code actions whenever the server advertises them.
- **Rider:** no change. Language files use the platform's default code actions. The C# client keeps
  `LspCodeActionsDisabled`, so quick fixes inside tagged strings appear in VS Code only: enabling them
  would mix Nitrogen's actions into Rider's own Alt+Enter menu for all C# code.

## 6. DateCalc

A new `examples/DateCalc/DateCalcFixes.cs`, added to `sources` in `nitrogen.json` and to the
test project's file copy, exports `public static readonly DiagnosticFixes Fixes`:

- **DC0001:** for an invalid `yyyy-MM-dd` date, the nearest valid one: the month clamped to 1–12, then
  the day clamped to 1–(days in that month). The title is "Change to 2026-02-28", and the edit
  replaces the date literal.
- **DC0002:** when the diagnostic's span holds an `Add` expression, replace its `+` operator with `-`;
  the title is "Use `-`". The fixer proposes this for every `+` mismatch, and the server's check keeps
  it only where `-` type-checks.

## 7. Tests

- **`DiagnosticFixes`:** `Propose` dispatches by code; an unknown code gives nothing.
- **`GrammarWorkspace`:** one provider is discovered; two give `NGR0004`; with none, `Fixes` is null.
- **Service, on DateCalc:**
  - `let d = 2026-02-30;` → one fix, "Change to 2026-02-28", replacing the literal;
  - `2026-13-05;` → "Change to 2026-12-05";
  - `2026-12-25 + 2026-10-05;` → "Use `-`", whose edit replaces the `+`;
  - `2 weeks + 2026-10-05;` → no fix (proposed, then rejected by the check);
  - a range away from the error → no fix;
  - a language without fixes (the Scopes test language) → none;
  - the same DC0001 fix inside a tagged C# string, with the edit at C# positions.
- **LSP:**
  - the capability is advertised;
  - a `textDocument/codeAction` request on `2026-02-30;` returns one `quickfix` with its diagnostic
    and a `WorkspaceEdit` for the document.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
