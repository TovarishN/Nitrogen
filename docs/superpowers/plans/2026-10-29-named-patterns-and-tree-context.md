# Named Patterns and Tree Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add named (recursive, multi-alternative) syntax patterns, descendant search at a hole, an
`inside`/`parent` ancestor clause on structural diagnostics, and `FindAll`.

**Architecture:**
- Grammar modules emit `NamedPatternSpec`s. The `Language` compiles them once into a shared table of
  `NamedPatternDefinition`s, created as empty shells first so that recursive references can bind to
  instances.
- `QuotePatternMatcher` keeps its work-list/choice-stack machine. It gains frames, so a job knows
  which pattern tree it walks, and capture scopes, so a callee's captures stay private.
- Two new job kinds: pattern reference (alternatives become choice points) and descendant search
  (each candidate becomes a choice point).
- The ancestor clause runs in `SemanticsCheck`. It shares one budget with the main pattern and skips
  ancestors whose syntax fails validation.

**Tech Stack:** C# / .NET 10, xUnit, the Nitrogen source generator (`.ngr` → generated modules), the
bootstrap `GrammarParser` and the self-hosted `Nitrogen.Ngr` parser.

**Spec:** `docs/superpowers/specs/2026-10-29-named-patterns-and-tree-context-design.md`

## Global Constraints

- Work on a new branch `named-patterns` cut from an up-to-date `main`. Commit after every task.
- Keep the existing `MaxWorkSteps` default of `1_000_000`. Every new loop calls `budget.Step()`.
- `Match(node)` stays anchored to one node, and existing pattern tests must pass unchanged.
- No recursion on the C# stack for tree traversal or pattern references. Use explicit jobs and stacks.
- Named-pattern captures are private to their alternative. Only the caller's binding is published.
- A repeated descendant capture, and `..P*` (descendant search on a list capture), are compile errors.
- Main-pattern capture names and context-pattern capture names may not overlap. This is a compile
  error, not an equality join.
- An ancestor whose subtree fails input validation is skipped as non-matching, with no error. This is
  the user's decision.
- Budget exhaustion in structural diagnostics keeps today's behavior: a thrown
  `InvalidOperationException`, reported as a check failure.
- Both grammar parsers must produce equal `GrammarFile` models for every new form.
- Diagnostic codes:
  - `NQP0007`: unknown named pattern.
  - `NQP0008`: non-consuming reference cycle. The message names the cycle as `A -> B -> A`.
  - `NQP0009`: invalid pattern capture. This covers a category that doesn't fit the site, a repeated
    descendant capture, and a descendant list.
  - `NGR0163` `UnknownNamedPattern`, `NGR0164` `DuplicateNamedPattern`, `NGR0165`
    `ContextCaptureCollision`, `NGR0166` `InvalidPatternCapture`.
- New keywords (`pattern`, `inside`, `parent`) are contextual. Each must remain a valid identifier and
  semantic property name, as `match` does today.

## Review Focus

1. **`FindAll` on a file with one syntax error elsewhere.** It should still return matches in the
   valid parts. Candidates whose own subtree is invalid are skipped. This applies the user's "skip"
   decision to `FindAll`. (Test in Task 4.)
2. **Very deep input**, for example 5,000 nested `f(f(…))` calls, under a recursive pattern and under
   descendant search. Either it matches or it returns `NQP0006`. It must never throw
   `StackOverflowException`. (Tests in Tasks 2 and 3.)
3. **A caller and a named pattern alternative using the same capture name.** Neither should see or
   constrain the other's binding. (Test in Task 2.)
4. **`inside` on the root node, which has no ancestors.** No diagnostic and no error. (Test in Task 7.)
5. **A named pattern used from a module that extends its declaring module**, by both its simple and
   its qualified name. (Test in Task 6.)

---

### Task 1: Named pattern declarations, `QuoteHole` forms and compile-time validation

