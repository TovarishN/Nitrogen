# Quasiquote pattern matching validation

Implementation: C# CompilePattern/Match and ordered typed `.ngr` match arms, scalar/list captures, caller bindings, editor navigation/rename and DateCalc examples.

## Manual acceptance follow-up: misplaced reference hints

The user reported two `= 3` hints on `let count = 3;`, with no hint on `count swapped;`.
The packaged-server reproduction confirms that the fallback's value is placed at the initializer's statement.
Hint placement incorrectly inferred the source statement from the lowered HIR value's first origin.
An initialized reference intentionally keeps its initializer's origin, so that origin is not a statement identity.

The fix positions hints from selected source syntax in the same preorder used by LowerSelected.
It leaves HIR provenance unchanged. An initial attempt to prepend reference origins was withdrawn after
ReferenceInitializerTests caught two provenance-contract failures.

Three new regression cases (plain reference, match fallback, parenthesized fallback) first failed with
both hints on the declaration, then passed with separate source-statement positions. Focused verification
passes 36/36; the complete Release suite passes **1757/1757** with no skipped tests.

Logs: `/tmp/nitrogen-reference-hint-red.log`, `/tmp/nitrogen-reference-hint-final-green.log`,
`/tmp/nitrogen-reference-hint-final-full.log`, and `/tmp/nitrogen-reference-hint-bundled-red.log`.
The scratch VSIX is being refreshed for the user's next native check; visual confirmation remains pending.

## Automated evidence before native acceptance

Product commit before native acceptance: `cde69743cc2b09ea4aec14b008e73f3f07267756`.
Packaged identity: `0.14.0+build.169.cde69743cc2b09ea4aec14b008e73f3f07267756`.
The subsequent completion commit changes only documentation.

- Release solution tests: **1754/1754**, no skipped tests. Repeated after the review fixes and in canonical packaging.
- Canonical `build.sh`: successful Release build with **0 warnings and 0 errors**, full tests, VSIX and Rider packages, shared Rider tests, and each bundled server's initialize/shutdown smoke.
- VS Code client tests: **40/40**; no client code changed during the final review fixes.
- Packaged metadata: VSIX manifest and Rider plugin.xml both **0.14.0**. This is local distribution verification, not a new release.
- Four NuGet packages: Runtime, Generator, CLI and Build successfully packed sequentially; all nuspec versions **0.14.0**.
- Disposable consumer `/tmp/nitrogen-pattern-consumer`: no project references, restored from `/tmp/nitrogen-pattern-packages-final` into a fresh `packages-final` cache. Scalar/list captures, structural construction/lowering, imported grammar match arms/fallback, and snapshot isolation all pass.
- Actual VSIX-bundled server checks the prepared `pattern-matching.patterncalc` demo with exit 0 and no diagnostics.

Proof logs: `/tmp/nitrogen-pattern-review-full.log`, `/tmp/nitrogen-pattern-task7-completion.log`, `/tmp/nitrogen-pattern-final-package.log`, `/tmp/nitrogen-pattern-final-pack.log`, `/tmp/nitrogen-pattern-final-consumer.log`, `/tmp/nitrogen-pattern-final-demo.log`, and `/tmp/nitrogen-pattern-client-tests.log`.

Local editor artifacts: `artifacts/nitrogen-0.14.0.vsix` and `artifacts/rider/nitrogen-rider-0.14.0.zip`.
No publication, merge, native UI success or performance benefit is claimed.

## Independent review

A fresh whole-branch review found two Important issues. Both are fixed with regressions:

- Capture rename could switch the surrounding parsed rule through lookahead. Rename now compares the complete quote structure as well as capture identities. The reproducer failed before the fix and passes afterward.
- A selected arm whose construction failed lost its result-source location. Failed instances now retain that source; lowering and editor diagnostics expose it as a related location. Runtime and editor regressions failed before the fix and pass afterward.

No deferred minor findings. The reviewer declined independent full-suite, performance and native UI/package-install judgments; automated distribution checks are performed separately. No performance claim is made.

## Rulings I made

The following records the execution decisions in chronological order, including the cost if wrong.

