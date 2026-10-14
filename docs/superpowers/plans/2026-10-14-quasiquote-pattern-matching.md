# Typed Quasiquote Pattern Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Match typed syntax through a reusable C# API, then expose ordered pattern arms in grammar quote helpers without losing caller bindings.

**Architecture:** Compile patterns through the composed parser and compare syntax with an iterative shared matcher. Declarative helper metadata selects a result quote using that matcher; existing quotation instantiation and semantic lowering construct and evaluate the result. Editor support uses parsed capture identities and the existing composition-aware source index.

**Tech Stack:** Existing C#/.NET projects, xUnit, Nitrogen bootstrap and self-hosted grammar parsers, Roslyn source inspection, existing VS Code/LSP integration.

**Spec:** `docs/superpowers/specs/2026-10-14-quasiquote-pattern-matching-design.md` (approved 2026-10-14).

## Global Constraints

- Both interfaces, starting with the C# API; native execution in this session is already selected.
- At most one variable-length capture per physical list initially.
- Ignore trivia; compare exact token text and ordered structural children. Retain wrappers and parentheses.
- Repeated captures must be structurally equal; captures borrow the input tree.
- Match original syntax before operand rewrite expansion or lowering.
- Ordered arms require exactly one explicit final fallback.
- No second parser, textual substitution, evaluator, expression-tree representation, guards, or arbitrary C# execution.
- Read-only patterns may recognize declarations; returned construction quotes retain existing declaration restrictions.
- Diagnostic family is NQP0001–NQP0005; NP is already used by projection.
- Every declared capture must occur in real parsed holes; dollar text in opaque tokens/comments is insufficient.
- Preserve old constructors and behavior for ordinary quote helpers and RewriteSpec consumers.
- Work only in `/tmp/nitrogen-language-composition`, branch `codex/quasiquote-patterns`; preserve unrelated primary-checkout changes.
- Use repository version/build-number machinery for every commit. Do not publish another release without a release request for this feature.

## Review Focus

1. Captured syntax inside an outer rewrite still resolves caller-local names and nested proxies: Task 5 regression.
2. Two language snapshots with identical kind integers must be rejected: Task 2 snapshot test.
3. Opaque `$Name` tokens must not create captures, including overlap with lexical terminals: Tasks 1 and 6 parsed-identity tests.
4. Invalid unselected arms and unused helpers must fail composed validation without executing configured C#: Task 4 regression.
5. Empty, nested, repeated and separator-bearing list captures must retain exact item order: Task 2 list matrix.

## File ownership and execution order

Tasks 1–2 own public runtime matching. Task 3 owns syntax/model parity and refuses unsupported generation until Task 4 supplies complete metadata and validation. Task 5 executes that metadata with source provenance. Task 6 integrates editor/refactoring behavior. Task 7 supplies examples and final acceptance. Run each RED/GREEN cycle before its commit; do not dispatch implementation agents. One independent whole-branch review follows completed implementation under the preserved native workflow.

For each commit, stage only its listed files with `git add -- <paths>` and use the message at the task's final step. Check `git diff --cached --check` and `git status --short` first. The existing build identity derives its number from git history; do not maintain a parallel counter.

### Task 1: Compile and match scalar syntax

**Files:**
- Create: `Nitrogen.Runtime/Language.Patterns.cs`
- Create: `Nitrogen.Runtime/Quotation/QuotePattern.cs`
- Create: `Nitrogen.Runtime/Quotation/QuotePatternMatcher.cs`
- Create: `Nitrogen.Runtime/Quotation/PatternCompilation.cs`
- Create: `Nitrogen.Tests/QuotePatternTests.cs`
- Modify: `Nitrogen.Runtime/Quotation/QuoteTemplate.cs` only for shared source-validity utilities if necessary.

