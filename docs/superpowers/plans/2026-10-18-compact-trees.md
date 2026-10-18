# Compact Trees Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A plain parse of the 1 MB C# benchmark file holds at most 1.5× Roslyn's live memory (about 7.4 MB), with no full-parse slowdown.

**Architecture:**
- **Fewer nodes:**
  - Absent optionals and empty lists become null child slots.
  - One-item lists hold their item directly.
  - Choice rules of single references or literals are aliases with no node of their own.
  - Grammar-marked `transparent` extensible alternatives produce no node.
- **Smaller rows:** a dense 16-bit kind per language, trivia computed on demand, and an optional tree text separate from the parsed text, so CSNitrogen can drop its masked copy.

Positions stay absolute.

**Tech Stack:** C# / .NET 10, the Nitrogen source generator, xUnit; CSNitrogen (separate repository) for migration and measurement.

**Spec:** `docs/superpowers/specs/2026-10-18-compact-incremental-trees-design.md` (Part 1)

## Global Constraints

- **Memory:** live heap while holding a plain-parse tree is at most **1.5× Roslyn's** (Roslyn: 4.95 MB, so the target is about 7.4 MB). Preprocessed text that Nitrogen creates counts.
- **Speed:** warm full-parse time is no worse than today (~85 ms `NitrogenTree`). `AllocationTests` stays green.
- **`Span(n)` stays constant-time.** `Child(n, k)` is constant-time for fixed k and for sequential enumeration.
- **Keyword and punctuation `Literal` leaves stay.**
- **APIs and dumps may change.** Every changed test expectation must be explained by one of the four shape rules in Tasks 1–4; no other shape change is allowed.
- **Verify** with `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`. CSNitrogen's `CSNitrogen.Tests` and `CSNitrogen.Oracle.Tests` must pass against this runtime, built with `-p:NitrogenRoot=<worktree>` or the `NitrogenRoot` environment variable.
- **Don't touch the user's CSNitrogen checkout,** which has uncommitted work. Do CSNitrogen changes on a new CSNitrogen worktree branch, and leave its `docs/coverage/dependencies.json` re-pin to the owner.

## Review Focus

- **Templates and quasiquotes:** a pattern written against a list or an optional must still match trees whose list collapsed to one item or whose optional is a null slot. Add a `QuotePatternTests` case per shape in Tasks 1 and 2.
- **Recovery:** `PushEmpty` and `PushEmptyList` in the recovery pass, `Missing` nodes and `Skipped` flags still produce identical dumps. `RecoveryParsingTests` and `RecoverySoundnessTests` cover this; add a case where the recovered element is an absent optional.
- **Language service:** the formatter, semantic tokens and outline walk children and must skip null slots instead of throwing. Add a `Formatter` test over a document with an absent optional.
- **Binding and semantics** read declarations by positional child. A rule with an optional name must bind the same symbols. Add a `FileBinding` test.
- **Typed views on the "standing-in" node:** after an alias or transparent alternative disappears, `SyntaxView.TryCast<TAlternative>` on the extension point's child must still work, and casting to a removed wrapper's view type must fail cleanly. Add a `ViewTests` case.

---

### Task 1: Null slots for absent optionals and empty lists

**Files:**
- Modify:
  - `Nitrogen.Runtime/ParserState.cs` (`PushEmpty`, `PushEmptyList`)
  - `Nitrogen.Runtime/BuildArena.cs`
  - `Nitrogen.Runtime/TreeFreezer.cs`
  - `Nitrogen.Runtime/SyntaxTree.cs`
  - `Nitrogen.Runtime/SyntaxList.cs`
  - `Nitrogen.Runtime/Views.cs`
  - `Nitrogen.Runtime/SyntaxDumper.cs`
- Audit and fix every reader that the spec's "Affected code" list names.
- Test:
  - Create `Nitrogen.Tests/Grammars/Shapes.ngr` and register it as `<AdditionalFiles Include="Grammars\Shapes.ngr" Namespace="Nitrogen.Tests.Shapes" />`.
  - Create `Nitrogen.Tests/TreeShapeTests.cs`.

**Interfaces:**
- Produces:
  - `public const int SyntaxTree.NoNode = -1`. `Child(n, k)` returns it for a null slot. `ChildCount` still counts null slots.
  - The child stack carries `NoNode` for null children; every arena reader of child entries skips it.
  - `Optional<T>.HasValue => index != SyntaxTree.NoNode`.
  - `SyntaxNode.IsNull` is true for a `NoNode` index too.
  - The dump of a null slot is `_`, the same text as today's Empty.
