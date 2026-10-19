# Incremental Reparse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After a small edit to the 1 MB C# benchmark file, reparse (preprocessing plus grammar) in at most 2× Roslyn's incremental reparse, about 2–3 ms. The result must be identical to a full parse of the new text.

**Architecture:**

- **Recording (incremental mode):** a parse records a reuse table of rule results keyed by `(rule key, start, call context)`, each with its node and how far the parser examined the text. Rule keys: a non-alias syntax rule uses its kind; an extensible call uses its existing memo `ruleKey`.
- **Reparse:**
  1. Map each rule call's position back to the old text.
  2. Look up the old result.
  3. If its examined range avoids every edit, push one *graft* arena node standing for the old subtree.
  4. When the tree is frozen, the freezer copies each grafted old preorder block, rebasing child indices (skipping `NoNode`) and shifting positions.
- **Typing through errors:** a reparse whose fast pass fails falls back to a full recovery parse. It keeps the last successful parse as its reuse base, so typing through an error still reuses.

**Tech Stack:** C# / .NET 10, the Nitrogen source generator, xUnit; CSNitrogen for preprocessing, integration and measurement.

**Spec:** `docs/superpowers/specs/2026-10-18-compact-incremental-trees-design.md` (Part 3, including "Design notes after compact trees")

## Global Constraints

- **Edit reparse** (benchmark edits `TypeChar`, `InsertStatement`, `AddMethod`) is at most **2× Roslyn's incremental reparse** measured in the same run (about 2–3 ms). This covers preprocessing plus grammar. CSNitrogen's syntax-policy checks are excluded and measured separately.
- **Plain parses are unchanged:** no reuse data is kept, memory stays at its current 7.44 MB live, and plain warm full-parse time is no worse than 73 ms, within 2%. `AllocationTests` stays green.
- **Incremental mode is opt-in** (`ParseIncremental`). Its extra memory over a plain parse is measured and reported as a separate `memoryall` row, not counted against the plain-parse target.
- **Anti-goal:** an incremental result must equal a full parse of the new text node for node: kind, span, flags, child structure (including null slots), trivia, skipped spans and diagnostics. When reuse safety is uncertain, do not reuse.
- **Examined length must be conservative.** Overestimating only costs reuse; underestimating is a correctness bug.
- **Only the fast pass reuses.** Template and quasiquote parsing (`Holes`), and the recovery pass, never record or reuse.
- **A previous result from another `Language` instance is rejected** (`ArgumentException`), as is a disposed one (`ObjectDisposedException`).
- **Verify** with `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`. CSNitrogen's suites are run in the throwaway copy at `scratchpad/csn-verify` (the gate hashes there are re-pinned for the copy only; never re-pin in a real CSNitrogen checkout). CSNitrogen changes go on its `compact-trees` worktree branch (`~/work/CSNitrogen-compact`). Never touch `~/work/CSNitrogen`, and never push.

## Review Focus

- **An edit just outside a node that the node's lookahead saw.** For example, `a<b>c` against `a<b>(c`, or a keyword whose following character changes (`int` vs `intx`). Reuse must be refused because the examined range covers the edit. Owned by Task 1 (examined tests) and Task 5 (oracle seeds).
- **Insertion exactly at a node's start or end boundary.** Content inserted at a node's start must not be swallowed. Content inserted at its end must reuse only when the node's examined range ends at or before the insertion. Owned by Task 3.
- **Several edits in one reparse, out of order or touching each other.** Unsorted, overlapping or out-of-range changes throw. Adjacent ones are fine. Owned by Task 3.
- **Typing through an error and back:** good, then broken (the recovery parse), then good again. The final tree equals a full parse, and the last step reuses (`Stats.ReusedNodes > 0`). Owned by Task 4.
- **An edit that adds or removes a preprocessor directive, or touches an inactive `#if` region.** CSNitrogen falls back to full preprocessing and the result still equals a full parse. Owned by Task 6.

---

### Task 1: Examined-length tracking

**Files:**
- Modify:
  - `Nitrogen.Runtime/ParserState.cs`: every text read (`Current`, `MatchLiteral`, `MatchKeyword`, `MatchToken`, `SkipTrivia`, `PeekPastTrivia`, hole checks, recovery scans) updates a high-water mark when tracking is on.
  - `Nitrogen.Runtime/BuildArena.cs`
  - `Nitrogen.Grammar/SyntaxCodeWriter.cs`: generated token matchers and the predicate helpers report the furthest character read.
  - `Nitrogen.Runtime/ExtensibleRules.cs`
- Test: create `Nitrogen.Tests/ExaminedTests.cs`.

