# Typed Quasiquote Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Execution is native in this session, as previously selected. Steps use checkbox (`- [x]`) syntax for tracking. Use one independent whole-branch review after implementation.

**Goal:** Declare typed reusable `.ngr` quote helpers, invoke them from rewrites, navigate their inputs and uses, and extract a complete inline quote into a reviewed shared helper.

**Architecture:** Helpers are separate grammar declarations, resolved through the existing composed grammar analysis. A call emits an ordinary `RewriteSpec` whose hole names are helper parameters and whose child indices refer to caller fields. Quote validation and instantiation use the generated Nitrogen parser and quotation core; no text substitution or second helper interpreter is introduced.

**Tech Stack:** Existing C#/.NET 10 solution; netstandard2.0 grammar/generator components; Roslyn workspace compilation; Nitrogen runtime/semantic services; TypeScript VS Code extension; xUnit and Node tests. No new external dependencies.

**Spec:** [Approved specification](../specs/2026-10-13-quasiquote-helpers-design.md), approved in conversation on 2026-10-14.

## Global Constraints

- Work only in Nitrogen. The host task's default CsNitra directory is unrelated: set the working directory explicitly on every command.
- Preserve `/Users/dmitrikamenetski/work/Nitrogen/docs/editor-support.md`, which has an unrelated user edit. Make implementation changes in the existing isolated `/tmp/nitrogen-language-composition` worktree.
- The review UX prerequisite is commit `d4e0444` on `codex/extraction-review-ux`; integrate it before Task 5 and reuse its controls, progress and completion feedback.
- Helpers do not create parser productions or syntax kinds. Preserve existing kind numbering and the old rule named `function`.
- Parameters are named scalar syntax categories or zero-or-more lists; returns are one syntax category. No token/alternative categories, optionals, tuple parameters, overloads, defaults, nested calls or arbitrary argument expressions.
- Calls accept positional labeled fields only. Compare resolved category identities and scalar/list shapes, including every declared argument even if unused in the quote.
- Preserve original quote text verbatim, caller structure/binding, snapshot checks, existing ownership rules and rewrite-cycle detection.
- Validate unused helpers; forbid template-authored declarations. Generated and workspace paths use the same grammar analysis and parser emitter for the same registered inputs.
- Use NGR0141–NGR0147 as reserved in the spec, after rechecking the registry before edits. Retain existing quotation error codes for equivalent failures.
- No standalone C# named-helper invocation API or structural pattern matching in this slice.
- Refactoring is one complete reviewed proposal, no writes before Apply, no automatic Save All, one extraction undo. Support current, existing, and new destination modules explicitly.
- Product implementation begins only after this written plan is reviewed. PR/version/release are separate requests.

## Review Focus

1. **Unused malformed helpers:** generated C# can compile while a quoted body is invalid. Task 2 validates every helper against a composed syntax language, not just invoked helpers.
2. **Source generation versus runtime validation:** the netstandard generator cannot call the net10 runtime before its parser exists. Task 2 separates static diagnostics from composed validation and tests the generated language's construction path as well as workspace validation.
3. **Opaque dollar text and repeated holes:** extraction must infer actual parsed holes once, ignoring tokens/comments. Task 5 tests this through the composed parser, not a regular expression.
4. **Existing-file destinations and stale documents:** no CreateFile operation may target an existing file, and opening/closing identical documents must remain safe. Task 5 tests both LSP edit forms and real changed-text refusal.
5. **Caller binding and origins:** an imported helper must not capture the helper module's scope or lose the program operand's identity. Tasks 3 and 4 test caller-bound names, structural precedence, error origins and navigation.

## Validation boundary discovered during planning

`GrammarWorkspace.ValidateCompilation()` currently emits C# without loading it. Its full `Compile()` path loads modules and adds inline template diagnostics as warnings. A helper extraction cannot rely solely on either behavior: the first misses composed quote errors; the second may execute configured C# static initializers.

Add a **syntax-only emission mode** using the same parser/token/binding/quotation emitters. It omits semantic properties, user C# hooks, declarative lowering and views. Load only those generated syntax modules into a collectible validation context, construct the language with standard workspace trivia, and validate helper bodies with `CompileQuote`. Configured C# sources are still compiled separately, without execution, by existing `ValidateCompilation`. Never load the caller's helper assembly for extraction validation.

