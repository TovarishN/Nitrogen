# Inlay-hint evaluation: show each statement's value in the editor

Status: approved design (2026-10-07). Proven with `examples/DateCalc`.

## Problem

`nitrogen generate vscode` and `generate rider` turn a `nitrogen.json` language into an editor plugin
with coloring, diagnostics, hover, completion, outline and rename. For a language that computes
values, such as DateCalc ("each line shows its value"), the editor never shows them.
`DateCalcEvaluator.Run` parses, checks, lowers and projects a source through host handlers, but only
tests call it. The language server cannot run a language's typed HIR, and it implements neither
`textDocument/inlayHint` nor code lens.

## Goal

A workspace language whose helper sources export an evaluation profile shows, after each selected
statement, the value that statement evaluates to, as an LSP inlay hint. This works the same in VS Code
and Rider and needs no plugin-specific code. With `examples/DateCalc` open, `sample.datecalc` shows
the eight values that `DateCalcTests.Sample_shows_each_statement_value` expects today.

Out of scope: values inside tagged C# strings (`/*lang=datecalc*/`), showing values on hover, a
`today` builtin or any other effectful capability, and out-of-process evaluation.

## 1. Contract: `EvaluationProfile`

A new public type in `Nitrogen.Runtime/Semantic/EvaluationProfile.cs`:

```csharp
/// <summary>How an editor shows the values of a language's statements: which syntax kinds are
/// statements, the host handlers that project their HIR, the values of builtin symbols, and how a
/// value reads as text.</summary>
public sealed class EvaluationProfile(
    IReadOnlySet<int> statementKinds,
    Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
    Func<Symbol, ProjectedValue?> builtins,
    Func<ProjectedValue, string> format)
{
    public IReadOnlySet<int> StatementKinds { get; }
    public ProjectionRegistry Bind(SemanticCatalog catalog); // new ProjectionRegistry(catalog, handlers(catalog))
    public ProjectedValue? Builtin(Symbol symbol);
    public string Format(ProjectedValue value);
}
```

- Handlers are built from the composed catalog (`catalog.Operations["DateCalc.Add"]`), not from
  signatures the helper looked up in a language of its own. So the profile does not need a second
  `Language` in the workspace's load context.
- `Bind` relies on the existing `ProjectionRegistry` checks: a handler must match its catalog
  signature exactly and appear only once.

## 2. Discovery

- `GrammarWorkspace.Compile` scans the helper sources' public static fields and properties of type
  `EvaluationProfile`, next to its scan for `ModuleDescriptor`/`SemanticModule`.
  - With exactly one profile, it calls `profile.Bind(language.SemanticCatalog)`. A throwing `Bind` is a
    whole-language workspace diagnostic `NGR0002`, and the language compiles without a profile.
  - Two or more profiles are diagnostic `NGR0003`, and none is used.
- `WorkspaceSnapshot` gains `EvaluationProfile? Evaluation` and `ProjectionRegistry? EvaluationRegistry`.
  Both are cleared on `Dispose`.
- `LanguageEntry` gains an optional `Evaluation` (profile plus bound registry), so a built-in C#
  language can register one too. `NitrogenLanguageService.Compile` copies it from the snapshot.
- Evaluation is on whenever a profile is found. Users turn hints off with the editors' own controls:
  `editor.inlayHints.enabled` (which can be set per language) in VS Code, and Settings › Editor ›
  Inlay Hints in Rider. There is no Nitrogen setting.

## 3. Language service

```csharp
public sealed record ValueHint(DocumentPosition At, string Label, string? Tooltip, bool IsError);

public IReadOnlyList<ValueHint> ValueHints(string uri, DocumentRange range);
```

A new `NitrogenLanguageService.Evaluation.cs` partial:

1. It returns `[]` when the document is not open, its `LanguageEntry` has no evaluation, or
   `Diagnostics(uri)` contains an error. A document with an error shows no values, the same gate
   `DateCalcEvaluator.Run` applies, so a half-typed line never shows a stale or misleading value.