**Files:**
- Create: `Nitrogen.Runtime/Quotation/NamedPatterns.cs`
- Modify: `Nitrogen.Runtime/Quotation/QuoteTypes.cs`, `Nitrogen.Runtime/SyntaxModule.cs`,
  `Nitrogen.Runtime/Language.Patterns.cs`, `Nitrogen.Runtime/Quotation/QuotePattern.cs`,
  `Nitrogen.Runtime/LanguageBuilder.cs:60-75`
- Test: `Nitrogen.Tests/NamedPatternCompileTests.cs`

**Interfaces:**
- Produces, all public in `NamedPatterns.cs`:
  - `sealed record NamedPatternSpec(string Name, string Category, IReadOnlyList<NamedPatternAlternative> Alternatives, QuoteHelperSource Source)`.
    `Name` is qualified (`Module.Name`).
  - `sealed record NamedPatternAlternative(string Text, IReadOnlyDictionary<string, QuoteHole> Captures, QuoteHelperSource Source)`.
- Produces, internal:
  - `sealed class NamedPatternDefinition { string Name; string Category; QuotePattern[] Alternatives; }`.
    `Alternatives` is assigned after construction.
  - `sealed class NamedPatternTable { NamedPatternDefinition? Find(string qualifiedName); IReadOnlyList<(NamedPatternSpec Spec, QuoteDiagnostic Diagnostic)> Errors; }`.
- `SyntaxModule`: `public virtual IReadOnlyList<NamedPatternSpec> NamedPatterns => [];`
- `QuoteHole` gains `public string? PatternName { get; init; }` and `public bool Descendant { get; init; }`, with factories:
  - `QuoteHole.Pattern(string qualifiedName)`
  - `QuoteHole.PatternList(string qualifiedName)`
  - `QuoteHole.Descendant(string qualifiedName)`

  The factories set `Category = ""`. `CompilePattern` replaces it with the definition's category.
- `Language`: `internal NamedPatternTable NamedPatternTable { get; }`, lazily built once per `Language`.
  Its patterns live as long as the `Language`, like `_diagnosticPatterns`.
- `QuotePattern` gains `internal IReadOnlyDictionary<string, NamedPatternDefinition> References`,
  keyed by capture name.

- [ ] **Step 1: Write failing tests.** Use a hand-written test module,
  `sealed class PatternsModule(params NamedPatternSpec[] specs) : SyntaxModule("P")` (as in
  `ModuleComposerTests.EmptySyntaxModule`), composed with `QuoteListsModule.Instance`. Tests:
  - `Pattern_capture_takes_definition_category`: spec `P.One : QuoteLists.Expr = <[ 1 ]>`.
    `CompilePattern("QuoteLists.Expr", "f($x)", {x: QuoteHole.Pattern("P.One")})` succeeds.
  - `Unknown_pattern_reports_NQP0007`.
  - `Direct_non_consuming_cycle_reports_NQP0008`: `P.A = (x: P.A) <[ $x ]>`, and the message contains `P.A -> P.A`.
  - `Indirect_cycle_through_descendant_reports_NQP0008`: `P.A = (x: ..P.B) <[ $x ]>` and `P.B = (y: P.A) <[ $y ]>`.
  - `Consuming_recursion_is_accepted`: `P.Nested = (n: P.Nested) <[ f($n) ]> | <[ 1 ]>`.
  - `Repeated_descendant_capture_reports_NQP0009`: `f($s, $s)` with `s: Descendant`.
  - `Descendant_list_reports_NQP0009`. Construct with `QuoteHole.Descendant(..) with { IsList = true }`.
  - `Composition_surfaces_named_pattern_errors`: `LanguageBuilder.TryBuild` fails with `NQP0008` at the spec's source.