For normal generated-language construction, validate declared helpers against the actual composed `Language` before `LanguageBuilder.TryBuild` returns success. This also covers handwritten modules/custom trivia in that runtime composition. The source generator reports static model errors; full body parsing happens after parser construction. Do not describe a successful C# compilation alone as successful quote validation.

## Task 1: Grammar syntax, model, and self-hosting parity

**Files:**
- Modify: `Nitrogen.Grammar/GrammarModel.cs`, `GrammarParser.cs`, `GrammarDumper.cs`.
- Modify: `Nitrogen.Ngr/Nitrogen.ngr`, `GrammarSemantics.cs`, `NgrProjector.cs`.
- Create: `Nitrogen.Tests/Grammar/QuoteHelperGrammarTests.cs`.
- Modify: `Nitrogen.Tests/Grammar/SelfHostingTests.cs`.

**Interfaces:** Append optional `Helpers` to `ModuleDecl` and optional `HelperCall` to `BindingClause`, preserving existing positional construction. Add these grammar-model records:

```csharp
public sealed record QuoteText(string Text, GrammarSpan Span);
public sealed record QuoteParameter(NameDecl Name, NameDecl Category, bool IsList, GrammarSpan Span);
public sealed record QuoteHelperDecl(NameDecl Name, EquatableArray<QuoteParameter> Parameters,
    NameDecl ReturnCategory, QuoteText Body, GrammarSpan Span);
public sealed record QuoteHelperCall(NameDecl Helper, EquatableArray<NameDecl> Arguments, GrammarSpan Span);
```

`ModuleDecl.Helpers` has type `EquatableArray<QuoteHelperDecl>` and defaults to `default`.
`BindingClause.HelperCall` has type `QuoteHelperCall?` and defaults to `null`; inline rewrites retain their current `Target` representation.

- [x] **1. Write parser regression tests before adding model/parser code.** Start with the following test, then parameterize zero/list/multiple parameters, qualified categories/calls, multiline quotes and unchanged legacy syntax:

```csharp
[Fact]
public void Parses_a_helper_without_turning_it_into_a_rule()
{
    const string text = "syntax module M { syntax function F(X: E) : E = <[ $X ]>; syntax E = \"x\"; }";
    var result = GrammarParser.Parse(text);
    Assert.True(result.Success);
    var module = Assert.Single(result.File!.Modules);
    var helper = Assert.Single(module.Helpers);
    Assert.Equal("F", helper.Name.Name);
    Assert.Equal(" $X ", helper.Body.Text);
    Assert.Equal("E", Assert.Single(module.Rules).Name);
    Assert.Equal("E", helper.ReturnCategory.Name);
}
```

Also assert `GrammarParser.Parse("syntax module M { syntax function = \"x\"; }")` still produces one `SyntaxRule` named `function`. Reject missing return types, default parameters, optional/list returns, nested calls and non-field arguments with an error span at the unsupported token.

- [x] **2. Run RED:** `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~QuoteHelperGrammarTests`. Record the expected new-model/missing-syntax failure.
- [x] **3. Add the records and parser branches.** Detect a helper declaration using `syntax function Identifier (` lookahead; otherwise retain normal `syntax Identifier =` parsing. Capture quotes through the existing template capture path so delimiters/trivia handling agree. In `rewrites`, choose inline quote or `qualifiedName(field, ...)`; retain precise name/body/argument spans. Keep helpers out of `Rules` and extension-point collections.

Add the self-hosted grammar productions, lowering operation signatures, and AST handlers for the same records. Extend the existing module-member partition to collect helpers separately. Do not map helpers through `TokenRule` or `SyntaxRule`. Add grammar dumper support for declarations and calls with unchanged quoted bodies.

- [x] **4. Run GREEN:** focused parser tests, then `--filter FullyQualifiedName~SelfHostingTests`. Include a mixed file containing both a helper and a rule named `function`, and compare bootstrap/self-hosted models including spans as existing parity tests do.
- [x] **5. Commit:** `git add Nitrogen.Grammar Nitrogen.Ngr Nitrogen.Tests/Grammar && git commit -m "Parse typed quote helpers and preserve self-hosting parity"`. Stage only the named files actually changed.

