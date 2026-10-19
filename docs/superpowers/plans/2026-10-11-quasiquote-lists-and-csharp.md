# List splices and C# syntax quotation implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Implement typed list splices in rewrite templates, then a C# quotation API returning independently owned syntax trees with editor support.

**Architecture:** Extend generated list parsing with explicit list-hole placeholders and list-site metadata. One structural instantiator flattens lists and normalizes separators, using semantic proxies for grammar rewrites and copied syntax for C# quotation. Generated category metadata validates nodes without reparsing their text.

**Tech Stack:** C#, existing Nitrogen generated parser, pooled immutable SyntaxTree, Roslyn for C# editor recognition, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-11-quasiquote-lists-and-csharp-design.md` (approved by user on 2026-10-11).

## Global constraints

- Work only in `/Users/dmitrikamenetski/work/Nitrogen`; preserve prior navigation changes and unrelated work.
- Deliver and verify list splices before adding the C# quotation surface.
- Keep the scalar `ParseTemplate(string, string, IReadOnlyDictionary<string, string>)` overload working.
- Support direct nonterminal repetitions and separated lists with literal separators in this slice.
- Reject optional, token, grouped tuple, and nonliteral-separator splice shapes explicitly.
- Preserve atomic scalar-splice precedence; no string replacement followed by reparsing.
- Preserve existing rewrite binding, property forwarding, cycle guards, and item diagnostics.
- No template-authored declarations, hygiene, special C# compiler syntax, or implicit execution.
- No commits, pushes, or installed-plugin updates during plan preparation. Execution should preserve task boundaries and avoid staging earlier navigation changes without user authorization.

## Review focus

1. A splice of an alias category must accept every permitted alternative and reject unrelated nodes with the same text.
2. All-empty splices in a `+` destination must fail without leaving invalid list layout or indexing missing items.
3. C# results must survive disposal of source trees and compiled templates without retaining pooled storage.
4. A failed grammar edit must not make editor requests throw against the last good snapshot.
5. Apparent splice markers inside strings/comments must remain ordinary language text.

## File and responsibility map

- `Nitrogen.Runtime/Quotation/QuoteTypes.cs`: hole shapes, node/list arguments, diagnostics, origin records.
- `Nitrogen.Runtime/Quotation/QuoteTemplate.cs`: compile-result/template ownership and instantiation entry points.
- `Nitrogen.Runtime/Quotation/QuoteInstantiator.cs`: structural construction and list normalization in copy/proxy modes.
- `Nitrogen.Runtime/Quotation/QuoteOrigins.cs`: explicit source remapping for constructed results.
- `Nitrogen.Runtime/Language.Quotation.cs`: CompileQuote and generated category compatibility lookup.
- `Nitrogen.Runtime/ParserState.Quotation.cs`: template-only list markers and list-site bookkeeping.
- `Nitrogen.Runtime/Language.cs`, `ParserState.cs`, `SyntaxKinds.cs`, `SyntaxTree.cs`, `BuildArena.cs`, `TreeFreezer.cs`: minimal hooks, metadata lifetime, typed template overload.
- `Nitrogen.Grammar/QuotationWriter.cs`: emit nonterminal category membership and supported list metadata.
- `Nitrogen.Grammar/GrammarValidator.cs`, `GrammarDiagnostic.cs`, `DeclarativeWriter.cs`, `SyntaxCodeWriter.cs`, `ModuleWriter.cs`: validation and generated integration.
- `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`, `DeclarativeTypes.cs`, `Semantics/FileSemantics.Rewrites.cs`: list-aware rewrite instantiation using existing scalar proxy semantics.
- `Nitrogen.LanguageService/NitrogenLanguageService.Quasiquotes.cs`: grammar list-splice editor behavior.
- `Nitrogen.LanguageService/CSharpQuotations.cs`, `NitrogenLanguageService.CSharpQuotations.cs`, `EmbeddedStrings.cs`: constant C# quote-call recognition, value/source maps, editor routing.
- `Nitrogen.Tests/Grammars/QuoteLists.ngr`, quotation/runtime/language-service test files: parser, semantic, ownership, editor, and integration regressions.
- `docs/language-guide.md`: tested grammar and C# usage examples and explicit limitations.