- `SyntaxKinds.Empty` stays defined but is never emitted.

- [ ] **Step 1: Write the grammar.** `Shapes.ngr`:

  ```
  syntax module Shapes
  {
    token Name = ['a'..'z']+ except "decl" except "int" except "public" except "static";
    syntax File = Decls:Decl*;
    syntax Decl = Mods:Mod* "decl" Name:Name Type:(":" TypeName)? "{" Items:(Name; ",")* "}";
    syntax Mod = "public" / "static";
    syntax TypeName = "int" / Name;
  }
  ```

- [ ] **Step 2: Write the failing test** `Absent_optional_and_empty_list_are_null_slots`:
  - Parse `"decl a { }"`.
  - The `Decl` node has the same `ChildCount` as today (positions stable).
  - `tree.Child(decl, <Mods slot>) == SyntaxTree.NoNode`, `Child(decl, <Type slot>) == NoNode` and `Child(decl, <Items slot>) == NoNode`.
  - `tree.NodeCount` is today's count minus 3.
  - The typed view's `Type.HasValue` is false, `Mods.Count == 0` and `Items.Count == 0`.
- [ ] **Step 3: Run** `--filter TreeShapeTests`. Expected: FAIL.
- [ ] **Step 4: Implement.** `PushEmpty`/`PushEmptyList` push `NoNode`. `TreeFreezer` writes `NoNode` into the slot and doesn't traverse it. `SyntaxList` and `SeparatedList` give `Count == 0` for `NoNode`. `SyntaxDumper` prints `_`. Fix each audited reader to skip `NoNode`.
- [ ] **Step 5: Add the Review Focus tests** for recovery, quotation, formatter and binding with absent optionals.
- [ ] **Step 6: Run** the full suite. Expected: all pass, with updated dump expectations only where an `Empty` leaf or empty list became `_`.
- [ ] **Step 7: Commit** `Represent absent optionals and empty lists as null child slots`.

### Task 2: One-item lists hold their item directly

**Files:**
- Modify:
  - `Nitrogen.Grammar/SyntaxCodeWriter.cs` (list helpers)
  - `Nitrogen.Runtime/ParserState.cs`
  - `Nitrogen.Runtime/SyntaxList.cs`
  - quotation list handling (`ParserState.Quotation.cs`, `Quotation/*`)
- Test: `Nitrogen.Tests/TreeShapeTests.cs`

**Interfaces:**
- Produces:
  - `public readonly int ParserState.CloseList(bool collapseSingle)`. With exactly one child and `collapseSingle`, it pops the frame and leaves the child as the result.
  - The generator passes `collapseSingle: false` when the item expression is itself a repetition or separated list, or an alias that can resolve to one.
  - `SyntaxList<T>` and `SeparatedList<T>` built on a non-`List` node read it as one item with zero separators.
  - `CloseTemplateList` collapses identically. Templates must produce the same shape as ordinary parses.

- [ ] **Step 1: Write the failing test** `One_item_list_is_its_item`:
  - Parse `"public decl a { b }"`.
  - The `Mods` slot holds a node of the `Mod` alias's leaf kind (a `Literal` "public").
  - The `Items` slot holds a `Name` node.
  - Typed views give `Mods.Count == 1`, `Items.Count == 1` and `Items.SeparatorCount == 0`.
  - Parsing `"decl a { b, c }"` still gives a `List` node with 3 children.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces.
- [ ] **Step 4: Add a `QuotePatternTests` case:** a list hole pattern matches a one-item list and a two-item list.
- [ ] **Step 5: Run** the full suite. Expected: all pass; changed dumps only lose `(List x)` → `x`.
- [ ] **Step 6: Commit** `Store one-item lists as their item`.

### Task 3: Choices of single literals and references are aliases

**Files:**
- Modify:
  - `Nitrogen.Grammar/EmitModel.cs` (`IsAlias`)
  - `Nitrogen.Grammar/ViewWriter.cs`
  - `Nitrogen.Grammar/QuotationWriter.cs`
  - `Nitrogen.Grammar/SemanticsWriter.cs` (wherever `IsAlias` gates behavior)
  - `docs/language-guide.md`
- Test: `Nitrogen.Tests/TreeShapeTests.cs`; grammar tests under `Nitrogen.Tests/Grammar/`