- Ruling: packaged skill scripts have CRLF shebangs; copied them with normalized newlines to /tmp/nitrogen-plan-tools — avoid modifying skill installation — no product impact. Cost if wrong: Only the disposable execution scripts change; incorrect normalization would block execution.

- Task 1 Ruling: use existing Scopes/Calc/Lexical fixtures for declaration, wrapper and opaque-marker checks rather than add duplicate test grammar — same parser contracts, less fixture maintenance. Cost if wrong: A missing fixture-specific behavior would need an additional targeted grammar.

- Task 2 Ruling: the Nitrogen separated-list grammar has no configurable trailing-separator modes (SyntaxCodeWriter always rolls back unmatched separator). Test its actual forbidden-trailing contract and recovered-input rejection; do not add CsNitra-only modes to this feature. Cost if wrong: Configurable trailing separators remain outside Nitrogen grammar support.

- Task 3 Ruling: add three explicit Grammar semantic types (QuoteMatchBody, QuoteMatchArm, QuoteCaptures) so self-hosting lowers/project all structure; update reviewed type registry expectation 21→24. Full pre-fix 1697 passed / 2 failed (truncation + registry count); no recovery snapshot change. Cost if wrong: Consumers enumerating semantic types must accept the three new types.

- Task 4 Ruling: retain existing syntax-only workspace pipeline unchanged; it already emits helpers and invokes LanguageBuilder's complete validation without configured C# initializers. Extend LanguageBuilder's source mapping to arm sources instead of adding a second workspace validator. Cost if wrong: All-arm validation relies on the existing workspace compilation path.

- Task 4 Ruling: Grammar targets netstandard2.0, so use ContainsKey/Add rather than Dictionary.TryAdd in static analysis. Runtime net10.0 uses TryAdd normally. Cost if wrong: No behavior difference; the older target retains compatible dictionary operations.

- Task 5 Ruling: outer swap(swap(1+2)) falls back on the original inner Swapped syntax, then lowers the inner swap once to 2+1; do not match after expansion (spec original-syntax requirement). Cost if wrong: Users wanting expansion-aware matching need a separate explicit future feature.

- Task 5 Ruling: existing dynamic root lowering admits only HirOperation and rejected literal/fallback results (two NH0003 RED cases). Add an internal StructuralResult flag for rewrite registrations only; non-operation results must equal the selected syntax's computed semantic type. All operation signature checks remain — required for the approved arbitrary syntax-category helper return contract. Cost if wrong: Rewrite roots admit correctly typed non-operation values; operation admission remains strict.

- Task 5 Ruling: bare explicit literals are not registered roots in the existing parity oracle; compare literal helper returns to hand-derived HirConstant values, and use f(...) for explicit-program parity. This preserves the new StructuralResult regression rather than weakening it. Cost if wrong: Literal parity uses expected HIR rather than a registered explicit literal root.

- Task 5 Ruling: add PatternRefs.ngr and its explicit AdditionalFiles entry to isolate caller-binding tests from existing initializer fixtures; no change to existing grammar behavior. Cost if wrong: The dedicated fixture must remain included in generated test inputs.

- Task 6 Ruling: signed fixture must include the defining grammar's required language declaration (NGR0402 proved it was invalid); exercise normal signed discovery with comments/blank lines and no redundant nitrogen.json. Preserve original offsets with masked text. Local quotation compilation now also masks signatures consistently. Cost if wrong: Signature masking must continue to preserve original text offsets.

- Task 6 Ruling: report one NQP0003 per physical list, not one per hole; C# host diagnostic regression exposed duplicate same-layout errors. Keep first marker's location. Cost if wrong: The first list hole is the reported location for an invalid multi-hole list.

- Task 6 Ruling: existing pure Extract Block already refuses every helper declaration and quote extraction selects only inline rewrite return quotes. Keep those guards and add a precise match-helper/subpattern refusal at the service boundary; no model flattening or new extraction behavior. Cost if wrong: Match-helper/subpattern extraction remains unavailable with a refusal explanation.