**Interfaces:**
- Produces:
  - `internal bool BuildArena.Tracking`: off by default and for every plain parse.
  - `internal int BuildArena.Examined`: the furthest text index read, plus one, since the last mark.
  - `internal int ParserState.BeginExamined()`: saves the outer mark and resets it to `Position`; returns the saved value.
  - `internal int ParserState.EndExamined(int saved)`: returns this call's examined end, and restores the outer mark as the max of both.
  - When `Tracking` is false these do nothing, and the hot path costs one predictable branch.

- [ ] **Step 1: Write failing tests** (Calc and Shapes grammars, tracking forced on through an internal test hook `Language.ParseTracked(text, start)` that returns the arena's per-call examined ends):
  - `Literal_failure_examines_up_to_the_mismatch`
  - `Keyword_examines_the_following_character`: `int` followed by `x` reads index 3.
  - `Token_examines_the_character_that_stopped_it`
  - `Not_predicate_examines_what_it_tried`
  - `Trivia_scan_examines_up_to_the_next_token`
  - `Examined_never_precedes_node_end`: for every recorded call, examined end ≥ node end.
- [ ] **Step 2: Run** `--filter ExaminedTests`. Expected: FAIL (hook missing).
- [ ] **Step 3: Implement.** Audit every read site, and list them in the report with the conservative bound each one uses.
- [ ] **Step 4: Measure** a plain warm parse of a large Calc input before and after, in Release, best of 20. Expected: within 2%.
- [ ] **Step 5: Run** the full suite. Expected: all pass.
- [ ] **Step 6: Commit** `Track how far the parser examines the text`.

### Task 2: Record a reuse table in incremental mode

**Files:**
- Create: `Nitrogen.Runtime/ReuseTable.cs`
- Modify:
  - `Nitrogen.Runtime/Language.cs` (`ParseIncremental`)
  - `Nitrogen.Runtime/ParseResult.cs`
  - `Nitrogen.Runtime/TreeFreezer.cs` (arena→tree index map for table entries)
  - `Nitrogen.Grammar/SyntaxCodeWriter.cs` (non-alias rule entry and exit hooks)
  - `Nitrogen.Runtime/ExtensibleRules.cs`
- Test: create `Nitrogen.Tests/IncrementalTests.cs`.

**Interfaces:**
- Produces:
  - `public ParseResult Language.ParseIncremental(string text, Rule start, string? treeText = null)`. It parses like `Parse`, with tracking on, and the result carries `internal ReuseTable? Reuse`.
  - `public bool ParseResult.IsIncremental => Reuse is not null`.
  - `ParseResult` remembers its start `Rule` and `Language`.
  - `ReuseTable` holds, sorted by start, entries `(long RuleKey, int Start, int End, int ExaminedEnd, int Node)`:
    - `RuleKey` is the rule's kind for syntax rules. For extensible calls it is the memo `ruleKey` with a tag bit, so the two spaces never collide.
    - `Node` is a tree index after freeze.
    - Entries whose arena node did not survive the freeze are dropped. So are failures, which this plan never reuses.
  - Generated code: a non-alias rule calls `s.RecordRule(kind, start, savedExamined)` when it succeeds, only when `Arena.Tracking`. `ParseExtensible` records its successes the same way.
  - `ParseStats` gains `int ReusedNodes`, 0 here.

- [ ] **Step 1: Write failing tests:**
  - `Incremental_parse_equals_plain_parse`: dumps, spans and trivia are equal for Calc, Recovery and Shapes inputs.
  - `Plain_parse_keeps_no_reuse_data`: `IsIncremental` is false, and `AllocationTests` is unchanged.
  - `Reuse_table_entries_point_at_nodes_of_their_rule`: every syntax-rule entry's node has that kind, and its span starts at `Start`.
- [ ] **Step 2: Run** `--filter IncrementalTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full suite. Expected: all pass.
- [ ] **Step 5: Commit** `Record a reuse table in incremental parses`.

### Task 3: Reparse with grafted subtrees

**Files:**
- Create: `Nitrogen.Runtime/TextChange.cs`
- Modify:
  - `Nitrogen.Runtime/Language.cs` (`Reparse`)
  - `Nitrogen.Runtime/ParserState.cs` (`TryReuse`)
  - `Nitrogen.Runtime/BuildArena.cs` (graft nodes)
  - `Nitrogen.Runtime/TreeFreezer.cs` (block copy)
  - `Nitrogen.Runtime/ReuseTable.cs` (carry-over)
  - `Nitrogen.Grammar/SyntaxCodeWriter.cs` (rule entry calls `TryReuse`)
  - `Nitrogen.Runtime/ExtensibleRules.cs`
- Test: `Nitrogen.Tests/IncrementalTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct TextChange(int Start, int OldLength, int NewLength)`, in old-text coordinates.
  - `public ParseResult Language.Reparse(ParseResult previous, string text, ReadOnlySpan<TextChange> changes, string? treeText = null)`. It throws:
    - `ObjectDisposedException` when `previous` is disposed;
    - `ArgumentException` when `previous` is not incremental, comes from another `Language`, has unsorted, overlapping or out-of-range changes, or when `text.Length != old length + Σ(NewLength − OldLength)`.
  - The new result is independent of `previous`: either can be disposed first.
  - **Reuse rule.** At a rule call at new position `p`:
    1. If `p` lies inside a changed new range, there is no reuse.
    2. Otherwise `p' = p − delta`, where delta is the sum of earlier changes' `NewLength − OldLength`.
    3. Look up the entry `(key, p')` and take its examined range `[a, b) = [p', ExaminedEnd)`.
    4. A change `(s, L)` conflicts when `s < b && s + L > a`, or when `L == 0 && a < s && s < b`.
    5. With no conflict, push a graft for the old node, set `Position = p + (End − p')`, and raise the new examined mark to the mapped `b`.
  - **Freezer.** A graft copies the old block:
    - The block is the old node plus its descendants, contiguous in preorder, with their slot range.
    - The kind/flags, start + Δpos, length and childStart + Δslot columns are copied.
    - Each child slot gets + Δindex unless it equals `NoNode`.
    - Graft counts as one node in the arena's count pass and as the block size in the freeze.
  - **Carry-over.** The new `ReuseTable` holds this parse's recorded entries, plus the previous entries lying inside grafted blocks, shifted and re-indexed. So the next reparse can reuse inside them.
  - `Stats.ReusedNodes` is the number of tree nodes copied from grafts.

- [ ] **Step 1: Write failing tests:**
  - `Reparse_equals_full_parse_after_edit`: Theory over Calc, Recovery and Shapes inputs, with inserts, deletes and replaces at the start, middle and end. Compare dumps, spans, flags, trivia, skipped spans and diagnostics.
  - `Untouched_items_are_reused`: a 200-statement Calc program with one middle statement edited gives `ReusedNodes ≥ 90%` of `NodeCount`.
  - `Insert_at_node_start_is_not_swallowed`
  - `Lookahead_into_the_edit_prevents_reuse`
  - `Bad_changes_throw`
  - `Previous_from_another_language_throws`
  - `Disposed_previous_throws`
  - `Results_are_independent`
  - `Reparse_of_a_reparse_reuses_inside_grafts`
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full suite. Expected: all pass.
- [ ] **Step 5: Commit** `Reparse by grafting untouched subtrees`.

### Task 4: Typing through errors

**Files:**
- Modify: `Nitrogen.Runtime/Language.cs`, `Nitrogen.Runtime/ParseResult.cs`, `Nitrogen.Runtime/ReuseTable.cs`
- Test: `Nitrogen.Tests/IncrementalTests.cs`

**Interfaces:**
- Produces:
  - When a reparse's fast pass fails, the recovery pass runs as a full parse. That is a plain recovery parse: no tracking, no reuse.
  - The result's `Reuse` keeps the **last successful base**: its tree, its table and the composed change list from that base to this text. The base tree is kept alive by the new result, and released when the result is disposed. It is reference-counted or copied (builder's call; document it).
  - A later `Reparse` composes its changes onto that list and reuses from the base.
  - `ParseResult.IsIncremental` stays true for such results.