**Interfaces:**
- Consumes: `Language.ParseTemplate`, `Language.AcceptsQuoteNode`, `QuoteNode`, `QuoteArgument`, `QuoteHole`, `QuoteDiagnostic`.
- Produces: `public PatternCompilation Language.CompilePattern(string category, string text, IReadOnlyDictionary<string, QuoteHole> captures)`.
- Produces: `PatternCompilation : IDisposable` with `bool Success`, `QuotePattern? Pattern`, `IReadOnlyList<QuoteDiagnostic> Diagnostics`.
- Produces: `public QuoteMatchResult QuotePattern.Match(QuoteNode input)`; `public sealed record QuoteMatchResult(bool Matched, IReadOnlyDictionary<string, QuoteArgument> Captures, IReadOnlyList<QuoteDiagnostic> Diagnostics)`.
- Internal shared entry: `QuoteMatchResult QuotePattern.MatchResolved(QuoteNode input, Func<QuoteNode, QuoteNode> resolve)`; public Match calls the same engine without a resolver. Only Task 5 supplies a resolver for owned runtime proxies.

- [x] Add this regression using the existing QuoteLists language factory:

```csharp
[Fact]
public void Repeated_scalar_capture_uses_structural_equality()
{
    var language = TemplateListInstantiationTests.CreateLanguage();
    using var pattern = language.CompilePattern("QuoteLists.Expr", "$X + $X",
        new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
    using var same = language.ParseTemplate("1 /* trivia */ + 1", "QuoteLists.Expr", new Dictionary<string, string>());
    using var different = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
    Assert.True(pattern.Pattern!.Match(new(same.Tree, same.Tree.Root)).Matched);
    var mismatch = pattern.Pattern.Match(new(different.Tree, different.Tree.Root));
    Assert.False(mismatch.Matched);
    Assert.Empty(mismatch.Captures);
    Assert.Empty(mismatch.Diagnostics);
}
```

- [x] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~QuotePatternTests`. RED: CompilePattern does not exist.
- [x] Implement compilation: copy the map; validate syntax/extensible categories; parse through ParseTemplate; scan actual Hole/ListHole nodes and reject unused contracts. Do not invoke construction's declaration rejection. Classify parse/category failures NQP0001, contract failures NQP0002, unsupported list layouts NQP0003. List compilation remains explicitly rejected until Task 2.
- [x] Implement a work-stack matcher. A work item carries pattern/source nodes; resolve source nodes before validation when an internal resolver exists. Scalar holes validate category, capture their original QuoteNode, and compare later occurrences using an iterative structural-equality work stack. Ordinary nodes compare kinds, child counts/order and ordinal leaf text. Freeze captures only on full success; never return partial captures.

```csharp
// Matcher control flow; concrete work-item storage belongs in QuotePatternMatcher.
while (work.Count != 0)
{
    var pair = work.Pop();
    // Resolve source; capture at a parsed hole, otherwise compare kind/leaf text
    // and push corresponding children in reverse order. Any mismatch returns
    // false with empty captures and no diagnostics.
}
```

- [x] Add capture-free success, exact token spelling, retained parentheses/wrappers, subtree matching, invalid category/token category, unused capture, dictionary mutation, declaration-pattern, comments/opaque-dollar and overlapping lexical-terminal tests. Use a test grammar literal/token containing `$X` to prove it is not a parsed hole. Assert no tree or semantic state mutation.
- [x] Run the focused suite plus `QuoteApiTests` and `QuoteOwnershipTests`; GREEN: new tests and existing construction tests pass.
- [x] Commit only Task 1 files: `feat: add typed scalar quasiquote pattern matching`.

### Task 2: Deterministic list matching and ownership boundaries

**Files:**
- Modify: `Nitrogen.Runtime/Quotation/QuotePattern.cs`, `QuotePatternMatcher.cs`, `Language.Patterns.cs`
- Modify: `Nitrogen.Runtime/SyntaxTree.cs`, `ParserState.Quotation.cs` only if existing template list metadata needs an additive accessor.
- Modify: `Nitrogen.Tests/QuotePatternTests.cs`, `Nitrogen.Tests/Grammars/QuoteLists.ngr`
- Create: `Nitrogen.Tests/QuotePatternOwnershipTests.cs`

**Interfaces:** Consumes Task 1 public API and `TemplateListShape`; produces the same API with list support and full NQP0004/NQP0005 validation. No new public list tree representation.

- [x] Add a theory matching `f(1, ..$Args, 2)` against `f(1, 2)`, `f(1, 3, 2)`, `f(1, 3, 4, 2)` and `f(0, 3, 2)`. Expected matched values are true/true/true/false and captured counts 0/1/2/0. Capture item texts in source order through QuoteArgument's existing list representation.
- [x] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~QuotePattern`; RED: list pattern compilation rejects the supported pattern.
- [x] Compile each physical pattern list into fixed prefix, optional list capture and fixed suffix, recording separator layout from TemplateLists. Reject two list holes in one physical list even when names repeat. Match fixed ends then capture exactly the remaining item nodes; separators participate in layout validation but never enter captured lists. Nested lists receive independent compiled sites.
- [x] Add unseparated lists, optional/forbidden/required trailing separators, nested lists and the same list capture in two different lists. Repeated lists compare ordered item structures. Unsupported token-list sites report NQP0003, not a mismatch.
- [x] Add the following lifetime/snapshot matrix:

```csharp
[Theory]
[InlineData("disposed-pattern", "NQP0004")]
[InlineData("disposed-input", "NQP0004")]
[InlineData("out-of-range", "NQP0004")]
[InlineData("other-snapshot", "NQP0005")]
public void Invalid_input_never_returns_partial_captures(string scenario, string code)
{
    var language = TemplateListInstantiationTests.CreateLanguage();
    using var pattern = language.CompilePattern("QuoteLists.Expr", "$X",
        new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
    using var source = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
    using var foreign = TemplateListInstantiationTests.CreateLanguage()
        .ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
    var candidate = scenario switch
    {
        "out-of-range" => new QuoteNode(source.Tree, source.Tree.NodeCount),
        "other-snapshot" => new QuoteNode(foreign.Tree, foreign.Tree.Root),
        _ => new QuoteNode(source.Tree, source.Tree.Root),
    };
    var matcher = pattern.Pattern!;
    if (scenario == "disposed-pattern") pattern.Dispose();
    if (scenario == "disposed-input") source.Dispose();
    var result = matcher.Match(candidate);
    Assert.False(result.Matched);
    Assert.Empty(result.Captures);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
}
```

  Add a wrong-category test using another syntax category in the fixture. Add recovered/error/ambiguous/skipped/missing/uninstantiated-hole trees using existing parser recovery fixtures. Check validity before reading pooled storage. Public matching rejects unresolved template holes.
- [x] Verify explicit origins survive captures, absent origins do not invent paths, first repeated capture identity is retained, captured lists/maps are immutable, and captured nodes remain usable after disposing only PatternCompilation. Instantiate a replacement quote from captures, dispose input after instantiation, then bind/check/lower the owned result.
- [x] Exercise a deeply nested valid tree within parser limits and a large list; matcher/equality use iterative stacks. Record timing only as an observation, not a performance guarantee.
- [x] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuotePattern|FullyQualifiedName~QuoteOrigin|FullyQualifiedName~QuoteOwnership|FullyQualifiedName~TemplateList'`; GREEN: all selected tests pass.
- [x] Commit: `feat: match list splices with explicit capture lifetimes`.

### Task 3: Bootstrap and self-hosted match-helper syntax

**Files:**
- Modify: `Nitrogen.Grammar/GrammarModel.cs`, `GrammarLexer.cs`, `GrammarParser.cs`, `GrammarDumper.cs`
- Modify: `Nitrogen.Ngr/Nitrogen.ngr`, `GrammarSemantics.cs`, `NgrProjector.cs`
- Modify: `Nitrogen.Grammar/QuoteHelperAnalysis.cs`, `QuoteHelperWriter.cs`, `DeclarativeWriter.cs`
- Modify: `Nitrogen.Tests/Grammar/QuoteHelperGrammarTests.cs`, `SelfHostingTests.cs`