- Task 7 Ruling: use postfix precedence 5 for the opt-in swapped example so it receives the whole addition; do not reserve swapped as an identifier, preserving existing variable names. Reversal can still fail ordinary semantic checks (date+duration is not commutative); document syntax versus semantic equivalence. Cost if wrong: Structurally valid swaps can still fail ordinary semantic checks for noncommutative operations.

- Task 7 Ruling: list example/round-trip acceptance reuses Task 2 and Task 5 QuoteHelpers tests (empty/fixed prefix/suffix, caller refs) rather than add a duplicate DateCalc variadic function. Cost if wrong: List examples live in the reusable test fixture rather than the DateCalc sample.

- Task 7 Ruling: build.sh is the canonical Release solution build, full Nitrogen.Tests gate and both editor packages; use it for those gates rather than duplicate the same build/tests before packaging. The solution has one test project. Run client npm tests separately. Cost if wrong: The canonical script remains the owner of package/build gate behavior.

- Task 7 Ruling: solution-wide parallel pack triggered concurrent CLI Publish from Nitrogen.Cli and Nitrogen.Build.BundleNitrogenChecker (GenerateDepsFile file lock). Match packages.yml's sequential four-project packing; no source/packaging behavior change needed. Cost if wrong: Solution-wide concurrent packing remains unsafe; distribution uses the CI sequential convention.

- Final: Ruling: retain construction failures at the existing lowering stage with NH0010/NH0009, attaching the selected arm as related source — adding semantic errors changes admission to generic NH0003 and breaks established diagnostics; cost if wrong: consumers expecting semantic-stage reporting still need the lowering diagnostics API. Cost if wrong: Consumers expecting semantic-stage errors must inspect lowering diagnostics for these failures.

- Final: Ruling: independent reviewer declined fresh full-suite, quantitative performance, and native UI/package-install judgments — execute full-suite and isolated package checks here, keep native UI explicitly pending, and make no performance claim; cost if wrong: native editor behavior remains unverified until manual checks. Cost if wrong: Native editor behavior remains pending manual acceptance; no performance result is claimed.

## Native VS Code acceptance

Native computer control is unavailable in this session. These checks are prepared for manual execution; no native UI pass is claimed. Automated editor service and client tests are separate evidence.


Run `bash /tmp/nitrogen-composition-ui/run-pattern-demo.sh` after local packaging completes.
It uses a separate temporary VS Code profile and extension directory; your regular extensions remain available in your regular window.
Trust only the scratch project if prompted. Use View → Editor Layout → Split Right, with DateCalc.ngr on the left and pattern-matching.patterncalc on the right.

1. In DateCalc.ngr, go to definition on `Value` after `match`: it should open the helper parameter.
2. Go to definition on `$Left` in the pattern and result: both should open that arm's `Left: Expr` declaration. Go to definition on `Expr` and `+`: expect the category and Add rule.
3. Rename `Left` to `First`: its declaration and its two real holes should change, with no unrelated edits. Undo afterward.
4. In PatternMatching.cs, go to definition on a `$Left` capture inside the CompilePattern string: expect its capture-map key. Go to definition on the quoted `+`: expect the DateCalc Add rule.
5. Confirm the sample shows 3, 5, 17 days, 3 and 5, with no errors or duplicate hints. Hover the swapped expressions to inspect their lowered operations.
6. Add an unselected arm before the fallback, `| <[ nonsense ]> => <[ 1 ]>;`. Expect a pattern diagnostic at that arm. Remove it and confirm diagnostics clear without restarting the server.
7. Change the selected result from `$Right + $Left` to `$Right * $Left`: after saving, `count + 2 swapped` should evaluate to 6. The duration example can report its ordinary type error; restore the original addition and confirm 5 and 17 days return.
8. Try Extract Quote Helper within a pattern. Expect an explanation that match helpers/subpatterns cannot be extracted. Existing complete inline rewrite quotes remain extractable.

The generic extension reloads workspace grammars automatically. This scratch launch uses the generic extension, so no generated-extension development setting is necessary.
Native UI verification has not been claimed; automated language-service, runtime and client checks are recorded separately.