- [ ] **Step 2: Run them and confirm they fail to compile or fail.**
  Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~NamedPatternCompileTests`.
  Expected: build errors (the types don't exist yet).

- [ ] **Step 3: Implement.**
  - Build the table in two passes:
    1. Create a shell for every spec across `Modules`.
    2. Compile each alternative through an internal `CompilePatternCore(category, text, captures, NamedPatternTable table)`.
       Public `CompilePattern` calls it with `NamedPatternTable`.
  - `CompilePatternCore` resolves each `PatternName` (unknown → `NQP0007`) and substitutes its category.
    The existing category-metadata and list-site checks then run unchanged. It also checks the
    `NQP0009` cases.
  - Cycle detection:
    - An alternative contributes a non-consuming edge when its parsed root is a `Hole` (after skipping
      `Group` wrappers) whose contract has `PatternName` set, whether or not it is a descendant search.
    - Report each cycle once, by DFS over the definitions in declaration order.
  - In `LanguageBuilder`, report `NamedPatternTable.Errors` alongside `patternErrors`, using the same
    `CompositionDiagnostic` location mapping.

- [ ] **Step 4: Run the tests until they pass.** Also run the existing suites:
  `dotnet test Nitrogen.Tests --filter "FullyQualifiedName~QuotePattern|FullyQualifiedName~QuoteSequence"`
  should show PASS.

- [ ] **Step 5: Commit** with the message `Add named pattern declarations and compile-time validation`.

### Task 2: Matcher frames, private capture scopes and pattern-reference jobs

**Files:**
- Modify: `Nitrogen.Runtime/Quotation/QuotePatternMatcher.cs`, `Nitrogen.Runtime/Quotation/QuotePattern.cs`
- Test: `Nitrogen.Tests/NamedPatternMatchTests.cs`

**Interfaces:**
- Consumes the `References`, `NamedPatternDefinition` and `NamedPatternTable` from Task 1.
- Produces these internal matcher records:
  - `Frame(SyntaxTree Pattern, IReadOnlyDictionary<string, QuoteHole> Contracts, IReadOnlyDictionary<int, QuotePatternList> Lists, IReadOnlyDictionary<string, NamedPatternDefinition> References, int Scope)`.
  - Every existing job gains a `Frame Frame` first parameter.
  - `ReferenceJob(Frame Caller, NamedPatternDefinition Target, QuoteNode Input, string? BindName, int Alternative = 0)`.
  - `BindJob(Frame Caller, string Name, QuoteNode Value)`.
- Captures are keyed by `(int Scope, string Name)`. The outer pattern is scope 0, and only scope 0
  is published. Each reference attempt allocates a fresh scope.
- Produces `QuotePatternMatcher(QuotePattern root, Func<QuoteNode,QuoteNode>? resolve, QuoteMatchBudget budget)`.
  It replaces the 5-argument constructor. Later tasks start it from either a parsed pattern or a
  named definition.

- [ ] **Step 1: Write failing tests**, using the Task 1 test module:
  - `Second_alternative_is_tried_on_alternative_mismatch`: `P.Lit = <[ 1 ]> | <[ 2 ]>`. Pattern `f($x)` with `x: P.Lit` matches `f(2)`.
  - `Repeated_reference_capture_uses_equality`: pattern `f($x, $x)` with `x: P.Lit` matches `f(2, 2)`
    and rejects `f(1, 2)`.
  - `Successful_reference_is_committed`: `P.Wide = (a: QuoteLists.Expr*) <[ f(..$a, ..$_) ]> | <[ 1 ]>`.
    The pattern `f($x, 9)` with `x: P.Wide` runs on an input whose first argument is `f` with 200
    items and whose second argument is `8`. It returns a mismatch within `MaxWorkSteps = 2_000`, which
    shows the callee's span choices were cut rather than retried.
  - `Private_captures_do_not_leak`: after matching `f($x)` with `x: P.Pair`, where `P.Pair = (a: Expr) <[ $a + $a ]>`,
    the result has exactly the key `x`.
  - `Same_capture_name_in_caller_and_callee_is_independent` (Review Focus 3): the caller declares `a`,
    the callee alternative also uses `a`, and the input has different syntax at each.
  - `Callee_repeated_capture_equality_still_applies`: `P.Pair` rejects `1 + 2` and accepts `1 + 1`.
  - `Recursive_pattern_matches_several_depths`: `P.Nested` (Task 1) matches `1`, `f(1)` and `f(f(f(1)))`, and rejects `f(2)`.
  - `List_capture_items_must_each_match`: `f(..$xs)` with `xs: QuoteHole.PatternList("P.Lit")` matches `f(1, 2)` and rejects `f(1, 3)`.
  - `Deep_recursion_hits_budget_not_stack` (Review Focus 2): 5,000 nested `f(…)` with
    `MaxWorkSteps = 10_000` returns `NQP0006`. With the default budget it returns `Matched` and does
    not overflow.

- [ ] **Step 2: Run them and confirm they fail.**
  Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~NamedPatternMatchTests`.