**Interfaces:** Keep `QuoteHelperDecl(..., QuoteText Body, ...)` compatible; Body remains the required fallback quote. Add `QuoteHelperMatch? Match { get; init; }`.

```csharp
public sealed record QuoteHelperMatch(NameDecl Selector,
    EquatableArray<QuoteMatchArm> Arms, GrammarSpan Span);
public sealed record QuoteMatchArm(EquatableArray<QuoteParameter> Captures,
    QuoteText Pattern, QuoteText Result, GrammarSpan Span);
```

Nonfallback arms are ordered in Arms. Fallback's actual source remains Body.Span; parser detects duplicate/nonfinal/missing fallback before constructing a valid match body. No dummy quote bodies.

- [x] Add bootstrap and self-host parity fixtures containing:

```ngr
syntax function Swap(Value: Expr) : Expr = match Value
{
  | (Left: Expr, Right: Expr) <[ $Left + $Right ]> => <[ $Right + $Left ]>;
  | _ => <[ $Value ]>;
};
```

  Also cover `Items: Expr*`, a capture-free arm, comments/blank lines, malformed arrows and ordinary helpers named `match` where legal. Compare all arm captures, quotes and spans in the model dump.
- [x] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuoteHelperGrammarTests|FullyQualifiedName~SelfHostingTests'`; RED: parser rejects match body.
- [x] Add Arrow recognition before Equals; preserve normal assignment and raw semantic-block parsing. Parse match selector/braces/arms and mandatory final fallback. Extend the self-host grammar and semantic operations; NgrProjector must project typed HIR rather than reparse through the bootstrap parser.
- [x] Extend GrammarDumper and every helper-model visitor to include Match. Until Task 4 supplies semantics, QuoteHelperAnalysis emits a clear unsupported-match diagnostic and both writers refuse emitting a fallback-only helper. This temporary refusal prevents a parser-only commit from silently changing program behavior; Task 4 removes it.
- [x] Add missing/duplicate/nonfinal fallback, scalar/list capture syntax and existing simple-helper regressions. Review changed recovery/parity snapshots individually.
- [x] Run the same focused command; GREEN: parsing/parity tests pass and generation explicitly refuses new match helpers.
- [x] Commit: `feat: parse typed quote helper match arms`.

### Task 4: Declarative metadata and complete composed validation

**Files:**
- Modify: `Nitrogen.Runtime/Quotation/QuoteHelper.cs`, `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`
- Create: `Nitrogen.Runtime/Quotation/QuoteMatchPlan.cs`
- Modify: `Nitrogen.Runtime/Language.Quotation.cs`, `Language.Patterns.cs`, `Language.cs`
- Modify: `Nitrogen.Grammar/QuoteHelperAnalysis.cs`, `QuoteHelperWriter.cs`, `DeclarativeWriter.cs`
- Modify: `Nitrogen.Workspace/QuoteHelperValidation.cs`
- Modify: `Nitrogen.Tests/Grammar/QuoteHelperValidationTests.cs`, `Nitrogen.Tests/Workspace/QuoteHelperValidationTests.cs`

**Interfaces:** Keep existing positional records compatible. Add `QuoteHelperMatchSpec? QuoteHelper.Match { get; init; }`, `string? RewriteSpec.MatchHelperName { get; init; }`, and `QuoteHelperSource? QuoteHelperError.Source { get; init; }`.

```csharp
public sealed record QuoteHelperMatchSpec(string Selector, string SelectorCategory,
    IReadOnlyList<QuoteMatchArmSpec> Arms);
public sealed record QuoteMatchArmSpec(string Pattern,
    IReadOnlyDictionary<string, QuoteHole> Captures, string Result,
    QuoteHelperSource PatternSource, QuoteHelperSource ResultSource);
```

Internal `QuoteMatchPlan` owns compiled patterns/result templates plus fallback. `Language.MatchPlan(string qualifiedHelperName)` returns one snapshot-local lazily cached plan, or its validation diagnostics; key by qualified name, not record reference identity. Workspace validation may compile/dispose a temporary plan. Never share plans across Language instances.

