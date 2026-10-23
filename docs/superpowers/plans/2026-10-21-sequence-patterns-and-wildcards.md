# Sequence Patterns and Wildcards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Match multiple selected syntax elements anywhere in an ordered list, with typed scalar and sequence discards.

**Architecture:** Extend the existing composed quotation parser with an internal pattern mode and occurrence-local wildcard contracts. Compile list patterns to ordered sequence plans and match using iterative global backtracking with transactional captures. Preserve existing public entry points while adding explicit search options.

**Tech Stack:** C#, generated Nitrogen parsers, xUnit, existing grammar helper and language-service infrastructure.

**Spec:** `docs/superpowers/specs/2026-10-21-sequence-patterns-and-wildcards-design.md` (approved 2026-10-21).

## Global Constraints

- Return the first complete match; try variable-length segments shortest to longest in pattern-source order.
- `$_` consumes exactly one node; `..$_` consumes zero or more list items. Neither creates a binding.
- Wildcard categories come from individual grammar sites. Reserve `_` in pattern capture declarations only.
- Retain exact structural equality, repeated-name constraints, origin and borrowed lifetime behavior.
- No textual substitution, second parser, unordered matching, all-matches API or implicit subtree traversal.
- Keep ordinary source parsing, public `ParseTemplate` and construction quotation behavior unchanged.
- Default to 1,000,000 work steps per pattern invocation; exhaustion reports `NQP0006` with empty captures.
- Match errors and cancellation must never select a later grammar arm or fallback.
- Preserve unrelated work. Establish an isolated checkout at execution time if needed; no staging, commits or pushing without user authorization.

## Review Focus

1. Parser branches and memoized nodes must retain the correct wildcard category after rollback (Task 1).
2. A later repeated binding must be able to reopen a previously successful nested split (Task 3).
3. Adjacent anonymous segments must terminate deterministically without exposing synthetic names (Task 3).
4. Input hidden behind a discard must still undergo validity checks and consume the shared budget (Task 2).
5. Quotation editor analysis must not interpret ordinary string/comment dollar text as wildcard syntax (Task 5).

## File responsibilities

- `Nitrogen.Runtime/Language.Patterns.cs`, `Language.cs`: pattern-mode parsing and compilation validation.
- `Nitrogen.Runtime/ParserState.cs`, `ParserState.Quotation.cs`: recognition at supported typed sites.
- `Nitrogen.Runtime/BuildArena.cs`, `TreeFreezer.cs`, `SyntaxTree.cs`: occurrence metadata lifetime and freezing.
- New `Nitrogen.Runtime/Quotation/PatternHoleSite.cs`: internal occurrence contract, `Category` and `IsList`.
- `Nitrogen.Runtime/Quotation/QuotePattern.cs`: immutable sequence plans and entry points.
- `Nitrogen.Runtime/Quotation/QuotePatternMatcher.cs`: traversal, choices and capture rollback.
- New `Nitrogen.Runtime/Quotation/QuoteMatchOptions.cs` and `QuoteMatchBudget.cs`: public options and invocation-local accounting.
- `Nitrogen.Runtime/Semantics/FileSemantics.Rewrites.cs`: helper match/error propagation.
- `Nitrogen.Grammar/QuoteHelperAnalysis.cs`, `GrammarValidator.cs`: reserved-name and pattern validation.
- `Nitrogen.LanguageService/NitrogenLanguageService.Quasiquotes.cs`, `NitrogenLanguageService.CSharpQuotations.cs`, `CSharpQuotations.cs`: pattern-aware editor features.
- Tests and documentation are listed with their owning tasks below.

## Task 1: Typed, occurrence-local discard parsing

**Files:** Runtime parser/metadata files above; new `Nitrogen.Tests/QuotePatternWildcardTests.cs`; `Nitrogen.Tests/TemplateListParsingTests.cs`.

**Interfaces:** Add internal `Language.ParsePattern(string text, string category, IReadOnlyDictionary<string, QuoteHole> captures) : ParseResult`. Add internal pattern-mode state to `ParserState`; add `SyntaxTree.PatternHoleSites : IReadOnlyDictionary<int, PatternHoleSite>` keyed by frozen node identity. `PatternHoleSite` records category and scalar/list shape. Public template APIs remain unchanged.