## Task 2: Resolution, ordinary rewrite emission, and all-helper validation

**Files:**
- Create: `Nitrogen.Grammar/QuoteHelperAnalysis.cs`, `QuoteHelperWriter.cs`.
- Modify: `GrammarAnalysis.cs`, `GrammarValidator.cs`, `GrammarDiagnostic.cs`, `DeclarativeWriter.cs`, `ModuleWriter.cs`, `GrammarCompiler.cs`, `QuotationWriter.cs`.
- Create: `Nitrogen.Runtime/Quotation/QuoteHelper.cs`, `Nitrogen.Workspace/QuoteHelperValidation.cs`.
- Modify: `Nitrogen.Runtime/SyntaxModule.cs`, `Language.Quotation.cs`, `LanguageBuilder.cs`, `Nitrogen.Runtime/Semantic/SemanticCatalog.cs`, `Nitrogen.Workspace/GrammarWorkspace.cs`.
- Create: `Nitrogen.Tests/Grammar/QuoteHelperValidationTests.cs` (class `GrammarQuoteHelperValidationTests`), `Nitrogen.Tests/Workspace/QuoteHelperValidationTests.cs` (class `WorkspaceQuoteHelperValidationTests`).

**Interfaces:**

```csharp
// Grammar assembly: identities and validated mappings, never executable code.
internal sealed record QuoteHelperSymbol(ModuleDecl Module, QuoteHelperDecl Declaration);
internal sealed record QuoteHelperInput(string Parameter, string Field, int Child,
    string Category, bool IsList, int Stride);
internal sealed record ResolvedQuoteHelper(QuoteHelperSymbol Symbol, string Category,
    IReadOnlyList<QuoteHelperInput> Inputs);

// Runtime: additive metadata with source coordinates, not a caller's snapshot identity.
public sealed record QuoteHelperSource(string Path, int Start, int Length);
public sealed record QuoteHelper(string Name, string Category, string Text,
    IReadOnlyDictionary<string, QuoteHole> Parameters, QuoteHelperSource Source);
public sealed record QuoteHelperError(QuoteHelper Helper, QuoteDiagnostic Diagnostic);
// Semantic namespace: a source location without a forged node/snapshot identity.
public sealed record CompositionDiagnosticLocation(string Path, TextSpan Span);
// CompositionDiagnostic adds an optional init-only Location property of this type;
// preserve its existing three-argument constructor and deconstruction.
// SyntaxModule adds: public virtual IReadOnlyList<QuoteHelper> QuoteHelpers => [];
```

`QuoteHelperAnalysis` is owned by `GrammarAnalysis`. Its `Resolve(string name, ModuleDecl module, GrammarReport? report)` returns `QuoteHelperSymbol?`; its `Bind(QuoteHelperCall call, ModuleDecl module, Expr callerBody, string returnCategory, string? implicitModule, GrammarReport? report)` returns `ResolvedQuoteHelper?`. Use the existing resolver's local/import/qualified policy; never select an arbitrary ambiguous candidate.

Add `GrammarCompiler.CompileQuotationSyntax(IEnumerable<GrammarInput> inputs) : GrammarCompileResult` using the same internal compile pipeline and parser emitters, with a mode that omits user semantic code/lowering/views. `QuoteHelperValidation.Validate(IEnumerable<GrammarInput> inputs) : IReadOnlyList<WorkspaceDiagnostic>` compiles those sources against Nitrogen runtime references only, loads them in a collectible context, validates all helper metadata and releases quote compilations/context before returning.

`Language.ValidateQuoteHelpers() : IReadOnlyList<QuoteHelperError>` validates all module helpers with `CompileQuote` and disposes each compilation. `LanguageBuilder.TryBuild` converts failures to composition diagnostics without returning a usable language; messages include helper identity, original quotation codes are preserved, and `CompositionDiagnostic.Location` carries the real helper/body span. Map relative quote spans through `QuoteHelper.Source`; use the full body span when the core has no narrower location. Workspace validation maps those locations directly, without parsing names out of message text. Existing modules with no helpers retain behavior.