- [x] Add a workspace fixture with an unused match helper whose second arm has an invalid result category and a configured C# initializer that throws if executed. Assert validation reports the second arm's grammar path/span and does not execute the initializer. Repeat with an unused invalid pattern.
- [x] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~QuoteHelperValidationTests`; RED: metadata/validation cannot represent all arms.
- [x] Validate selector is a scalar helper parameter; captures are unique, do not shadow params, have real categories and correct scalar/list shapes. Compile patterns with arm captures only and results with the union of parameters/captures. Compile fallback with parameters only. Enforce real parsed capture occurrences and result construction restrictions for every helper, even unused ones.
- [x] Emit the new declarative records and qualified MatchHelperName through shared writer routines. A result is not emitted as unconditional fallback. Remove Task 3's unsupported-feature refusal. While execution is not installed until Task 5, instance creation must report an explicit unsupported-match error for MatchHelperName, preserving ordinary helper behavior.
- [x] Route NQP diagnostics with PatternSource/ResultSource to CompositionDiagnostic locations. Reserve grammar diagnostics after the current registry's last code; add registry uniqueness assertions rather than guessing a conflicting number. MatchPlan cache stores immutable compiled plans for its owning language lifetime; transient validation compilations are disposed on every path.
- [x] Test missing selector, list selector, capture shadowing, duplicate captures, arm-local result misuse, wrong categories/shapes, unused declarations, opaque dollar text, unknown helper and imported helper ambiguity. Assert existing ordinary metadata/constructors still work.
- [x] Run grammar/workspace validation and source-generator tests; GREEN: all arms validate without configured C# execution and invalid metadata cannot fall back silently.
- [x] Commit: `feat: validate and emit declarative pattern helpers`.

### Task 5: Ordered selection with caller-aware syntax provenance

**Files:**
- Modify: `Nitrogen.Runtime/Semantics/FileSemantics.Rewrites.cs`, `FileSemantics.cs`
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs`, `HirLowering.cs`
- Modify: `Nitrogen.Runtime/Quotation/QuoteInstantiator.cs`, `QuoteTemplate.cs`, `QuoteMatchPlan.cs`
- Modify: `Nitrogen.Tests/Grammars/QuoteHelpers.ngr`, `Nitrogen.Tests/QuoteHelperRuntimeTests.cs`, `QuoteOriginTests.cs`

**Interfaces:**
- Consume Task 4 MatchPlan and Task 1 `QuotePattern.MatchResolved`.
- Add internal `sealed record RewriteSource(FileSemantics File, int Node)`.
- Replace internal single-owner Holes tuple with `IReadOnlyDictionary<int, RewriteSource>? Holes`.
- Add internal `IReadOnlyDictionary<int, QuoteNode> QuoteInstantiation.ProxyNodes`; retain existing Proxies for compatibility.
- Add internal `LoweringContext ForSource(FileSemantics source)` to preserve reporting, admission, snapshot, active templates and applicable argument/hole contexts.