- [ ] **Step 1: Write the failing test** `Typing_through_an_error_still_reuses`:
  - Go from good, to a missing `;` (recovered), to good again.
  - The last result equals a full parse and has `ReusedNodes > 0`.
  - Dispose the results in every order without exceptions.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full suite. Expected: all pass.
- [ ] **Step 5: Commit** `Keep the last good base while typing through errors`.

### Task 5: Randomized oracle

**Files:**
- Create: `Nitrogen.Tests/IncrementalOracleTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4 APIs.
- Produces: `static void AssertSameTree(ParseResult incremental, ParseResult full)`, which compares kind, span, flags, children with null slots, trivia, skipped spans and diagnostic code, span and message. Task 6 reuses it, so make it `internal` in a test helper file.

- [ ] **Step 1: Write the oracle:**
  - For each test grammar with a sizeable generated input (Calc, Recovery, Shapes, Mini, Lists), apply 200 random edit sequences (fixed seeds 1–200) of 1–5 single or multi edits.
  - Edits are inserts, deletes and replaces of 0–8 characters, biased toward token boundaries, inside tokens, comments and near the end.
  - Compare `Reparse` against `Parse` after each step.
  - Add an opt-in long run, `NITROGEN_ORACLE_SEEDS=5000`.
- [ ] **Step 2: Run.** Every discrepancy is a bug in Tasks 1–4. Fix it at its cause in the owning code, with a focused regression test named after the seed, and record each in the report.
- [ ] **Step 3: Run** the full suite. Expected: all pass, and the oracle takes no more than 30 s on the default seeds.
- [ ] **Step 4: Commit** `Add the incremental reparse oracle`.

### Task 6: CSNitrogen incremental preprocessing and API

**Files** (CSNitrogen worktree `~/work/CSNitrogen-compact`, branch `compact-trees`):
- Modify:
  - `src/CSNitrogen/Preprocessing/ConditionalPreprocessor.cs` (incremental path)
  - `src/CSNitrogen/Preprocessing/PreprocessResult.cs`
  - `src/CSNitrogen/Parsing/CSharpParser.cs`
  - `src/CSNitrogen/Parsing/ParseDocument.cs`
- Test: `tests/CSNitrogen.Tests/Incremental/IncrementalParseTests.cs` (create).

**Interfaces:**
- Produces:
  - `public static PreprocessResult ConditionalPreprocessor.Reprocess(PreprocessResult previous, PreprocessRequest request, ReadOnlySpan<Nitrogen.TextChange> changes)`. It takes the fast path when no change touches a directive line or an inactive span, and no change's new text contains `#` or a line break. The fast path patches the masked text with the same change, and shifts the directives, inactive spans and line mappings. Otherwise it calls `Process`.
  - `public static ParseDocument CSharpParser.ParseIncremental(ParseRequest request)`.
  - `public static ParseDocument CSharpParser.Reparse(ParseDocument previous, ParseRequest request, ReadOnlySpan<Nitrogen.TextChange> changes)`. It runs preprocessing (incremental), then `Language.Reparse` with `treeText: request.Source`, then the policies, exactly as `Parse`. It throws when `previous` is not incremental or the profile differs.