- [x] **1. Write static-resolution RED tests:** compile two `GrammarInput` values with `GrammarCompiler.Compile`; assert success for imported and qualified helper calls and diagnostics NGR0141–NGR0147 for collisions, duplicate parameters, unknown/ambiguous calls, argument count, unsupported shapes and category/return mismatch. Pin the diagnostic span to the specific name/argument. Include unused bad signatures.

```csharp
[Fact]
public void Helper_calls_are_type_checked_before_emission()
{
    var result = GrammarCompiler.Compile(new[] {
        new GrammarInput("m.ngr", "syntax module M { syntax E = \"x\"; syntax function F(X:E):E=<[ $X ]>; syntax R=V:E rewrites F(); }", "Demo")
    });
    Assert.Contains(result.Diagnostics, d => d.Diagnostic.Code == "NGR0145");
    Assert.Empty(result.Sources);
}
```

- [x] **2. Run RED:** `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~QuoteHelperValidationTests`.
- [x] **3. Implement lookup and mapped rewrite emission.** Emit `RewriteHole` names from the helper's parameter names, with child indices/strides from call arguments; emit the original helper body and declared return category. For two parameters bound to the same caller field, emit both names with the same child index. Do not rewrite dollar text or copy operand source text. Reuse `QuotationWriter.Shape` and the existing extension-point rewrite-category logic through one shared method.

Emit `QuoteHelpers` metadata independently of kind enumeration, including unused declarations. Implement static body-hole checks against parameter declarations and validate opaque-token candidates through the composed parser at the full-validation stage.

Implement syntax-only emission as a mode of `ModuleWriter.Write`, retaining parser/token registration, quote categories and data-only binding tables needed to detect declarations. Skip generated C# semantics, views and declarative semantic tables. Keep the normal emitter's output unchanged for old grammars.

Wire `QuoteHelperValidation.Validate` into workspace compile/validation after normal C# emission succeeds and before loading configured helper assemblies. Use it for extraction preflight. In normal `LanguageBuilder`, validate helper bodies after constructing syntax but before returning success. Keep existing inline-quote warning behavior unchanged; helper body failures are errors. Preserve syntax-only language construction without semantic catalog requirements.

- [x] **4. Add and run full-validation tests:** unused malformed quote, unused unknown hole, wrong list splice, authored declaration, keyword/operator supplied by another composed module, and a configured C# static initializer that throws if executed. `ValidateCompilation` must diagnose helper failures without running that initializer. Test the normal generated factory's `TryBuild` path too. Confirm no assembly from failed validation remains pinned.
- [x] **5. Commit:** stage the exact grammar/runtime/workspace/test files above; commit `"Resolve quote helpers through structural rewrites and validate every body"`.

## Task 3: End-to-end binding, lists, ownership, and DateCalc parity

**Files:**
- Create: `Nitrogen.Tests/DateCalc/DateCalcQuoteHelperTests.cs`, `Nitrogen.Tests/QuoteHelperRuntimeTests.cs`, `Nitrogen.Tests/Grammars/QuoteHelpers.ngr`.
- Modify: `Nitrogen.Tests/Nitrogen.Tests.csproj` to register the new grammar fixture.
- Modify only if tests expose a gap: `Nitrogen.Runtime/Semantics/FileSemantics.Rewrites.cs`, `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`.

**Interfaces:** Use Task 2's emitted ordinary rewrite metadata and existing `LanguageBuilder`, `QuoteTemplate.Instantiate`, `ProjectSemantics`, and `HirLowering`. There is no new helper execution API.

- [x] **1. Write a temporary DateCalc fixture:** copy the six source/config files as `DateCalcExtractionTests` does; insert `syntax function FortnightDuration(Value: Expr) : Expr = <[ $Value * 2 weeks ]>;` before the existing `extensible syntax Expr`; replace only `rewrites <[ $Count * 2 weeks ]>` with `rewrites FortnightDuration(Count)`. Reuse the actual `GrammarWorkspace` compile/evaluator reflection pattern from `DateCalcExtractionTests`, keeping the fixture test-only and temporary.