- [ ] Add two overlapping matching arms followed by fallback. Assert first-arm precedence and fallback parity against explicit syntax. Add a rewrite operand whose own expansion would match a different arm; assert matching observes its original syntax.
- [ ] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~QuoteHelperRuntimeTests`; RED: explicit unsupported-match execution diagnostic.
- [ ] Bind helper arguments through existing RewriteHole child/stride metadata. Resolve the selector's proxy root to original syntax; call each compiled arm's shared matcher in order, merge parameters with that arm's captures, and instantiate only the chosen result. An ordinary mismatch tries the next arm; invalid matching input propagates diagnostics. Use the fallback only after all ordinary mismatches.
- [ ] Preserve provenance per inserted node: QuoteInstantiator records original QuoteNode in ProxyNodes. Runtime maps its tree to the owning FileSemantics and builds RewriteSource entries. Its resolver follows owned Hole proxies iteratively, detects cycles, and refuses foreign/unowned storage. Composite nodes can retain authored outer-quote structure while comparison resolves their proxy descendants. Public Match still rejects raw unresolved holes.
- [ ] Update property/type proxy reads to dispatch to each RewriteSource.File. Lowering caches by `(FileSemantics, Node)`, not integer node alone. ForSource first finds an existing context for that owner in the caller chain; otherwise derives one sharing diagnostics/active-template state and the owner's proxy environment. Preserve argument maps and rewrite diagnostic attribution. Do not pre-lower selector operands to choose arms.
- [ ] Add caller-bound names inside scalar/list captures, nested ordinary-to-match and match-to-match helpers, composite authored selectors containing proxy descendants, multiple source owners with colliding node integers, repeated captures and source-origin assertions. Compare bind/check/lower results with explicit syntax, including unresolved caller references. Test required-list construction failure and recursive rewrite NH0008 still terminate.
- [ ] Selected-arm diagnostics identify the selected result's source, plus the existing use-site expansion context. Never report the fallback as the source of a selected arm failure. Dispose per-use constructed trees through existing ownership paths.
- [ ] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuoteHelperRuntimeTests|FullyQualifiedName~QuoteOrigin|FullyQualifiedName~QuoteOwnership|FullyQualifiedName~Rewrite'`; GREEN: parity and existing rewrite tests pass.
- [ ] Commit: `feat: execute ordered quote patterns with caller bindings`.

### Task 6: IntelliSense, rename and safe refactoring

**Files:**
- Modify: `Nitrogen.LanguageService/QuoteHelperSourceIndex.cs`, `NitrogenLanguageService.QuoteHelpers.cs`, `NitrogenLanguageService.Quasiquotes.cs`
- Modify: `Nitrogen.LanguageService/CSharpQuotations.cs`, `NitrogenLanguageService.CSharpQuotations.cs`
- Modify: `Nitrogen.Grammar/QuoteHelperExtraction.cs`, `GrammarExtraction.Transform.cs`
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.QuoteHelperExtraction.cs`
- Modify: `Nitrogen.Tests/LanguageService/QuoteHelperNavigationTests.cs`, `CSharpQuoteNavigationTests.cs`, `QuoteHelperExtractionTests.cs`, `GrammarExtractionParityTests.cs`

**Interfaces:** Preserve existing LSP entry points and source-index identities; extend identity with helper/arm/capture scope. CSharpQuotations recognizes actual `Nitrogen.Language.CompilePattern` symbol and its `category`, `text`, `captures` arguments, alongside CompileQuote.

- [ ] Add definition/reference/rename cases where two arms both declare Left. Rename in arm 1 changes only its declaration and parsed holes in its pattern/result; arm 2 and a token spelling `$Left` remain untouched. Selector navigation points to the helper parameter. Pattern keyword/category navigation points to the composing grammar.
- [ ] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuoteHelperNavigationTests|FullyQualifiedName~CSharpQuoteNavigationTests'`; RED: match-arm contexts are absent.
- [ ] Index pattern quotes with arm captures and result quotes with params plus captures. Compile these contexts with the appropriate pattern/construction API. Preserve connected-composition resolution and signed-file source offsets. Validate renamed text by reparsing all affected compositions and checking helper/capture identity, shape and binding stability.
- [ ] Add C# API recognition for constant regular/verbatim/raw/concatenated strings and constant capture maps. Refuse same-named unrelated methods and unresolved/nonconstant contracts using existing conservative behavior. Do not execute helper code to inspect patterns.
- [ ] Add completion and diagnostics for selector, capture declarations, categories, holes and pattern keywords. Verify all occurrence ranges are actual source coordinates, including comment signatures and blank lines.
- [ ] Refactoring keeps simple inline-return extraction working. Refuse extraction of subpatterns or whole match helpers where the current transform cannot preserve arm scopes; provide an actionable reason. Whole grammar-block extraction may proceed only when every arm, fallback, import and helper call is preserved. Test apply-all and preview through existing LSP edit tests.
- [ ] Run language-service quote/navigation/extraction suites; GREEN: navigation and rename scope are correct and unsupported moves refuse without edits.
- [ ] Commit: `feat: add pattern-aware navigation and refactoring guards`.