- [ ] **Step 3: Implement the frames, scopes, `ReferenceJob` and `BindJob`.**
  - `ReferenceJob`:
    1. Charge one step and check `AcceptsQuoteNode(Target.Category, input)`.
    2. If `Alternative + 1 < Alternatives.Length`, push a `Choice` that retries with `Alternative + 1`.
    3. Push `BindJob`, then a `NodeJob` for the alternative's root in a new frame with a fresh scope.
  - A successful reference is committed (spec, Matcher architecture). `BindJob` carries the choice-stack
    depth recorded *before* step 2, and pops every choice above it, including the next-alternative
    choice. Add `int ChoiceDepth` to `BindJob`.
  - `BindJob` binds in the caller's scope. If the name is already bound, it does the structural
    equality check instead.
  - A hole whose contract has a reference pushes a `ReferenceJob` instead of binding directly.
  - `MatchSegment` takes `ref Pending? work` and pushes one `ReferenceJob` (with `BindName: null`)
    per item for pattern lists.
  - Rollback stays trail-based: the trail entries become `(Scope, Name)`.

- [ ] **Step 4: Run the new tests and all `QuotePattern*`/`QuoteSequence*` tests.** Expected: PASS.

- [ ] **Step 5: Commit** with the message `Match named pattern references with private capture scopes`.

### Task 3: Descendant search job

**Files:**
- Modify: `Nitrogen.Runtime/Quotation/QuotePatternMatcher.cs`
- Test: `Nitrogen.Tests/NamedPatternMatchTests.cs`

**Interfaces:**
- Produces `DescendantJob(Frame Caller, NamedPatternDefinition Target, QuoteNode[] Candidates, int Index, string BindName)`.
- `Candidates` is the preorder, document-order list of the hole node and its descendants, built once
  with one budget step per node. It skips `NoNode` children and resolves each node through `resolve`.

- [ ] **Step 1: Write failing tests**, using `P.One = <[ 1 ]>` and the pattern `f($s, ..$_)` with `s: QuoteHole.Descendant("P.One")`:
  - `Finds_at_depth_zero`: input `f(1, 5)`.
  - `Finds_deeper_than_one_level`: `f(2 + 3 * 1, 5)`. `s` binds to the `1` leaf, checked by its text and span.
  - `No_candidate_is_a_mismatch`: `f(2, 1)`. The `1` is outside the hole's subtree, so this is not a match.
  - `Moves_to_next_candidate_when_reference_fails`: with `P.Two = <[ 2 ]>` and `s: Descendant(P.Two)`,
    the pattern `f($s, ..$_)` on `f(1 + 2, 5)` binds `s` to the `2` leaf (the third preorder candidate).
  - `Backtracks_from_list_span_into_new_search`: the pattern `f(..$_, $s, ..$_)` with
    `s: Descendant(P.Two)` on `f(1 + 3, 4 * 2)`. The first span tries length 0 and exhausts the
    candidates under `1 + 3`, then retries with length 1. `s` binds to the `2` inside `4 * 2`.
  - `Deep_descendant_search_is_budgeted` (Review Focus 2): 5,000 nested `f(…)` with
    `MaxWorkSteps = 10_000` returns `NQP0006` and no exception.

- [ ] **Step 2: Run them and confirm they fail.**

- [ ] **Step 3: Implement.**
  - A hole whose contract has `Descendant` builds `Candidates` and pushes a `DescendantJob`.
  - A `DescendantJob` at `Index` pushes a `Choice` for `Index + 1` (when one exists), then a
    `ReferenceJob(Caller, Target, Candidates[Index], BindName)`.

- [ ] **Step 4: Run the tests.** Expected: PASS.