**Interfaces:**
- Produces: `EmitModel.IsAlias(rule)` is true when the body is a `ReferenceExpr`, `LiteralExpr`, or a `ChoiceExpr` whose every alternative is a `ReferenceExpr` or `LiteralExpr`. An alias whose alternatives are all literals or token references gets a `Token` view; otherwise it gets `SyntaxNode`, as today. A rule with semantics or clauses stays a non-alias, as `GrammarValidator` already enforces for aliases; check it.

- [ ] **Step 1: Write the failing test** `Leaf_choice_rule_has_no_node`. In `"static decl a : int { }"`:
  - the `Mods` slot is a `Literal` "static";
  - the `Type` group's second child is a `Literal` "int";
  - no node of kind `Shapes.Mod` or `Shapes.TypeName` exists in the tree.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the `IsAlias` widening and the view-type choice, and document "alias rules" in `docs/language-guide.md`.
- [ ] **Step 4: Run** the full suite. Expected: all pass; changed dumps only lose leaf-choice wrapper nodes.
- [ ] **Step 5: Commit** `Treat choices of single literals and references as aliases`.

### Task 4: `transparent` extensible alternatives

**Files:**
- Modify:
  - `Nitrogen.Grammar/GrammarParser.cs` (`ParseAlternative`)
  - `Nitrogen.Grammar/GrammarModel.cs` (`Alternative`)
  - `Nitrogen.Grammar/GrammarValidator.cs`
  - `Nitrogen.Grammar/SyntaxCodeWriter.cs`
  - `Nitrogen.Grammar/ViewWriter.cs`
  - `Nitrogen.Runtime/ExtensibleRules.cs`
  - `Nitrogen.Runtime/ExtensionPoint.cs` / `Extensions.cs` (prefix extension record)
  - `Nitrogen.Ngr/Nitrogen.ngr` (the grammar-of-grammars, for the editor)
  - `docs/language-guide.md`
- Test: `Nitrogen.Tests/TreeShapeTests.cs`; extend `Shapes.ngr`.

**Interfaces:**
- Produces:
  - **Syntax:** `| transparent Name = <single element>`. `Alternative` gains `bool IsTransparent`.
  - **Validation:**
    - The body must yield exactly one node on every path: a single element, labeled or not, or a choice of single elements.
    - It must be a prefix alternative.
    - No precedence, clauses or semantics.
    - Each violation is a new `GrammarCodes` diagnostic with a clear message.
  - **Runtime:** `PrefixExtension` gains `bool Transparent`. `ParsePrefix` calls `s.CloseTransparent()`, which pops the frame and leaves the single child. Candidate comparison is unchanged.
  - **Views:** the alternative gets no view type. The extension point's existing `SyntaxNode`-based access stays.

- [ ] **Step 1: Extend the grammar.** Add to `Shapes.ngr`: `extensible syntax Stmt { | transparent Plain = Body:(Decl / ";") }`, plus `syntax Block = "{" Stmts:Stmt* "}"`.
- [ ] **Step 2: Write failing tests:**
  - `Transparent_alternative_has_no_node`: parsing a `Block` with `decl a { }` and `;` yields `Stmts` children of kinds `Decl` and `Literal`, and no `Plain` kind.
  - `Transparent_alternative_must_yield_one_node`: a grammar with `| transparent Bad = "a" "b"` reports the new diagnostic code.
- [ ] **Step 3: Run.** Expected: FAIL.
- [ ] **Step 4: Implement** as in Interfaces.
- [ ] **Step 5: Add the `ViewTests` case** from Review Focus.
- [ ] **Step 6: Run** the full suite. Expected: all pass.
- [ ] **Step 7: Commit** `Add transparent extensible alternatives`.

### Task 5: Dense 16-bit kinds and on-demand trivia

**Files:**
- Modify:
  - `Nitrogen.Runtime/SyntaxTree.cs`
  - `Nitrogen.Runtime/TreeFreezer.cs`
  - `Nitrogen.Runtime/Language.cs` (kind table)
- Test: `Nitrogen.Tests/TreeFreezerTests.cs`, `Nitrogen.Tests/LargeTreeTests.cs`