## Shared interfaces

Use namespace `Nitrogen` for the construction API. Use records for immutable data and sealed disposable classes for owned results.

```csharp
public sealed record QuoteHole(string Category, bool IsList)
{
    public static QuoteHole Node(string category) => new(category, false);
    public static QuoteHole List(string category) => new(category, true);
}

public sealed record QuoteNode(SyntaxTree Tree, int Node, SourceOrigin? Origin = null);
public abstract record QuoteArgument
{
    public sealed record Scalar(QuoteNode Value) : QuoteArgument;
    public sealed record Many(IReadOnlyList<QuoteNode> Values) : QuoteArgument;
    public static QuoteArgument Node(QuoteNode value) => new Scalar(value);
    public static QuoteArgument List(IReadOnlyList<QuoteNode> values) => new Many(values);
}

public sealed record QuoteDiagnostic(string Code, TextSpan Span, string Message, string? Hole = null);
public sealed record TemplateListShape(string Category, int MinimumCount, string? Separator);
```

`QuoteCompilation : IDisposable` owns parsed template storage and exposes bool Success, IReadOnlyList<QuoteDiagnostic> Diagnostics, and QuoteTemplate? Template.
`QuoteTemplate.Instantiate(IReadOnlyDictionary<string, QuoteArgument>)` returns `QuoteInstantiation : IDisposable`.
`QuoteInstantiation` exposes bool Success, IReadOnlyList<QuoteDiagnostic> Diagnostics, SyntaxTree? Tree, and IReadOnlyDictionary<int, SourceOrigin> Origins.
`Language.CompileQuote(string category, string text, IReadOnlyDictionary<string, QuoteHole> holes)` returns QuoteCompilation.
`Language.ParseTemplate(string text, string category, IReadOnlyDictionary<string, QuoteHole> holes)` returns ParseResult.
`Language.ExpectedTemplate` gains the equivalent typed overload.

Origin integration: `QuoteInstantiation.MapOrigin(SourceOrigin)` maps only origins from the result tree/generated document. `MapRange(string generatedPath, TextSpan)` returns source locations for intersecting mapped nodes. `Remap(LoweringResult)` maps HIR and diagnostic origins, retaining generated locations where no input identity was supplied. These APIs do not bind or execute anything.

Grammar rewrite `RewriteHole` gains optional IsList=false and Stride=1 constructor parameters. `SyntaxModule` gains a virtual category-membership table with an empty default; generated modules populate it. A node is compatible only if its tree uses the same Language instance and its kind belongs to the requested category; generated aliases include transitive members. Handwritten modules without sufficient metadata receive an unsupported-category diagnostic instead of guessed acceptance.

Use NGR0139 for scalar/list shape mismatch and NGR0140 for unsupported splice shapes (currently unallocated after NGR0138). Use NH0010 for an empty required instantiated list (verify the registry again at implementation). C# quotation uses NQ0001 template/category failure, NQ0002 argument mismatch, NQ0003 invalid/disposed/recovered source, NQ0004 category/snapshot mismatch, NQ0005 required-list cardinality, and NQ0006 declarations. Existing unknown-field and template warnings keep their current codes.

---

### Task 1: Parse typed list placeholders at generated list sites

**Files:** QuoteTypes.cs; ParserState.Quotation.cs; Language.cs; ParserState.cs; SyntaxKinds.cs; SyntaxCodeWriter.cs; BuildArena.cs; TreeFreezer.cs; SyntaxTree.cs; TemplateParsingTests.cs; new QuoteLists.ngr and TemplateListParsingTests.cs; Nitrogen.Tests.csproj.

**Consumes:** Current scalar template parser and generated repetition/separated-list loops.
**Produces:** Typed ParseTemplate/ExpectedTemplate overloads, SyntaxKinds.ListHole, and list-site metadata accessible internally by frozen list node index.

- [x] Write the generated test grammar with `Expr`, numeric expressions, `Call`, and comma/semicolon/repeated/required lists. Add its AdditionalFiles entry to the test project.