- [ ] **Step 5: Commit** with the message `Search descendants for named patterns at a hole`.

### Task 4: Public named patterns and `FindAll`

**Files:**
- Modify: `Nitrogen.Runtime/Language.Patterns.cs`, `Nitrogen.Runtime/Quotation/QuotePattern.cs`,
  `Nitrogen.Runtime/Quotation/PatternCompilation.cs`, `Nitrogen.Runtime/Quotation/QuotePatternMatcher.cs`
- Test: `Nitrogen.Tests/QuotePatternFindAllTests.cs`

**Interfaces:**
- `Language`: `public QuotePattern? NamedPattern(string qualifiedName)`. It returns `null` when the
  name is unknown or failed to compile. The language owns the result, and callers must not dispose it.
- `QuotePattern`: `public QuoteFindResult FindAll(QuoteNode root, QuoteMatchOptions? options = null)`.
- In `PatternCompilation.cs`:
  - `public sealed record QuoteFoundMatch(QuoteNode Node, IReadOnlyDictionary<string, QuoteArgument> Captures)`
  - `public sealed record QuoteFindResult(IReadOnlyList<QuoteFoundMatch> Matches, bool Complete, IReadOnlyList<QuoteDiagnostic> Diagnostics)`
- A `QuotePattern` built for a named definition starts the matcher with a `ReferenceJob` and publishes
  no captures. A parsed pattern starts with a `NodeJob`, as today.

- [ ] **Step 1: Write failing tests:**
  - `Named_pattern_matches_directly`: `language.NamedPattern("P.Nested")!.Match(f(f(1)))`.
  - `FindAll_returns_preorder_document_order`: `P.One` over `f(1, f(1), 1)` returns three nodes in source order.
  - `FindAll_returns_nested_matches`: `P.Nested` over `f(f(1))` returns the outer call, the inner call and `1`.
  - `FindAll_budget_returns_partial_and_NQP0006`: `Complete == false`, at least one match, and the diagnostic code is `NQP0006`.
  - `FindAll_cancellation_throws`: `OperationCanceledException`.
  - `FindAll_skips_invalid_subtrees` (Review Focus 1): parse a source with one recovered error inside
    one argument. Matches in the other arguments are returned, `Complete == true`, and there are no
    diagnostics.

- [ ] **Step 2: Run them and confirm they fail.**

- [ ] **Step 3: Implement.**
  - `FindAll` computes subtree validity bottom-up in one pass over `root`, using the same rules as
    `Validate`, with one budget step per node. It then visits candidates in preorder.
  - For each candidate that is valid and whose category fits, it runs a fresh matcher that skips
    whole-input validation, using one shared `QuoteMatchBudget` for the whole search.
  - On `QuoteMatchLimitException`, it returns the matches found so far plus `NQP0006`.

- [ ] **Step 4: Run the tests.** Expected: PASS.

- [ ] **Step 5: Commit** with the message `Expose named patterns and add QuotePattern.FindAll`.

### Task 5: Grammar syntax in both parsers

**Files:**
- Modify: `Nitrogen.Grammar/GrammarModel.cs`, `Nitrogen.Grammar/GrammarParser.cs` (module members
  near line 228, `ReadQuoteParameters` at 833, `ParseMatchDiagnostic` at 1177),
  `Nitrogen.Ngr/Nitrogen.ngr` (`Member` at 56, `QuoteParameter` at 67, `MatchDiagnostic` at 162),
  `Nitrogen.Ngr/NgrProjector.cs`
- Test: `Nitrogen.Tests/Grammar/NamedPatternSyntaxTests.cs`

**Interfaces:**
- `QuoteParameter` gains a trailing `bool IsDescendant = false`. It is written as `name: ..Category`,
  using the lexer's existing `TokenKind.DotDot`.
- `public sealed record NamedPatternAlternativeDecl(EquatableArray<QuoteParameter> Captures, QuoteText Pattern, GrammarSpan Span)`.
  The suffix keeps it distinct from the runtime `NamedPatternAlternative`.