- [x] Add failing public-API tests: `Scalar_discard_has_no_binding` asserts `$_` matches one compatible root with empty captures; `Mixed_discard_shapes_need_no_declarations` compiles `f($_, ..$_)` with an empty map; `Discard_occurrences_keep_independent_categories` uses two different supported syntax categories.
- [x] Add failing parser regression tests for branch rollback, memoized nodes, incompatible surviving interpretations, `$_Name`, comment/string dollar text and overlapping tokens. Assert reserved `_` contracts yield `NQP0002`, supported discard sites compile, and unsupported sites fail compilation. Keep construction `_` contracts and ordinary parsing unchanged.
- [x] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuotePatternWildcardTests|FullyQualifiedName~TemplateListParsingTests'`; confirm failures identify missing discard behavior.
- [x] Implement pattern mode through the existing parse pipeline. Reuse `SyntaxKinds.Hole`/`ListHole` with per-node wildcard contracts; record contracts only for exact `_` spellings at supported sites. Copy/remap metadata during freezing and detached tree copies, clear/remove it with arena nodes, and preserve branch/memoization correctness. Reject ambiguous parses using the existing parser contract. Route `CompilePattern` through `ParsePattern` and exclude discards from declared-capture checks.
- [x] Match wildcard nodes with category validation and no binding. Keep the existing one-segment list restriction until Task 3.
- [x] Rerun the focused command plus `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuoteApiTests|FullyQualifiedName~QuoteOwnershipTests|FullyQualifiedName~QuotePatternTests'`; require all passing.

## Task 2: Bounded matching and cancellation

**Files:** New options/budget files, `QuotePattern.cs`, `QuotePatternMatcher.cs`, helper evaluation; new `Nitrogen.Tests/QuotePatternBudgetTests.cs`.

**Interfaces:** Public sealed record `QuoteMatchOptions` with init properties `int MaxWorkSteps = 1_000_000` and `CancellationToken CancellationToken`. Add `QuotePattern.Match(QuoteNode input, QuoteMatchOptions options) : QuoteMatchResult`; preserve existing overload. Internal `QuoteMatchBudget.Step()` accounts one unit and checks cancellation. Pass one instance through validation, comparisons and subsequent split search. Add optional options to the internal `MatchResolved` path without changing existing callers.

- [x] Write `Invalid_budget_is_an_argument_error`, `Exhaustion_is_diagnostic_not_mismatch`, `Cancelled_match_throws`, and `Discarded_input_is_still_validated`. Assert nonpositive limits throw `ArgumentOutOfRangeException`; exhaustion returns only `NQP0006` and no captures; cancellation throws `OperationCanceledException`; invalid discarded input retains existing invalid-input diagnostics when budget permits validation to finish.
- [x] Add a deterministic small-tree accounting test covering validation and structural equality, and helper integration proving a match diagnostic prevents fallback. Bound equality work for repeated captures as well as ordinary traversal.
- [x] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~QuotePatternBudgetTests`; confirm failures for the new API/behavior.
- [x] Implement shared accounting and error propagation. Define a single internal budget-exhaustion signal caught at the match entry point; do not catch caller cancellation as mismatch. Reject null options consistently with argument checks. Ensure helper evaluation retains its existing stop-on-diagnostics behavior.
- [x] Rerun focused budget and existing pattern/helper tests; require all passing.

## Task 3: Multiple list segments and global backtracking

**Files:** `Language.Patterns.cs`, `QuotePattern.cs`, `QuotePatternMatcher.cs`; new `Nitrogen.Tests/QuoteSequencePatternTests.cs`; `Nitrogen.Tests/QuotePatternTests.cs`.

**Interfaces:** Replace `QuotePatternList.HoleIndex` with an ordered item/segment plan retaining `TemplateListShape`. Internal segment descriptors distinguish named captures and discards and retain source-node identity. Keep all public capture types unchanged; use the Task 2 invocation budget.

- [x] Add `Two_selected_elements_use_earliest_viable_positions`: pattern `f(..$_, $X, ..$_, $Y, ..$_)` on `f(1, 2, 3)` captures `1`, `2`. Add a three-element counterpart and exact-one-gap cases.
- [x] Add `Adjacent_segments_are_shortest_first`: `f(..$A, ..$B)` on `f(1, 2)` captures empty `A` and `[1,2]` as `B`; adjacent anonymous segments succeed with no bindings.
- [x] Add `Later_equality_reopens_nested_choices`: `f(f(..$_, $X, ..$_), $X)` on `f(f(1, 2), 2)` captures `2`. Add repeated-list cases in one physical list, mismatch rollback, a failed candidate followed by success, and repeated calls on one compiled pattern.
- [x] Cover empty and singleton-collapsed input, required lists, separators, unseparated lists, minimum remaining-length pruning, category mismatch, preserved origins and borrowed captures. Add stack-depth and adversarial budget tests. Retain unsupported token-separated-site rejection; remove only the old rejection expectation for supported multiple segments.
- [x] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuoteSequencePatternTests|FullyQualifiedName~QuotePatternTests'`; confirm failures on the new supported layouts.
- [x] Implement iterative depth-first search with explicit work/choice stacks, suffix minimum counts and a capture trail. Choice frames restore all continuation work and bindings, including across nested lists. Compare repeated bindings structurally without mutation; use segment views while searching and publish immutable named captures only after complete success. Charge comparisons and each attempted split to the shared budget. Keep deterministic zero/one-segment fast paths only where equivalent.
- [x] Run all pattern, budget and quotation ownership tests; require passing, empty captures on every failure, and no recursive search/equality stack growth.