**Interfaces:**
- Produces:
  - `Language` owns `internal ushort DenseKind(int kind)` and `internal int KindOf(ushort dense)`:
    - Per-module `ushort[]` tables indexed by local kind, grown under a lock on a miss.
    - Reads are lock-free.
    - Built-in kinds (module 0) are pre-assigned.
    - It throws `InvalidOperationException` past 65,535 distinct kinds.
    - Trees without a `Language` (hand-built in tests) use a static table for module 0 and module 99 probes, or keep an `int[]` fallback path. Builder's call; the tests must cover both.
  - `SyntaxTree` stores `ushort[] _kind`. `Kind(n)` returns the full `int` kind, so the API is unchanged.
  - `SyntaxTree.Trivia` is built on first access, like `Parent`; benign race, same pattern.
  - `_trivia` is no longer filled by the freezer. `SkippedSpans` stay stored.

- [ ] **Step 1: Write failing tests:**
  - `Kinds_round_trip_through_dense_ids`: every node of a Calc parse has `Kind(n)` equal to the kind before this change. Compare against a dump.
  - `Trivia_is_built_on_first_access`: a reflection check that `_trivia` is null right after the parse; then `Trivia` equals the expected gaps (reuse `Freeze_records_leading_and_trailing_trivia`'s values).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces. Keep the pooled and exact-size paths from `perf/parse-memory`.
- [ ] **Step 4: Run** the full suite. Expected: all pass, including `AllocationTests`. Trivia isn't built in the steady-state loop.
- [ ] **Step 5: Commit** `Store dense 16-bit kinds and build trivia on demand`.

### Task 6: A tree text separate from the parsed text

**Files:**
- Modify: `Nitrogen.Runtime/Language.cs`, `Nitrogen.Runtime/TreeFreezer.cs`
- Test: `Nitrogen.Tests/CalcParsingTests.cs`

**Interfaces:**
- Produces: `public ParseResult Parse(string text, Rule start, string treeText)`.
  - It parses `text`, and the resulting `SyntaxTree.Text` is `treeText`.
  - It throws `ArgumentException` unless `treeText.Length == text.Length`.
  - The caller guarantees every token's characters are equal in both. CSNitrogen masks only directives and inactive regions, which are trivia.

- [ ] **Step 1: Write the failing test** `Tree_text_can_differ_from_parsed_text`:
  - Parse `"x = 1;"` with `treeText: "x = 1;"` and check `ReferenceEquals(result.Tree.Text, treeText)`.
  - Assert that a length mismatch throws.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full suite. Expected: all pass.
- [ ] **Step 5: Commit** `Let a parse keep a different tree text of equal length`.

### Task 7: Migrate CSNitrogen and measure

**Files (in a new CSNitrogen worktree on branch `compact-trees`):**
- Modify:
  - `src/CSNitrogen.Syntax/Grammars/*.ngr`: mark pass-through alternatives `transparent`. Candidates: `StandardStatement`, `StandardEmbeddedStatement`, `StandardModifier`, `StandardClassMember` and similar `Standard*` alternatives. Keep `NameExpression`, `LiteralExpression` and `BuiltinTypeName` unless a search of `src/CSNitrogen/**` shows nothing distinguishes them from their child.
  - `src/CSNitrogen/Parsing/*.cs`: every positional child access, `Empty`/`List` kind check and view type that changed. `CSharpParser.Parse` passes `request.Source` as `treeText`. Audit every `Tree.Text` / `MaskedText` reader for code that relied on the masked buffer.
  - `benchmarks/CSNitrogen.Benchmarks/*`: the probe prints `TreeNodes`; README results.

- [ ] **Step 1: Create the worktree** from CSNitrogen's committed `HEAD`. Copy the benchmark project in if it isn't committed yet. Build with `NitrogenRoot=<Nitrogen worktree>`.
- [ ] **Step 2: Mark the alternatives and fix compile errors and test failures.** Run `dotnet test tests/CSNitrogen.Tests` and `dotnet test tests/CSNitrogen.Oracle.Tests`. Expected: all pass. Oracle span comparisons must be unchanged; only internal shapes moved.
- [ ] **Step 3: Measure** `memoryall 3`, `coldall 10` and `--filter '*FullParse*'` against a `main` baseline built the same way in the same sitting. Expected:
  - `nitrogen-tree` "live w/ tree" is at most 7.4 MB;
  - `NitrogenTree` mean is at most the baseline;
  - node count is about 400k or fewer.
- [ ] **Step 4: Write it up.** If the memory target is missed, record the measured cause and per-node bytes in `docs/experiments/2026-10-18-compact-trees.md` (Nitrogen) rather than weakening anything.
- [ ] **Step 5: Commit** in both repositories: `Mark pass-through alternatives transparent and adopt compact trees` (CSNitrogen) and `Record compact tree measurements` (Nitrogen).