- `public sealed record NamedPatternDecl(NameDecl Name, NameDecl Category, EquatableArray<NamedPatternAlternativeDecl> Alternatives, GrammarSpan Span)`.
- `ModuleDecl` gains `EquatableArray<NamedPatternDecl> Patterns = default`.
- `public sealed record MatchContext(bool ParentOnly, EquatableArray<QuoteParameter> Captures, QuoteText Pattern, GrammarSpan Span)`.
- `MatchDiagnosticStatement` gains `MatchContext? Context = null`.
- Surface: `pattern Name : Category = [|] alt ( | alt )* ;`, where each `alt` is `[captures] <[ … ]>`.
  The context clause is `inside`/`parent [captures] <[ … ]>`, placed between the pattern quote and `=>`.

- [ ] **Step 1: Write failing tests.** Each one parses with both `GrammarParser.Parse` and
  `NgrParser.Parse`, asserts that neither reports diagnostics, and asserts that the two `File`s are
  equal (as in `MatchDiagnosticTests.Bootstrap_and_self_hosted_parsers_agree_on_diagnostic_matches`):
  - `Pattern_declaration_with_alternatives`: three alternatives, one with captures, and a leading `|`.
  - `Descendant_capture_declaration`: `match (s: ..Secret) <[ … ]> => warning X1001 : "m";`.
  - `Inside_clause`, and `Parent_clause` with captures.
  - `Contextual_keywords_remain_identifiers`: a rule child named `pattern` and semantic properties
    named `inside` and `parent` still parse.
  - Assert the model values in at least one test:
    - `Patterns[0].Alternatives.Length == 3`;
    - `IsDescendant` is set;
    - `Context.ParentOnly` is set.

