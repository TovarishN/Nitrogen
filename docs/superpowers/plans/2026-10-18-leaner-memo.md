# Leaner Memo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Halve the packrat memo's peak size on the C# benchmark without slowing the full parse.

**Architecture:** Two independent changes to `Nitrogen.Runtime`:

1. Failures that ran no prefix candidate are not memoized.
2. `MemoTable` becomes an array of 16-byte entries, without the separate stamp array, at a load factor of up to 0.75.

Measured with CSNitrogen's benchmark.

**Tech Stack:** C# / .NET 10, xUnit, BenchmarkDotNet (CSNitrogen benchmark project).

**Spec:** `docs/superpowers/specs/2026-10-18-compact-incremental-trees-design.md` (Part 2)

## Global Constraints

- Done = the memo's **peak size on the benchmark is at least halved** from 20.97 MB (1,048,576 slots), with **no regression in full-parse time** (~85 ms `NitrogenTree`).
- The build arena's size is out of scope.
- Small and medium parses keep a constant steady-state allocation. `AllocationTests.Steady_state_parse_allocates_a_constant_amount_regardless_of_size` must stay green.
- Parse results are unchanged: the same trees, diagnostics and ambiguities.
- Verify with `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`.

## Review Focus

- **A failure memoized at one minimum precedence must not answer a call at another.** The rule key already includes precedence; keep it.
- **The recovery pass's call flags must stay in the key.** Recovery tests must stay green.
- **Generations wrapping at `int.MaxValue`** (if a generation counter remains) must not resurrect stale entries. Add a test that forces the wrap.
- **The cheap-failure skip must not change the furthest-failure diagnostics.** `Expect` must still be recorded even when the failure isn't memoized. `ExpectedTests` must stay green.
- **Benchmark numbers come from the same machine, before and after,** in one sitting.

---

### Task 1: Don't memoize failures that ran no candidate

**Files:**
- Modify: `Nitrogen.Runtime/ExtensibleRules.cs` (`ParseExtensible`, `ParsePrefix`)
- Test: `Nitrogen.Tests/MemoTableTests.cs`

**Interfaces:**
- Produces: `static bool ParsePrefix(ref ParserState s, ExtensionPoint point, out bool triedCandidate)`. `triedCandidate` is false when `point.PrefixCandidates(bucket)` yielded nothing.

- [ ] **Step 1: Write the failing test** `A_failure_with_no_prefix_candidate_is_not_memoized`:
  - Parse a Calc input that calls `Expr` at a position whose first character has no prefix alternative. An example is `"x = ;"`, which is expected to fail.
  - Read `ParseContext.Cached!.Memo.Count` after the parse. If a large context left it null, use a small input.
  - Assert that no entry exists for that `(ruleKey, position)`: add `internal bool MemoTable.Contains(int ruleKey, int position)` for the test.
- [ ] **Step 2: Run** `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter MemoTableTests`. Expected: FAIL (the entry exists).
- [ ] **Step 3: Implement.** In `ParseExtensible`, write the failure entry only when `triedCandidate` is true. Keep `arena.Expect(...)` and `MemoMisses` exactly as today.
- [ ] **Step 4: Run** the full test suite. Expected: all pass.
- [ ] **Step 5: Commit** `Skip memoizing extensible failures that ran no candidate`.

### Task 2: 16-byte memo entries at load factor 0.75

**Files:**
- Modify: `Nitrogen.Runtime/MemoTable.cs`
- Test: `Nitrogen.Tests/MemoTableTests.cs`

**Interfaces:**
- Consumes/produces: the public surface of `MemoTable` is unchanged: `Count`, `Capacity`, `Clear()`, `TryGet`, `Set`. Add `internal const double MaxLoad = 0.75`.

- [ ] **Step 1: Write failing tests:**
  - `Capacity_grows_only_past_three_quarters_load`: insert 300 distinct keys into a new table, then `Assert.Equal(512, memo.Capacity)`. Today's 50% rule gives 1024.
  - `Clear_forgets_every_entry_and_keeps_capacity`: insert 1000 entries, `Clear()`, then assert `TryGet` misses all of them, `Count == 0` and the capacity is unchanged.
  - `Entries_survive_growth`: insert 5000 keys with distinct node/end values, then assert all round-trip.
- [ ] **Step 2: Run** the `MemoTableTests` filter. Expected: the capacity test FAILs.
- [ ] **Step 3: Implement** one `Entry[]` of `struct Entry { long Key; int Node; int End; }` (16 B) with linear probing:
  - **Empty-slot encoding:** a reserved key value, `long.MinValue`, which no `(ruleKey, position)` can produce because both halves are non-negative.
  - **`Clear()`:** O(1) when `Count == 0`. Otherwise it may `Array.Fill` the keys region. Keep the steady state allocation-free; measure the clear cost on the benchmark. If it costs more than 1% of `NitrogenTree`, keep a 4-byte generation inside `Entry` instead, which gives 20 B but still benefits from the 0.75 load.
  - **Growth:** when `(Count + 1) > Capacity * MaxLoad`.
- [ ] **Step 4: Run** the full test suite. Expected: all pass, including `AllocationTests`.
- [ ] **Step 5: Commit** `Pack memo entries into 16 bytes at three-quarters load`.

### Task 3: Measure and record

**Files:**
- Modify: `CSNitrogen/benchmarks/CSNitrogen.Benchmarks/README.md` (in a CSNitrogen branch, or in the isolated copy if CSNitrogen is still uncommitted; see below)
- Create: `docs/experiments/2026-10-18-leaner-memo.md`

- [ ] **Step 1: Build the benchmark against this branch** in an isolated copy, without touching the user's CSNitrogen checkout:

  ```bash
  rsync -a --exclude .git --exclude bin --exclude obj --exclude .dependencies ~/work/CSNitrogen/ "$SCRATCH/csn-perf/"
  ```

  Then run `NitrogenRoot=<this worktree> dotnet build $SCRATCH/csn-perf/benchmarks/CSNitrogen.Benchmarks -c Release`. The environment variable matters, because BenchmarkDotNet rebuilds the project itself.
- [ ] **Step 2: Record the memo's peak.** Add to the probe (or `memoryall`) the memo slot count and bytes after a benchmark parse; the oversized context is weakly held, so read it before a GC. Expected: at most 10.5 MB (half of 20.97 MB).
- [ ] **Step 3: Time it.** Run `NitrogenRoot=… dotnet …CSNitrogen.Benchmarks.dll --filter '*FullParse*'`. Expected: `NitrogenTree` mean no worse than the baseline run in the same sitting (base = `main` built the same way).
- [ ] **Step 4: Write the evidence note.** Record the entries, slots, bytes, hit rate and parse time, before and after.
- [ ] **Step 5: Commit** `Record leaner memo measurements`.