```ngr
syntax module QuoteLists
{
  token Number = ['0'..'9']+;
  syntax Program = Expr ";";
  extensible syntax Expr
  {
    | Num = Value:Number
    | Add = Left:Expr "+" Right:Expr precedence 10 left
    | Mul = Left:Expr "*" Right:Expr precedence 20 left
    | Call = "f" "(" Args:(Expr; ",")* ")"
    | Required = "required" "(" Args:(Expr; ",")+ ")"
    | Space = "space" "[" Args:Expr* "]"
    | Semi = "semi" "(" Args:(Expr; ";")* ")"
  }
}
```

- [x] Add and run failing parser tests using the real generated module. The key assertion is two list placeholders for `f(..$Args, ..$Args)`, and rejection of `..$Args + 1` as Expr.

```csharp
var language = new LanguageBuilder().Add(QuoteListsModule.Instance).Build();
using var parsed = language.ParseTemplate("f(..$Args, ..$Args)", "QuoteLists.Expr",
    new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
Assert.True(parsed.Success);
Assert.Equal(2, Enumerable.Range(0, parsed.Tree.NodeCount)
    .Count(n => parsed.Tree.Kind(n) == SyntaxKinds.ListHole));
```

Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~TemplateListParsingTests`.
Expected RED: missing typed overload/ListHole; after implementation all parser cases pass.

- [x] Implement template-only TryListHole(category). Generated item checks use `s.InTemplate && s.TryListHole(category)` before the ordinary item parse. Never put this check into the ordinary nonterminal entry. Preserve marks/resets and recovery loops. Metadata records element category, minimum cardinality, and destination literal separator; freeze it alongside template nodes without retaining arena dictionaries. Ordinary parsing keeps its existing list path.
- [x] Cover unknown/wrong-shape markers, several markers, whitespace, ordinary parse rejection, required-list placeholder acceptance, alias/import categories, and marker text inside a quoted token/comment. Run the existing TemplateParsingTests and recovery tests with the new parser tests. Expected GREEN: all pass; normal parses contain no ListHole.

### Task 2: Validate grammar list fields and emit category metadata

**Files:** GrammarValidator.cs; GrammarDiagnostic.cs; DeclarativeWriter.cs; new QuotationWriter.cs; ModuleWriter.cs; SyntaxModule.cs; DeclarativeRule.cs; Grammar/RewriteGrammarTests.cs; new Grammar/ListSpliceGrammarTests.cs.

**Consumes:** Task 1 QuoteHole shape and template list-site representation.
**Produces:** List-aware RewriteHole metadata, category-membership tables, NGR0139/NGR0140 validation.

- [x] Write failing validation/generation theories with literal expected diagnostic codes. Include scalar field used after `..$`, list field used after `$`, optional/token/tuple/nonliteral-separator fields, unknown fields, and a valid imported separated-list field. Run the focused file and confirm RED.

```csharp
[Theory]
[InlineData("Value:Expr", "..$Value", "NGR0139")]
[InlineData("Value:Expr*", "$Value", "NGR0139")]
[InlineData("Value:Expr?", "..$Value", "NGR0140")]
public void Invalid_splice_shapes_are_diagnostics(string field, string template, string code)
{
    string text = "syntax module M { token N = ['0'..'9']+; syntax Expr = N; " +
        "syntax R = " + field + " rewrites <[ " + template + " ]>; }";
    var result = GrammarCompiler.Compile([new GrammarInput("M.ngr", text, "Tests.M")]);
    Assert.Contains(result.Diagnostics, d => d.Diagnostic.Code == code);
}
```

- [x] Resolve field shape through the real GrammarAnalysis, preserving implicit extension-module scope. Emit list stride 1 or 2, element nonterminal category, and IsList. Add generated category membership for syntax rules, aliases, and extensible alternatives; do not validate by spelling or by reparsing source text. Both grammar front ends retain model parity.
- [x] Remove reliance on a raw `$Name` regex as proof of actual template-hole occurrence. Defer occurrence/placement confirmation to parsed Hole/ListHole nodes when the composed language exists; keep structural field-shape validation consistent across generator/workspace paths. Apparent dollar text in tokens/comments must not require nonexistent fields.
- [x] Verify scalar generated grammar compatibility, imported/qualified aliases, ambiguous resolution diagnostics, and lists without rewrites. Run Grammar/RewriteGrammarTests, SelfHostingTests, and ListSpliceGrammarTests. Expected GREEN: valid generated modules compile and metadata recognizes the precise admissible kinds.

### Task 3: Structural list expansion in grammar rewrites

**Files:** new QuoteInstantiator.cs; SyntaxTree.cs; FileSemantics.Rewrites.cs; DeclarativeTypes.cs; new TemplateListInstantiationTests.cs; LanguageService/RewriteTests.cs.

**Consumes:** Task 1 list-site metadata and Task 2 RewriteHole shape/stride.
**Produces:** Shared list-expansion traversal with copy/proxy policy; instantiated template tree and proxy-node-to-caller-node map.

- [x] Add failing zero/one/many-item rewrite cases and compare complete HIR structure/types against explicit equivalent programs. Add theories for first/middle/last empty splices, repeated/multiple splices, source semicolons into destination commas, and required-list cardinality.

```csharp
[Theory]
[InlineData("", "f(1, 2)")]
[InlineData("3", "f(1, 3, 2)")]
[InlineData("3; 4", "f(1, 3, 4, 2)")]
public void List_rewrite_matches_the_explicit_program(string items, string explicitText)
{
    using var fixture = QuoteListFixture.Create();
    Assert.Equal(fixture.LowerShape(explicitText), fixture.LowerShape("forward(" + items + ")"));
}
```

QuoteListFixture is test-only: compile a grammar extending QuoteLists with `Forward`, a sequence lowerer and typed numeric operations; parse/bind/check/lower both inputs through actual runtime APIs. It must report all diagnostics, not hide unsupported lowering. Run TemplateListInstantiationTests; expected RED until instantiation exists.

- [x] Flatten list markers to actual item slots before rebuilding separators. Rebuild destination list children in stride-compatible order; enforce minimum count after flattening. Proxy mode inserts scalar Hole nodes and records each original caller node; preserve original child semantic forwarding and use-site expansion origins. Own each instantiated proxy tree with its FileSemantics and release it when that file/version is disposed.
- [x] Reuse original child lowerings per caller node, including repeated list occurrences. Preserve cycle detection and avoid running caller diagnostics twice. Do not permit unresolved ListHole nodes into list views or lowering.
- [x] Add assertions for generated list views, atomic nested expressions, item diagnostics at the original item, dynamic selected operations, scalar/list mixing, cycles, and NH0010. Run parser, grammar, and rewrite suites. Expected GREEN: parity and valid list structure for every tested cardinality.

### Task 4: Grammar editor support for list splices

**Files:** NitrogenLanguageService.Quasiquotes.cs; QuasiquoteNavigationTests.cs; new ListSpliceNavigationTests.cs.

**Consumes:** Typed template parsing, list field metadata, existing grammar quote editor routing.
**Produces:** Definition/declaration, hover, usages, and shape-filtered completion for list markers.

- [x] Add failing tests for `..$Args` -> `Args:(Expr; ",")*`, completion after `..$Ar`, and exclusion of scalar fields. After `$`, exclude lists. Check same label in unrelated alternatives, current-buffer shifts, imported grammars, and stale snapshots.

```csharp
var uses = service.Definition(uri, At(grammar, "..$Args", 4));
Assert.Equal(At(grammar, "Args:(Expr"), Assert.Single(uses).Range.Start);
Assert.Contains(service.Completion(uri, At(grammar, "..$Ar", 5)), i => i.Label == "Args");
Assert.DoesNotContain(service.Completion(uri, At(grammar, "..$Ar", 5)), i => i.Label == "Value");
```

- [x] Derive occurrences from parsed Hole/ListHole nodes; use list metadata for completion. Map marker text offsets correctly (`$` versus `..$`), retaining opaque strings/comments and existing keyword navigation. Gracefully handle invalid current templates and absent categories in a retained snapshot.
- [x] Run LoweringNavigationTests, QuasiquoteNavigationTests, and ListSpliceNavigationTests. Then build and run the complete suite for the Stage 1 gate. Expected GREEN: list rewrites usable through parsing, checking, lowering, and grammar authoring before Task 5.

### Task 5: C# quote compilation, category checking, and independent syntax results

**Files:** QuoteTemplate.cs; QuoteInstantiator.cs; Language.Quotation.cs; Language.cs; QuoteTypes.cs; new QuoteApiTests.cs; new QuoteOwnershipTests.cs.

**Consumes:** Tasks 1–3 parser, category membership, and shared structural expansion.
**Produces:** Public CompileQuote/Instantiate contracts specified above; owned ordinary syntax with no remaining placeholders.

- [x] Write a failing public-API test with a nested expression splice into multiplication and an empty list splice. Validate the returned tree via binding, semantics, and lowering rather than just printed text.

```csharp
using var compiled = language.CompileQuote("QuoteLists.Expr", "$Value * 2",
    new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("QuoteLists.Expr") });