```csharp
// Assertion against actual DateCalcEvaluator output, not generated source text:
Assert.Equal(new[] { "3", "42 days", "42 days", "2026-11-02 Mon" }, values);
```

Compare lowered HIR for `(1 + 2) fortnights;` with `(1 + 2) * 2 weeks;`, using the existing `RewriteTests.Shape` logic in the new test class. Add another field also named `Value` in a surrounding grammar/module to prove helper parameter lookup does not capture it.

- [x] **2. Run RED:** `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalcQuoteHelperTests|FullyQualifiedName~QuoteHelperRuntimeTests"` before any runtime adjustment.
- [x] **3. Add the generated list fixture:** expression literals, addition/multiplication, calls with a literal comma separator, and helpers with scalar/list parameters. Cover empty lists, nonempty lists, repeated list parameters, two parameters supplied the same field, unknown argument categories and a required-list destination. Reuse `QuoteInstantiation` ownership and language snapshot tests with helper body metadata from the generated fixture.

If a failure exposes a difference between helper expansion and inline expansion, fix the mapped child/proxy metadata rather than parsing operand text. Preserve `_chain`/`NH0008` rewrite-cycle behavior. Do not add special evaluation logic for DateCalc.

- [x] **4. Run GREEN and existing quote suites:** the two new test classes plus `QuoteApiTests`, `QuoteOwnershipTests`, `TemplateListParsingTests`, `TemplateListInstantiationTests`, and `RewriteTests`. Include disposal of source trees/compilations and rejection of nodes from a new language snapshot.
- [x] **5. Commit:** `"Prove quote helper binding, list and DateCalc parity"` with fixture/test files and only any justified runtime changes.

## Task 4: Grammar IntelliSense, rename, and source origins

**Files:**
- Create: `Nitrogen.LanguageService/QuoteHelperSourceIndex.cs`, `Nitrogen.LanguageService/NitrogenLanguageService.QuoteHelpers.cs`, `Nitrogen.Tests/LanguageService/QuoteHelperNavigationTests.cs`.
- Modify: `NitrogenLanguageService.cs` definition/reference/completion/rename dispatch, `NitrogenLanguageService.Quasiquotes.cs`, and `Nitrogen.Ngr/Nitrogen.ngr` presentation/binding support.
- Modify as needed for additive origin metadata: `DeclarativeRule.cs`, `DeclarativeWriter.cs`, existing service/LSP diagnostic conversions.

**Interfaces:** `QuoteHelperSourceIndex` indexes the configured grammar snapshots and resolves occurrences by module/helper identity, parameter owner and field owner. It exposes `Definition(string path, DocumentPosition position) : IReadOnlyList<DocumentLocation>`, `References(string path, DocumentPosition position, bool includeDeclaration) : IReadOnlyList<DocumentLocation>`, `Completion(string path, DocumentPosition position) : IReadOnlyList<CompletionItem>`, and `Rename(string path, DocumentPosition position, string newName) : IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>>`. Use `NameDecl`/`QuoteText` spans from Task 1 and Task 2's resolved identities; do not bind names by spelling alone.

Add optional helper-definition source metadata to `RewriteSpec` without breaking its three-argument constructor. It carries `QuoteHelperSource`, not a forged runtime node/snapshot. Preserve program origins for spliced operands. Related LSP locations are emitted from actual declaration/body spans; clients without related-location support still receive a clear primary diagnostic at the use.

- [x] **1. Write RED service tests with two temporary modules:** a qualified helper call and an unqualified imported call; helper parameters used twice in the body; caller fields with different names; categories from a third module. Assert exact destination URI/range for helper, parameter, field and category navigation. Assert helper completion includes ordered input categories and return category.

Use the existing `RewriteTests` service setup and `LineMap.PositionOf(text.IndexOf(...))` to locate tokens. Tests call actual `service.Definition`, `References`, `Completion`, and `Rename` APIs, not the index directly.