- [ ] **Step 2: Run them and confirm they fail.**
  Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~NamedPatternSyntaxTests`.

- [ ] **Step 3: Implement both parsers.**
  - In `Nitrogen.ngr`, add a `NamedPattern` member, a `MatchContext` clause and an optional `".."`
    before `QuoteParameter`'s category, with matching `lowers` signatures.
  - `NgrProjector` maps them into the same records.

- [ ] **Step 4: Run the new tests, all `Grammar` tests and `MatchDiagnosticTests`.** Expected: PASS.

- [ ] **Step 5: Commit** with the message `Parse named patterns, descendant captures and context clauses`.

### Task 6: Grammar analysis and emission of named patterns and pattern captures

**Files:**
- Modify:
  - `Nitrogen.Grammar/GrammarAnalysis.cs` (pattern resolution);
  - `Nitrogen.Grammar/QuoteHelperAnalysis.cs:35-60`;
  - `Nitrogen.Grammar/GrammarValidator.cs:260` (match capture checks);
  - `Nitrogen.Grammar/GrammarDiagnostic.cs` (the `NGR0163`–`NGR0166` constants);
  - `Nitrogen.Grammar/SemanticsWriter.cs:155-190`;
  - `Nitrogen.Grammar/QuoteHelperWriter.cs`;
  - `Nitrogen.Grammar/ModuleWriter.cs`.
- Create: `Nitrogen.Grammar/NamedPatternWriter.cs`
- Test: `Nitrogen.Tests/Workspace/NamedPatternGrammarTests.cs`

**Interfaces:**
- `GrammarAnalysis.ResolvePattern(string name, GrammarSpan span, ModuleDecl from) -> (ModuleDecl Module, NamedPatternDecl Pattern)?`.
  It uses the same visibility rules as `Resolve`: own module, `using`, and extended modules.
- `QuoteHelperAnalysis.CaptureContract(QuoteParameter capture, ModuleDecl module, GrammarReport? report) -> (GrammarSymbol Category, string? PatternName)?`.
  - A plain category behaves as `Category()` does today.
  - A name that resolves to a pattern returns the pattern's category symbol and its qualified name.
  - Anything else reports `NGR0163`.
- `QuoteHelperWriter.HoleExpression(string category, bool isList, string? patternName, bool descendant) -> string`.
  It is the single place C# `QuoteHole` construction text is emitted, used for helper parameters, arm
  captures, diagnostic captures and pattern alternatives.
- The generated module overrides `NamedPatterns` with `NamedPatternSpec`s whose `Source` spans point
  at each alternative's quote.

- [ ] **Step 1: Write failing tests.** Use `GrammarWorkspace` as in `MatchDiagnosticTests.Compile`,
  with an XML-like grammar that declares `pattern Secret : Element = …`:
  - `Diagnostic_using_named_pattern_reports`: `match (s: Secret) <[ <credentials ..$_>..$_ $s ..$_</credentials> ]>`
    warns on matching input. The message `{s}` shows the element text.
  - `Descendant_capture_reports_on_outer_node`: `(s: ..Secret)` with a password nested two levels down
    gives exactly one warning, spanning `<credentials>`.
  - `Syntax_function_arm_uses_named_pattern`.
  - `Unknown_pattern_reports_NGR0163`.
  - `Duplicate_pattern_or_rule_name_reports_NGR0164`.
  - `Descendant_list_or_repeated_descendant_reports_NGR0166`.
  - `Extending_module_uses_pattern_by_simple_and_qualified_name` (Review Focus 5).
  - `Non_consuming_cycle_fails_composition_with_NQP0008`, located at the alternative's quote.

- [ ] **Step 2: Run them and confirm they fail.**

- [ ] **Step 3: Implement.**
  - Duplicate checks cover rules, helpers and patterns within a module.
  - Typed message capture views use the pattern's category view type. Find the generator code with
    `grep -rn SemanticPatternCaptures Nitrogen.Grammar`.

- [ ] **Step 4: Run the new tests, `MatchDiagnosticTests`, `DiagnosticCaptureMessageTests` and `XmlPatternExampleTests`.** Expected: PASS.

- [ ] **Step 5: Commit** with the message `Resolve and emit named patterns from grammars`.

### Task 7: Ancestor context clause (`inside` / `parent`)

**Files:**
- Modify: `Nitrogen.Runtime/SyntaxDiagnosticPattern.cs`, `Nitrogen.Runtime/Semantics/SemanticsRule.cs:65-125`,
  `Nitrogen.Runtime/Quotation/QuotePattern.cs`, `Nitrogen.Runtime/LanguageBuilder.cs`,
  `Nitrogen.Grammar/SemanticsWriter.cs`, `Nitrogen.Grammar/GrammarValidator.cs`
- Test: `Nitrogen.Tests/Workspace/MatchContextTests.cs`

**Interfaces:**
- `public sealed record SyntaxDiagnosticContext(bool ParentOnly, string Category, string Text, IReadOnlyDictionary<string, QuoteHole> Captures, QuoteHelperSource Source)`.
- `SyntaxDiagnosticPattern` gains `public SyntaxDiagnosticContext? Context { get; init; }`.
- `Language`: `internal PatternCompilation DiagnosticContextPattern(SyntaxDiagnosticContext context)`,
  cached like `DiagnosticPattern`.
- `QuotePattern`: `internal QuoteMatchResult MatchWithBudget(QuoteNode input, QuoteMatchBudget budget)`.
  It lets `QuoteMatchLimitException` propagate and returns `NQP0004` for invalid input.
- The message captures passed to `SemanticPatternCaptures` are the union of the main captures and the
  context captures. The names are disjoint, which `NGR0165` enforces at grammar compile.

- [ ] **Step 1: Write failing tests**, using the XML-like grammar from Task 6 and this input:
  ```xml
  <config><credentials><user>sa</user><group><password>a</password></group><password>b</password></credentials></config>
  ```
  - `Inside_reports_each_inner_item`: `match <[ <password ..$_>..$_</password> ]> inside <[ <credentials ..$_>..$_</credentials> ]>`
    gives two warnings, spanning each `<password>` element.
  - `Parent_requires_immediate_semantic_parent`: the `parent` form gives one warning, on `b` only.
  - `Nearest_ancestor_supplies_captures`: nest `<credentials name="outer">…<credentials name="inner">…`.
    The message `{attrs.Count}` or `{n}` reflects the inner ancestor.
  - `Broken_ancestor_is_skipped_silently`: a recovered syntax error inside `<credentials>` gives zero
    warnings and no check failure.
  - `Inside_on_root_has_no_ancestors` (Review Focus 4): no diagnostic and no error.
  - `Context_capture_collision_reports_NGR0165`.
  - `Budget_is_shared_across_ancestors`: in a hand-built `SemanticsCheck` test, a 1,000-deep chain of
    fitting ancestors with a small budget surfaces as a check failure carrying `NQP0006`.

- [ ] **Step 2: Run them and confirm they fail.**

- [ ] **Step 3: Implement.**
  - `SemanticsCheck.Match` creates one `QuoteMatchBudget` and matches the main pattern with
    `MatchWithBudget`.
  - On success, it walks `semantics.ParentOf` from the node. `parent` stops after one step.
  - Each ancestor costs one step. An ancestor whose category fails `AcceptsQuoteNode` is skipped. A
    fitting ancestor whose result is `NQP0004` is skipped. The first match wins.
  - It throws `InvalidOperationException` with `NQP0006` on limit exhaustion, as today.
  - The emitter writes `Context = new(...)` on the generated `SyntaxDiagnosticPattern`.
  - `LanguageBuilder` validates context patterns alongside diagnostic patterns.

- [ ] **Step 4: Run the new tests and the `Workspace` test folder.** Expected: PASS.

- [ ] **Step 5: Commit** with the message `Add inside/parent context clauses to structural diagnostics`.

### Task 8: Language service support for named patterns

**Files:**
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.NamedPatterns.cs`
- Modify: `Nitrogen.LanguageService/CompletionBuilder.cs`,
  `Nitrogen.LanguageService/NitrogenLanguageService.GrammarKeywords.cs`,
  `Nitrogen.LanguageService/SemanticTokenBuilder.cs`, and the definition, reference, rename and hover
  dispatch in `NitrogenLanguageService.Symbols.cs` / `NitrogenLanguageService.GrammarHelpers.cs`.
  Follow how quote helpers are wired.