Assert.True(compiled.Success);
using var source = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
{
    ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, source.Tree.Root)),
});
Assert.True(result.Success);
Assert.DoesNotContain(Enumerable.Range(0, result.Tree!.NodeCount),
    n => result.Tree.Kind(n) is SyntaxKinds.Hole or SyntaxKinds.ListHole);
```

- [x] Make Language partial and implement CompileQuote. Snapshot the caller's hole dictionary; parse once; reject declarations; retain owned parse storage until compilation disposal. Instantiate snapshots argument lists, validates categories/snapshot identity, and materializes fresh pooled arrays and a synthesized buffer. Copy subtrees structurally, keeping internal token gaps and valid generated spans; never reparse interpolated source expressions.
- [x] Add failed-compilation/argument tests for unknown/missing/extra holes, disposed trees, negative/out-of-range node indices, recovered/ambiguous nodes, wrong category, same-named modules in another snapshot, unsupported manual module metadata, and empty required destinations. Return no Tree on failure and release temporary allocations.
- [x] Add lifetime tests: dispose sources and compiled template after two instantiations, inspect/bind/lower both results, then dispose one and verify the other. Assert every node span fits Tree.Text and all copied node texts are readable. Run QuoteApiTests and QuoteOwnershipTests; expected GREEN with atomic operator structure and no borrowed arrays.

### Task 6: Explicit constructed-source origin integration

**Files:** QuoteOrigins.cs; QuoteTemplate.cs; HirTraversal.cs only if needed; new QuoteOriginTests.cs; docs/language-guide.md.

**Consumes:** Task 5 result tree and optional QuoteNode.Origin; existing SourceOrigin and LoweringResult.
**Produces:** MapOrigin, MapRange, Remap(LoweringResult) and documented project/binding/check/lower integration.

- [x] Add failing tests for a spliced unresolved reference, a semantic error in a spliced item, template-authored error locations, and HIR origins after lowering a generated document.

```csharp
var lowered = HirLowering.Lower(new ProjectSemantics(project)[generatedPath], language.SemanticCatalog);
var mapped = result.Remap(lowered);
Assert.Contains(mapped.Roots.SelectMany(HirOrigins), o => o.Path == originalPath);
Assert.DoesNotContain(mapped.Diagnostics, d => d.Origin.Path == originalPath && d.Origin.Span.End > originalText.Length);
```

`HirOrigins` is a test-only traversal of operation arguments and each node's Origins. Construct originalPath/originalText and the expected source span independently in the fixture.

- [x] Populate origin entries for copied and template nodes, with explicit template identity optional at compile time through an additional options overload. Map only matching generated-tree origins; retain generated locations otherwise. A nested source location must resolve to the original subtree, never a fabricated original-file offset. MapRange handles overlapping node spans by choosing smallest mapped covering nodes, then deduplicating source locations.
- [x] Test repeated insertions, origins omitted by caller, cross-document inputs, multi-line spans, and normal generated-document binding without remapping. Document that inserted names bind freshly in the destination project and no hidden symbols are captured. Run QuoteOriginTests and the public API suites; expected GREEN.

### Task 7: C# raw-string quote editor integration

**Files:** CSharpQuotations.cs; NitrogenLanguageService.CSharpQuotations.cs; EmbeddedStrings.cs; existing definition/hover/completion/reference entry points; new CSharpQuoteNavigationTests.cs.

**Consumes:** Constant CompileQuote calls, public QuoteHole declarations, typed template parsing, existing embedded string offset mapping.
**Produces:** C# quote recognition and source-mapped grammar/placeholder IntelliSense without executing helper code.

- [x] Add failing editor tests opening a C# helper containing the exact approved CompileQuote call shape. Resolve a syntax literal to its grammar rule, and a scalar/list name to its dictionary key. Request completion from multiline raw strings with indentation, regular string escapes, and verbatim strings. Verify replacement spans are C# source spans.

```csharp
var location = Assert.Single(service.Definition(helperUri, At(helperText, "..$Args", 4)));
Assert.Equal(helperUri, location.Uri);
Assert.Equal(At(helperText, "[\"Args\"]"), location.Range.Start);
```

- [x] Recognize calls with Roslyn argument binding, including named/reordered arguments and constant category/template expressions. Restrict assistance to supported statically recoverable hole dictionaries and one unambiguous workspace language category. Dynamic templates keep runtime behavior and get no fabricated assistance. Reuse the C# string decoder/offset map; do not create a second DSL parser. Host C# offsets and embedded quote offsets remain separate.
- [x] Add regressions for fake calls in comments/strings, unrelated CompileQuote methods, missing/edited dictionaries, multiple language candidates, list/scalar shape filters, disposed/stale workspace snapshots, and source buffers differing from disk. Run CSharpQuoteNavigationTests and the full embedded-language/editor suites. Expected GREEN; existing tagged string behavior is unchanged.

### Task 8: Whole-feature verification and documentation

**Files:** docs/language-guide.md; spec status; plan checklist; test fixtures added in earlier tasks.

**Consumes:** Verified Stage 1 and C# API/editor slices.
**Produces:** Reproducible examples, documented limitations and ownership, full verification evidence, independent final code review.

- [x] Add runnable grammar and C# examples corresponding to the tests; state supported list shapes, generated-text limits, ownership lifetimes, origin mapping, and the fresh destination-binding rule.
- [x] Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`, capturing full logs outside tracked source. Expected: zero build warnings/errors and all tests pass. Also run `git diff --check`; preserve and identify any unrelated baseline failures rather than weakening tests.
- [x] Request one independent fresh-context whole-change review, with the approved spec, plan, exact changed files, verification logs, and prior navigation changes clearly distinguished. Fix important findings with failing regression tests followed by the full suite.
- [x] Mark the spec implemented only after all gates pass. Report runtime verification separately from live Rider behavior. An installed-server update, if requested, follows successful verification and uses a backup; do not imply that a passing unit suite proves the editor process loaded new code.

## Execution result

Completed inline on 2026-10-11. Regression coverage combines the new quotation suites and existing parser, rewrite, semantic, and editor suites rather than duplicating every proposed test as a separate case. Final build has zero warnings/errors; all 1,307 tests pass; `git diff --check` passes. Independent review findings were reproduced with failing tests and fixed, including constant source mapping, opaque-token lint, predicate isolation, comment completion, and leading-trivia offsets. Logs: `/private/tmp/nq-final-build.log`, `/private/tmp/nq-final-tests.log`, `/private/tmp/nq-final-edge-red.log`, `/private/tmp/nq-final-edge-green.log`. No commits, pushes, or installed-server updates were made for this extension; live Rider remains unverified.

## Execution handoff

Recommend native execution in this chat: these tasks share parser metadata, list layout, and ownership contracts and benefit from one implementer's continuous context. Use one independent whole-feature reviewer after implementation. User review of this plan and selection/confirmation of execution method precede product-code edits under the writing-plans skill.