- Note: the policies are gate-pinned files; `CSharpParser.cs` and the preprocessor are too. After changing them, refresh the throwaway copy `scratchpad/csn-verify` (rsync from the worktree, then recompute every `reviewInputs` hash in order: csharp3 inputs → `CSharp3CoverageContract.ApprovedLedgerSha256` → csharp2 inputs → `CSharp2CoverageContract.ApprovedLedgerSha256`). Run the suites there. Never re-pin in the worktree.

- [ ] **Step 1: Write failing tests:**
  - `Reparse_equals_full_parse_on_csharp_corpus`: the Task 5 oracle helper over CSNitrogen's corpus files and the benchmark's generated C# 1 file (300 namespaces), with 100 fixed seeds, including directive edits.
  - `Directive_edit_falls_back_to_full_preprocessing`
  - `Edit_inside_inactive_region_falls_back`
- [ ] **Step 2: Run** in the throwaway copy. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `CSNitrogen.Tests` and `CSNitrogen.Oracle.Tests` in the throwaway copy. Expected: all pass.
- [ ] **Step 5: Commit** on `compact-trees`: `Add incremental preprocessing and reparse`.

### Task 7: Measure and record

**Files:**
- Modify: the uncommitted benchmark project in `~/work/CSNitrogen-compact/benchmarks/CSNitrogen.Benchmarks`. `EditReparseBenchmarks` gains:
  - `NitrogenIncremental`: `Reprocess` + `Language.Reparse` from an incremental previous result.
  - `CSNitrogenIncrementalFullApi`: `CSharpParser.Reparse`, with policies.

  `memoryall` gains a `nitrogen-incremental` row.
- Create: `docs/experiments/2026-10-19-incremental-reparse.md` (Nitrogen).

- [ ] **Step 1: Run** `validate`, `memoryall 3` and `--filter '*'` (`NitrogenRoot` env var, run from the benchmark directory), with the plain baseline from `c735e9e` in the same sitting.
- [ ] **Step 2: Check the targets:**
  - `NitrogenIncremental` at most 2× `RoslynIncremental` for each edit;
  - the plain `NitrogenTree` within 2% of baseline;
  - the plain live heap unchanged.
  - Report the incremental mode's extra memory per node.
- [ ] **Step 3: Write the evidence note.** If a target is missed, give the measured cause; for example, the time is dominated by the block copy. Revisit relative widths only with numbers.
- [ ] **Step 4: Commit** `Record incremental reparse measurements` (Nitrogen).
