# Values on hover

Status: implemented (2026-10-07). Builds on
[inlay-hint evaluation](2026-10-07-inlay-hint-evaluation-design.md) and
[value hints in tagged C# strings](2026-10-07-csharp-string-value-hints-design.md).

## Problem

A language with an evaluation profile shows each statement's final value as an inlay hint. Nothing
shows the value of a part of a statement: in `weekday(start + 3 * sprint);`, there's no way to see
what `3 * sprint` or `start + 3 * sprint` is. Hover already finds the lowered expression under the
cursor and describes its type and operation, but not its value.

## Goal

Hovering an expression in a language with an evaluation profile adds its value to the hover, in the
language's own files and in tagged C# strings. Hovering `3 * sprint` in `sample.datecalc`:

```
`DateCalc.Duration` · `DateCalc.Times` · `DateCalc.Weekday`
= 42 days
```

Out of scope: values for syntax that doesn't lower on its own (a whole `let` statement), and any
formatting beyond the profile's own formatter.

## 1. Behaviour

- The value is a new line under the existing summary: `= {profile.Format(value)}`. The hover range is
  unchanged (the hovered expression's range).
- **Literals:** the value is that of the outermost lowered node at the hovered node's source span. A
  date literal's inner text has the same span as its `DateCalc.Date` operation, so it shows
  `= 2026-10-05 Mon`. A number literal (`HirConstant`) already shows `= 2` in its summary, so it gets
  no value line, and neither does a text constant.
- **Names:** a name gets a value line only when the hovered node is itself a reference
  (`HirSymbolRef`), as for a builtin such as `pi`. DateCalc lowers a `let` reference by inlining its
  initializer, whose origin is the declaration, so nothing lowered sits under such a name. It keeps
  today's declaration hover with no value line; its value is the inlay hint on its declaration line.
- **Failure:** when projection fails, the line is `= ⚠ {reason}`, where the reason is the same text an
  error hint's tooltip shows (`NP0005: …`; diagnostics joined by `; `).
- **No line** when the document's language has no evaluation, or nothing lowers at the position.
  The hover is then exactly what it is today.
- **Errors elsewhere don't hide the value.** Hover is computed on request, so there's no stale-value
  risk, and lowering refuses an expression that contains or depends on an error. A value only
  appears for an expression that lowered. This differs from inlay hints on purpose: hints are hidden
  while the document has any error.

## 2. Service

- **Shared projection.** `NitrogenLanguageService.Evaluation.cs` gets one helper that projects a HIR
  node with a `BoundEvaluation` and returns either the formatted value or a failure reason:
  - it gathers the builtin inputs the node references, as hints do today;
  - it calls `HirProjector.Project`;
  - it catches an exception from the profile's `Builtin` or `Format`.

  The hint code (`Hint`) is rewritten on top of it, so labels, failure text and the exception guard
  are the same for hints and hover.
- **Hover.** In `Hover`, after building the summary: if the document's `LanguageEntry.Evaluation` is
  set and `inspection.Node` is not a `HirConstant`, project `inspection.Node` and append
  `"\n\n= " + value` or `"\n\n= ⚠ " + reason` to the summary.
- **No cache and no budget.** It's one projection, made when the user hovers.
- **Tagged C# strings:** no change. A host hover is already answered by the string's own document and
  mapped back.

## 3. LSP and editors

No change. The value is one more line in the existing `textDocument/hover` markdown.

## 4. Tests

On DateCalc, configured through its `nitrogen.json`:

- `3 * sprint` in `sample.datecalc` → the hover contains `= 42 days`;
- a date literal (`2026-10-05`) → `= 2026-10-05 Mon`;
- a builtin (`pi`) → `= 3.1415927`;
- a `let` reference (`sprint` in `start + sprint`) → keeps its declaration text, no value line;
- a number literal (`3`) → no line starting `= ` other than the summary's own `= 3`;
- `9999-12-31 + 1 days` hovered at `+` → `= ⚠ NP0005:`;
- an error on another line (`2026-10-05 + 2026-10-06;` after a valid statement) → the valid
  statement's sub-expression still shows its value;
- a language without a profile (the Scopes test language) → hover text identical to before;
- the same `3 * sprint` hover inside a tagged C# string → `= 42 days`.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