- [x] **2. Run RED:** `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~QuoteHelperNavigationTests`.
- [x] **3. Implement occurrence indexing and dispatch:** compose it with existing grammar rule/property/C# navigation rather than replacing those paths. Resolve `$Name`/`..$Name` occurrences from parsed quote holes; exclude opaque dollar tokens/comments. Rename the parameter declaration and only its helper-body splices; calls are positional and do not rename caller fields. Helper rename updates every resolved grammar call across files or refuses unsupported known consumers. Ambiguous uses must not navigate/rename to one arbitrary declaration.

Map parse-template failures to the helper quote body and runtime expansion failures primarily to the program use. Preserve existing copied-subtree origins, add the helper definition as related information, and show the expansion/helper name in the message. Keep keyword/operator navigation within helper quotes working through existing quotation contexts.

- [x] **4. Run GREEN:** new navigation tests plus `GrammarHelperNavigationTests`, `GrammarPropertyNavigationTests`, `CSharpQuoteNavigationTests`, `RewriteTests`, and LSP transport tests. Edit a helper's body with `service.Change` and assert sample diagnostics/value hints refresh without constructing a new service.
- [x] **5. Commit:** `"Add IntelliSense and precise origins for grammar quote helpers"`.

## Task 5: Extract Quote Helper with complete proposal review

**Files:**
- Create: `Nitrogen.Grammar/QuoteHelperExtraction.cs`, `Nitrogen.LanguageService/NitrogenLanguageService.QuoteHelperExtraction.cs`, `Nitrogen.Tests/LanguageService/QuoteHelperExtractionTests.cs`, `QuoteHelperExtractionLspTests.cs`.
- Modify: `ExtractionTypes.cs`, existing extraction/configuration helpers, `Lsp/LspMessages.cs`, `Lsp/LspServer.cs`.
- Modify: `editors/vscode/src/extractGrammar.ts`, `extractGrammarFlow.ts`, `extractGrammar.test.ts`, `editors/vscode/package.json`.

**Interfaces:**

```csharp
public sealed record QuoteHelperExtractionRequest(string Id, string DestinationUri,
    string Module, string HelperName, bool ExistingDestination);
public sealed record QuoteHelperExtractionPreparation(string? Id, string? SuggestedHelperName,
    string? SourceModule, string? SourceUri, string? UnavailableReason);
// New service methods:
// PrepareQuoteHelperExtraction(string uri, DocumentRange range)
//     -> QuoteHelperExtractionPreparation
// ResolveQuoteHelperExtraction(QuoteHelperExtractionRequest request)
//     -> ExtractionProposal
```

Append `bool CreateDestination = true` to existing `ExtractionProposal`; old Extract Block keeps the default. For an existing destination, return its insertion in `Changes`, no CreateFile, and no standalone destination replacement. Extract LSP conversion into one shared `ExtractionResult(ExtractionProposal)` helper consumed by both old/new resolution handlers.

Protocol methods are `nitrogen/prepareQuoteHelperExtraction` and `nitrogen/resolveQuoteHelperExtraction`. Add VS Code command `nitrogen.extractQuoteHelper`, exposed as `refactor.extract` with title **Extract Quote Helper…**. The new prompt path gathers helper name, target kind (`Current module`, `Existing module`, `New module`), and file/module inputs where required. Current module is default. An existing file is selected explicitly and parsed to choose its module; a new-file destination retains collision refusal.

Extend the TypeScript host flow with a separate helper-input collector that produces the exact request above. Keep review/apply/open/completion in the shared host and reuse `ExtractionReview`; do not create a second Apply/Cancel implementation.

- [x] **1. Integrate the prerequisite before editing the client:** check the worktree is clean, then `git merge --no-edit codex/extraction-review-ux`. Inspect any conflict against the approved feature files and preserve the existing UX tests. Run its 38 client tests and the extraction service tests before proceeding.
- [x] **2. Write RED selection/transformation tests:** a caret in a complete inline quote; an exact whole-quote selection; a partial selection; no holes; scalar/list holes; repeated holes; quoted/comment dollar text; destination in the same module, an existing shared module, and a new module. Assert the original body text is preserved and inferred parameters follow actual first-use order, not field declaration order.