## Task 4: Grammar-authored sequence patterns

**Files:** `Nitrogen.Grammar/QuoteHelperAnalysis.cs`, `GrammarValidator.cs`; `Nitrogen.Tests/Grammars/QuoteHelpers.ngr`; `Nitrogen.Tests/QuoteHelperRuntimeTests.cs`; grammar/workspace `QuoteHelperValidationTests.cs`.

**Interfaces:** Existing `QuoteMatchArmSpec` capture maps include named captures only; shared `CompilePattern` supplies discard parsing. No new `.ngr` declaration syntax or generator surface is required.

- [x] Add runtime tests for two/three selected elements, scalar/list discards, shortest-first capture selection and unchanged ordered arms/final fallback. Assert exact reconstructed syntax or lowered result, not merely successful compilation.
- [x] Add validation tests for reserved `_` declarations, unknown named captures, mixed scalar/list named uses, wildcard use in results without a construction binding, and invalid unused helper arms. Verify helper errors retain authored pattern locations.
- [x] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~QuoteHelperRuntimeTests|FullyQualifiedName~QuoteHelperValidationTests|FullyQualifiedName~QuoteHelperGrammarTests'`; confirm the new negative/positive cases expose missing integration.
- [x] Integrate reserved-name validation and pattern-mode metadata through helper validation. Preserve capture shadowing, final fallback requirements, original-selector semantics and error propagation. Reuse shared matching rather than implementing helper-only search.
- [x] Rerun the command and existing DateCalc helper tests; require passing.

## Task 5: Editor features and documented behavior

**Files:** Language-service files above; `Nitrogen.Tests/LanguageService/CSharpQuoteNavigationTests.cs`, `QuoteHelperNavigationTests.cs`, `ListSpliceNavigationTests.cs`; `docs/language-guide.md`, `docs/editor-support.md`, `README.md`, `.agents/skills/nitrogen/references/language-design.md`.

**Interfaces:** Editor pattern analysis uses the shared pattern-mode parse and occurrence categories. Construction quotation analysis continues using existing template parsing. No synthetic `_` declaration or capture-map entry is introduced.

- [x] Add C# and grammar tests asserting wildcard hover reports its actual category/shape, completion offers the appropriate discard spelling, and wildcard occurrences have no definition/reference/rename target. Named capture features must still work alongside multiple wildcard occurrences.
- [x] Add opaque dollar text, `$_Name`, construction-context and overlapping-token regressions; assert analysis of each reflects actual parsed holes. Add source-span checks for reserved-name and unsupported-site errors.
- [x] Run `dotnet test Nitrogen.Tests --filter 'FullyQualifiedName~CSharpQuoteNavigationTests|FullyQualifiedName~QuoteHelperNavigationTests|FullyQualifiedName~ListSpliceNavigationTests'`; confirm new feature failures.
- [x] Implement pattern-aware editor plumbing and update docs with ordered/shortest-first examples, independent discard semantics, repeated equality, `_` compatibility, borrowing, budget error and cancellation behavior. Replace the one-segment limitation in the skill reference only as implementation lands.
- [x] Rerun focused editor tests.
- [ ] Reproduce actual configured-language interactions in VS Code/Rider. Automated service checks pass; visual verification remains pending.

## Task 6: Integration and performance acceptance

**Files:** Extend sequence/budget tests where integration exposes gaps; record evidence in new `docs/superpowers/plans/2026-10-21-sequence-patterns-and-wildcards-validation.md`.

**Interfaces:** Completed public C# API, generated helper path and editor parsing must share one syntax/behavior contract.

- [x] Run `dotnet test Nitrogen.Tests` and `./build.sh`; require zero failures or document unrelated baseline failures with evidence. Do not infer live editor acceptance from these gates.
- [x] Measure single-segment baseline, two/three selected elements, nested repeated constraints and adversarial failure at increasing list lengths. Record input sizes, step counts, elapsed time and allocation observations; confirm ordinary examples fit the default and adversarial cases terminate with `NQP0006`.
- [x] Keep the approved 1,000,000-step default unless measurements justify proposing a design adjustment. Do not silently change match selection or resource behavior to make benchmarks pass.
- [x] Review spec coverage and final diff, confirming no construction/source parsing regressions, no leaked captures, and no later-arm selection after errors. Record focused, full-build, measured and live-editor evidence as separate outcomes.

## Execution handoff

Recommended: native execution in this chat, since parser metadata, search state and helper/editor contracts depend closely on each other. Subagent-driven execution with per-task review is an alternative. Review the plan and select an approach before implementation. Implementation and automated acceptance are complete locally; see the validation ledger. Live editor visual acceptance remains pending.