2. It lowers with `HirLowering.LowerSelected(file, profile.StatementKinds, snapshotId)`, not the
   inspection cache. `InspectDocument` lowers every registered root, and a statement without its own
   `lowers` clause (DateCalc's `Show`) is not one. If this lowering reports diagnostics, it returns `[]`.
3. For each root in source order, it collects the builtin symbols the root references
   (`HirSymbolRef` with `Binding.IsBuiltin`), maps them through `profile.Builtin`, and calls
   `HirProjector.Project(root, registry, inputs)`.
   - **Value:** label `= {profile.Format(value)}`, at the end of the statement's source span (the
     statement node of `root.Origins[0].Node`, so after DateCalc's `;`), no tooltip.
   - **Projection diagnostics:** label `= ⚠`, with the tooltip set to the messages joined by `; ` and
     `IsError` set.
   - **Handler exception:** caught and treated as one projection diagnostic with the exception
     message. Later statements still evaluate.
   - A builtin with no value (`profile.Builtin` returns null) is reported by `Preflight` as a
     projection diagnostic, as above.
4. **Budget.** It evaluates for at most 250 ms per document, checked between roots. Roots after the
   cutoff get no hint. A handler that never returns cannot be interrupted in-process. That is the same
   exposure helper code (semantic rules, static initializers) already has in the server. An
   out-of-process evaluator is the remedy, outside this design.
5. **Cache.** All hints of a document are cached by (document version, project version,
   `LanguageEntry`), invalidated like `_inspection`. `range` only filters the cached list: a hint is
   returned when `At` lies within `range`.

## 4. LSP

- `ServerCapabilities` advertises `inlayHintProvider: true`.
- `textDocument/inlayHint` returns `InlayHint[]` built from `ValueHints(uri, range)`: `position`,
  `label` (a string), `paddingLeft: true`, no `kind`, and `tooltip` when set.
- After a workspace recompile changes a language's `LanguageEntry`, and the client advertised
  `workspace.inlayHint.refreshSupport`, the server sends `workspace/inlayHint/refresh` once. That
  happens on `nitrogen.json`, grammar, or helper-source changes.
- New message records go in `LspMessages.cs` and the source-generated `LspJson` context.

## 5. Editors

- **VS Code:** `vscode-languageclient` 9 requests inlay hints whenever the server advertises them. No
  change is expected. The plan verifies this against the generic and generated extensions.
- **Rider:** the platform LSP client (`com.intellij.platform.lsp`) supports inlay hints. The plan
  checks whether Rider 2026.2 requires an explicit customization in `NitrogenLspSupport`. If it does,
  the generic plugin and `RiderPluginRenderer` both get that one-line opt-in, covered by
  `RiderPluginGenerationTests`.

## 6. DateCalc

- `DateCalcEvaluator` exposes `public static readonly EvaluationProfile Profile`:
  - statement kinds `{ DateCalcKinds.Let, DateCalcKinds.Show }`;
  - handlers from today's list, built with the passed catalog;
  - builtins from `MathModule.Constants`;
  - formatting with today's `Show`.
- `Run` becomes a thin wrapper that binds `Profile` to its own `Language`'s catalog and projects
  with it. Its output is unchanged, so `DateCalcTests` pass without edits. `Language` stays in
  `DateCalcEvaluator` for `Run`. If its static initializer conflicts with loading in the workspace,
  it moves behind a lazy member that only `Run` touches.
- `examples/DateCalc/nitrogen.json` adds `DateCalcEvaluator.cs` to `sources`.

## 7. Tests

- **`EvaluationProfile`:** `Bind` accepts exact handlers; rejects a mismatched or duplicate one;
  `Builtin` and `Format` delegate.
- **`GrammarWorkspace`:** one profile is discovered and bound; two give `NGR0003`; a throwing `Bind`
  gives `NGR0002` and the language still compiles; with no profile, `Evaluation` is null.
- **Service** (DateCalc through its `nitrogen.json`):
  - `sample.datecalc` yields eight hints with the expected labels, each at the end of its statement
    on lines 2–9;
  - `range` filtering;
  - no hints while any error diagnostic is present (`date + date`);
  - a throwing handler gives one `⚠` hint while the following statements still show values;
  - an exhausted budget truncates (injectable clock or budget);
  - an edit invalidates the cache;
  - a language without a profile returns `[]`.
- **`LspServerTests`:**
  - the capability is advertised;
  - an `inlayHint` request round-trips with a range;
  - a helper-source change sends `workspace/inlayHint/refresh` only when the client supports it.
- **Rider generation:** only if section 5 requires a customization.

Run `dotnet build Nitrogen.slnx` (warning-free) and `dotnet test Nitrogen.Tests`. If editor code
changes, also run `./build.sh`.