For service tests use a temporary DateCalc copy with the four-token/quote example. Prepare at the full `<[ $Count * 2 weeks ]>` span, resolve to `Shared.ngr` / `Shared` / `FortnightDuration`, assert no refusal, assert no files written, and assert only helper/import/config/call edits are proposed. Apply the proposed edits in the test fixture and evaluate before/after with the real DateCalc evaluator.

- [x] **3. Run RED:** `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~QuoteHelperExtraction` and `npm test` before implementing the new request/collector paths.
- [x] **4. Implement inference and proposal validation:** use the composed parser's real hole nodes and `TemplateLists` metadata; infer types from caller labels using Task 2's shape logic. Existing-file insertions use actual module closing spans and do not replace unrelated text. New modules qualify category names/import what is needed; add a source-side using and unqualified call only when resolution is unambiguous. Update configured grammars and language declarations using existing configuration-edit logic.

Snapshot the complete inputs during prepare. On resolution, reject named actual text/file-set changes, but allow unchanged documents to open/close; emit current versions. Check the configured language's original/proposed C# compilation without executing user helpers, and run Task 2's syntax-only quote validation on both compositions. Retain symlink/root/destination and unrelated-language glob protection. Existing old extraction must explicitly refuse helper-bearing selections or moves that would break helper references.

LSP emits `create` only when `CreateDestination` is true. All edits carry existing complete-proposal annotations and expected document contents. The client revalidates after review, compares the full edit independently of document versions, then applies the fresh proposal after local content/version checks.

- [x] **5. Add GREEN safety/protocol/client tests:** existing destination has no CreateFile; new destination refuses collisions; invalid existing-module choice; stale source, destination/config and helper changes; unchanged reopen; symlink and outside-root paths; unrelated grammar globs; C# compilation failures; unknown helper name collision; cancellation at every prompt; pending review blocks another extraction; Retry prepares a new helper proposal; successful Apply opens destination and reports each changed file once.

Use the existing `LspServerTests.Session` transport fixture to assert exact `documentChanges` and expected versions. Extend the client fixture to exercise the helper request payload and reuse existing persistent-control lifecycle tests. Verify unsupported clients get a disabled action rather than incomplete edits.

- [x] **6. Commit:** `"Extract inline rewrites into reviewed shared quote helpers"` with grammar/service/LSP/client tests and product changes.

## Task 6: Native acceptance, documentation, and completion evidence

**Files:**
- Modify: `docs/language-guide.md`, `editors/vscode/README.md`, and the worktree's `docs/editor-support.md` only (do not overwrite the user's dirty copy).
- Modify: `examples/DateCalc/DateCalc.ngr` to demonstrate the helper only after before/after parity is proved; keep `quasiquotation.datecalc` results unchanged and update its explanatory comment.
- Update this plan's checkboxes with actual evidence paths/results.

- [x] **1. Add documentation/example RED checks:** compile the documented helper grammar with `GrammarWorkspace`; evaluate the actual DateCalc sample; assert the documentation's declaration/call syntax and values work. Add an example with a list parameter. Clearly state v1 exclusions and existing ownership/binding behavior.
- [x] **2. Run RED then update examples/docs:** keep one inline rewrite fixture elsewhere so the old syntax remains tested. Add CLI/build consumption coverage of the documented shared helper inputs; do not add undeclared input discovery.
- [x] **3. Run complete gates once final source changes are done:**

```sh
dotnet build Nitrogen.slnx -warnaserror
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build
# Run the next two from editors/vscode:
npm test
npm run compile
# Run these from the Nitrogen worktree root:
python3 eng/check-links.py
python3 eng/test-build-identity.py
python3 eng/test-composition-inputs.py
git diff --check
```

- [ ] **4. Verify native VS Code in a scratch copy, not the user's DateCalc files:** launch the built development extension with the worktree's rebuilt server. Record baseline file bytes and sample values. Invoke Extract Quote Helper on the DateCalc quote, choose a new shared module, inspect every file in review, Cancel once, then repeat and Apply. Verify persistent controls, success feedback, unqualified helper navigation, parameter/keyword navigation, values and zero new diagnostics. Edit helper text and observe refresh; undo that edit; undo extraction once; verify original files/config byte-for-byte and new-file removal. Repeat using an existing-file destination to confirm no overwrite/CreateFile failure. If the Mac is locked, report native acceptance as blocked and do not claim it passed.
- [x] **5. Request one independent whole-branch review:** give the reviewer spec/plan, exact base/head, diff, fresh test evidence and native acceptance results. Address actionable findings, rerun only affected/new checks, then commit final docs/evidence as `"Document and verify typed quote helper extraction"`.