- Test: `Nitrogen.Tests/LanguageService/NamedPatternServiceTests.cs`

**Interfaces:**
- Consumes `ModuleDecl.Patterns` and `GrammarAnalysis.ResolvePattern` from Tasks 5 and 6.

- [ ] **Step 1: Write failing tests.** Follow the existing quote-helper navigation tests in `Nitrogen.Tests/LanguageService`:
  - Go-to-definition from `(s: Secret)` and from `(s: ..Secret)` lands on the declaration name.
  - Find-references lists the declaration and both uses.
  - Rename updates all three.
  - Hover shows `pattern Secret : Element` and the alternative count.
  - Completion after `(s: ` offers `Secret`.
  - Keywords `pattern`, `inside` and `parent` get keyword semantic tokens only in their contextual positions.

- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the `LanguageService` tests.** Expected: PASS.
- [ ] **Step 5: Commit** with the message `Navigate, rename and complete named patterns in the editor`.

### Task 9: Documentation, XML example and full validation

**Files:**
- Modify: `docs/language-guide.md` (add a "Named patterns and tree context" section after "Sequence
  patterns and wildcards"), `examples/XmlPatterns/Xml.ngr`, `examples/XmlPatterns/sample.xml`,
  `examples/XmlPatterns/README.md`, `Nitrogen.Tests/XmlPatterns/XmlPatternExampleTests.cs`, and the
  spec status line.

- [ ] **Step 1: Write failing example tests.** In `XmlPatternExampleTests`, add:
  - `Secret_inside_credentials_reports_each_item`: one `XML1003` per nested secret in `sample.xml`.
  - `Credentials_containing_secret_reports_once`: a single `XML1002`, spanning `<credentials>`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Update the example grammar and sample, then the guide and README.** Remove the
  guide's line "Matching performs no … implicit subtree search" only where it is now false for
  descendant captures.
- [ ] **Step 4: Run the full validation.** `./build.sh` (or `dotnet build -c Release` followed by
  `dotnet test`) should finish with zero warnings and all tests passing. Record the test count in the
  spec status line.
- [ ] **Step 5: Commit** with the message `Document named patterns and tree context; extend XML example`.