### Task 7: Usable examples, documentation and acceptance

**Files:**
- Modify: `README.md`, `docs/editor-support.md`
- Create: `examples/DateCalc/PatternMatching.cs`
- Modify: `examples/DateCalc/DateCalc.ngr`
- Create: `examples/DateCalc/pattern-matching.datecalc`
- Create: `Nitrogen.Tests/DateCalc/DateCalcPatternTests.cs`
- Modify: approved spec status and this plan's execution checkboxes.

**Interfaces:** Only the APIs/syntax delivered above. C# demo exposes `public static bool TrySwapAddition(Language language, QuoteNode input, out QuoteInstantiation? result)` using CompilePattern, Match and CompileQuote/Instantiate; caller owns successful result. Grammar demo exposes an explicitly suffixed rewrite so existing programs retain their behavior.

- [ ] Add an end-to-end test that matches an addition, swaps its operands structurally, instantiates and lowers the result with caller-bound values. Add a list example using the QuoteHelpers fixture to demonstrate fixed prefix/suffix plus an empty/nonempty capture. Tests assert HIR structure and origins, not just rendered text.
- [ ] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~DateCalcPatternTests`; RED: example/helper not present.
- [ ] Write the C# demo and `.ngr` match helper with explicit fallback. README documents scalar/list capture syntax, repeated-name equality, snapshot/lifetime ownership, declaration matching versus construction restrictions, diagnostic codes and supported editor actions. Clearly label absent guards/multiple list captures/native semantic-model matching. Explain source text is not rewritten automatically by matching.
- [ ] Run the focused test; GREEN: examples parse/bind/check/lower through the real pipeline.
- [ ] Run repository Release build and full tests from the worktree: `dotnet build Nitrogen.slnx -c Release` then `dotnet test Nitrogen.slnx -c Release --no-build`. Run `npm test` from `editors/vscode`. Preserve logs and report exact counts and any baseline failures separately.
- [ ] Run `./build.sh` for generic editor artifacts. Run `dotnet pack Nitrogen.slnx -c Release --no-build -o /tmp/nitrogen-pattern-packages` for local NuGet artifacts; inspect runtime/generator/CLI/Build packages and generic editor metadata. Install packages into a disposable consumer using existing package-smoke conventions and compile a C# CompilePattern call plus a grammar match helper; no project references. This step verifies distribution, not publication.
- [ ] Perform native VS Code acceptance in the already approved scratch folder only: type a match helper, navigate selector/capture/keyword/category, rename one arm capture, introduce an invalid unused arm, fix it, and exercise a generated-language sample with development reload. Verify native syntax coloring remains correct for augmented files and diagnostics are not duplicated. If native control is unavailable, provide these exact manual checks and report UI verification separately from automated checks.
- [ ] Obtain one independent whole-branch review under the selected native workflow; fix actionable findings and repeat only affected checks. Do not claim UI success from builds.
- [ ] Commit: `docs: demonstrate typed quasiquote pattern matching`. Report implemented scope, validation and limitations. PR/merge/release requires the user's next integration instruction.

## Inline plan self-review

- Spec C# compilation, equality, capture use, declaration recognition and diagnostic contract: Tasks 1–2.
- Spec list shapes, storage validity, snapshot identity, capture origins and iterative traversal: Task 2.
- Spec helper syntax, selector/capture scope, ordered arms and fallback: Tasks 3–5.
- Spec all-arm/all-helper validation, source-safe workspace validation and declarative emission: Task 4.
- Spec original-syntax selection, binding preservation, construction restrictions and cycle guards: Task 5.
- Spec grammar/C# editor recognition, connected compositions, signed files and refactoring boundaries: Task 6.
- Spec real examples, full checks, distributable consumer and native editor evidence: Task 7.
- Review Focus entries each have an explicit owning regression above. Public signatures and internal provenance signatures are fixed in their producing tasks.
