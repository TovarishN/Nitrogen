# Value hints in tagged C# strings

Status: implemented (2026-10-07). Builds on
[inlay-hint evaluation](2026-10-07-inlay-hint-evaluation-design.md).

## Problem

A workspace language with an evaluation profile shows each statement's value as an inlay hint in its
own files (#23). The same language inside a tagged C# string (`/*lang=datecalc*/ "..."`, or
`// language=datecalc` before the statement) is served for diagnostics, colouring, completion, hover
and navigation, but shows no values. `ValueHints` answers only open language documents. A C# file is a
*host*, and its tagged strings are virtual documents that the editor never addresses directly.

## Goal

In an open `.cs` file, each tagged string whose language has an evaluation profile shows its
statements' values as inlay hints, in VS Code and Rider. With `examples/DateCalc/Snippets.cs` open:

```csharp
DateCalcEvaluator.Run(/*lang=datecalc*/ """
    let christmas = 2026-12-25;          = 2026-12-25 Fri
    (christmas - 2026-10-05) in days;    = 81
    weekday(christmas);                  = Friday
    """);

const string Deadline = "2026-10-05 + 6 weeks;"  = 2026-11-16 Mon
```

Out of scope: values on hover, and interpolated strings (the scanner already skips them).

## 1. Literal end in `EmbeddedString`

`EmbeddedString(string Tag, string Value, int[] Map)` gains `int End`, the host offset just past the
literal's closing delimiter (`"`, `"` of a verbatim string, or the last `"` of a raw string's closing
quotes). `EmbeddedStrings.Find` sets it for regular, verbatim and raw literals. `Map` is unchanged.

## 2. Service: hints on a host

`NitrogenLanguageService.ValueHints(uri, range)` gains a host branch, after the pattern of
`HostDiagnostics` and `HostTokens`:

1. If `uri` is an open host, then for each of its embedded documents, in order, take
   `ValueHints(embedded, whole document)` and map each hint into the host (step 2). Then return the
   hints whose host position lies within `range`.
2. **Placement:**
   - **One-line literal:** when the opening and closing delimiters are on one host line (the host
     lines of `Map[0]` and `End`), every hint of that string goes at `End`, after the closing quote.
     Two statements in one such string give two hints at that position, in statement order.
   - **Multi-line literal:** otherwise the hint goes at its statement end, mapped through the string's
     source map (`OutOf`). In a raw or verbatim multi-line string, that is the end of the statement's
     line inside the string.
3. **Errors:** a hint's label, tooltip and error flag are unchanged. Each embedded document's hints
   are already gated on its own diagnostics, so a string with an error hides only its own values;
   other strings in the file still show theirs.
4. **Caching:** none is added. Each embedded document's hints are cached by its own versions, and
   mapping them into the host is cheap.
5. **Skipped languages:** a string whose language this server skips (`skipLanguages`, because another
   plugin carries it) is not embedded, so it gets no hints from this server.

## 3. LSP

No change. `textDocument/inlayHint` already calls `ValueHints` for any URI. Editors request hints
again after a host edit, and `workspace/inlayHint/refresh` already follows a language recompile.

## 4. Editors

- **VS Code:** no change. The client's document selector already includes C# files
  (`csharpSelector`), and the `CSharpStrings` middleware does not intercept inlay hints.
- **Rider:** `NitrogenCSharpClient` sets `inlayHintCustomizer` to `NitrogenInlayHints` instead of
  `LspInlayHintDisabled`. Its class comment drops "hints" from the features left to Rider and adds
  values to those served in strings. The server answers only inside tagged strings, so it doesn't
  compete with Rider's own C# hints. `NitrogenCSharpStrings.kt` is embedded verbatim in generated
  Rider plugins, so they get the same change.

## 5. Tests

- **`EmbeddedStringTests`:** `End` for a regular, a verbatim and a raw multi-line literal.
- **Service, on a C# host with DateCalc strings** (DateCalc configured through its `nitrogen.json`):
  - a one-line string's hint is after its closing quote;
  - a raw multi-line string gets one hint per statement, at each statement's line end inside the string;
  - a string with an error shows no values, while another string in the same file still does;
  - `range` filters by host position;
  - an edit to the host shows the new values;
  - a skipped language shows no values.
- **`LspServerTests`-style session:** an `inlayHint` request on a `.cs` URI returns host positions.
- **`RiderPluginGenerationTests`:** the generated `NitrogenCSharpStrings.kt` uses `NitrogenInlayHints`
  for its inlay hint customizer.
- **Manual:** the stdio probe on `examples/DateCalc/Snippets.cs` through the built `nitrogen lsp`.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`, and build the Rider
plugin (`gradle buildPlugin` in `editors/rider`).