## Self-review coverage

- Spec acceptance 1: Task 1 bootstrap/self-hosted parity and preserved kind production.
- Acceptance 2: Task 2 static and composed checks, including unused helpers.
- Acceptance 3–4: Task 3 DateCalc/HIR parity, lists, ownership, snapshots and cycles.
- Acceptance 5: Tasks 2, 4 and 6 generated/runtime/workspace agreement and live refresh.
- Acceptance 6: Task 4 service/LSP navigation, rename, completion and origins.
- Acceptance 7: Task 5 all destination forms, proposal safety, protocol and shared controls.
- Acceptance 8: Task 6 scratch native apply/cancel/refresh/undo and byte comparison.
- Acceptance 9: Task 6 complete gates and independent final review.
- All five Review Focus cases are assigned to explicit test steps. No product code has changed in this planning stage.

## Handoff and completion

Written-plan review is required before executing Task 1. Preserve the previously chosen **native** execution method; do not ask the user to select it again. Implement each approved stage with RED/GREEN evidence and focused commits. Do not stop after parser support and call the feature complete: editor/refactoring/native acceptance are part of the approved slice.

At completion report implemented behavior, commit/branch/worktree, .NET/client/generator verification, actual native acceptance, and any precise remaining limitation. Keep the worktree. Do not push, create a PR, merge or release unless requested.

## Execution evidence (2026-10-14)

- Tasks 1–5 committed through `c0ddeba`; documentation/example stage `8dca091`.
- Full solution build: zero warnings/errors (`/tmp/nitrogen-helper-full-build.log`).
- Full .NET gate: 1,642 passed (`/tmp/nitrogen-helper-full-tests-final.log`).
- Client: 40 passed plus TypeScript/bundle compilation (`/tmp/nitrogen-helper-final-client.log`).
- Markdown links, build identity, and composition input registration passed.
- Extraction service/protocol gate: 81 passed. DateCalc application and live buffer changes additionally verified
  through the actual service (`/tmp/nitrogen-helper-live-service.log`).
- Native VS Code verified new-file review/cancel/apply, current-module apply, helper/parameter/keyword navigation,
  zero grammar diagnostics, and single extraction undo with original byte hashes/new-file removal.
  Initial sample values came from the installed DateCalc client; they are excluded as new-server refresh evidence.
  Remaining native live-refresh/existing-module acceptance is blocked by unavailable native app controls.
- Independent whole-branch review found six Important defects and no Critical/Minor findings. The single fix pass
  adds RED/GREEN coverage for helper rename/extraction capture, actual parsed parameter holes (including overlapping
  opaque tokens), every connected composition's consumers, signed-file navigation, and complete category references.
- Post-review full gate: 1,649 tests passed; solution build has zero warnings/errors
  (`/tmp/nitrogen-helper-review-suite.log`, `/tmp/nitrogen-helper-review-build.log`).
- All automated stages and review fixes are committed. Native step 4 remains pending: the user approved trust for the scratch folder and trust was applied,
  but native app controls became unavailable before the remaining interactions could be performed;
  Task 6 is not marked complete. Large-composition latency was not benchmarked.
- No PR, merge, version bump or release requested for this feature.

Native continuation: `/tmp/nitrogen-composition-ui` alone was trusted (canonical
`/private/tmp/nitrogen-composition-ui`); no parent trust was added. The existing-destination fixture
is prepared with `Shared.ngr`, an unrelated `Marker` rule, and the `QuoteCalc` alias to isolate
the consuming client. VS Code was reloaded and the whole inline quote selected. On continuation,
`cua.getApp` was unavailable and `cua.getState()` reported no native apps, only browser surfaces.
Do not count the remaining existing-module Apply/live-refresh/undo checks as passed.
