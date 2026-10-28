# Recovery and incremental parsing parity

Status: investigation and first lexical recovery repair, 2026-10-28. Parity is
not established by the existing recovery or incremental correctness tests.

## First measured corpus

`Nitrogen.Tests/RecoveryQualityTests.cs` now freezes 413 deterministic mutations
over six accepted baselines: two local DSL fixtures, two original C# declaration
fixtures, and two declarations from Nitride's recovery manifest (IDs
`csharp4:frozen:grammar-0` and `csharp4:frozen:grammar-2`, read 2026-10-28).
The copied declarations are embedded in the test; CI needs no Nitride checkout.
`RecoveryCSharp.ngr` supports only classes containing simple fields. It is a
measurement fixture, not a C# grammar or an admission of full C# coverage.

The pinned oracle is the test project's existing Roslyn 4.12.0 package with
C# 4 options; Nitride's published 5.0.0 results are not directly interchangeable.
The saved `recovery-quality-baseline.json` includes family scores and every lost
Nitrogen item with replayable edit coordinates. Offsets are UTF-16 throughout.
The supplementary Unicode baseline checks token positions after a surrogate pair;
no imported UTF-8 offset translation is claimed at this stage.

Each mutation checks repeat-run tree/diagnostic determinism, exact coverage by
leaf spans and trivia, explicit accounting of non-comment/non-whitespace gaps as
skipped input, valid-input recovery equivalence, and incremental/full equality
through damage and correction. Preservation matches kind, relative structure,
null slots, flags, lexical spelling and mapped source span. C# preservation uses
the same original field intervals for both parsers. Edits touching an item's
boundary are excluded conservatively. Diagnostics are whole-file syntax counts,
not severity-weighted or normalized primary-error counts.

Initial C# subset preservation:

| Damage | Nitrogen | Roslyn | Eligible fields |
|---|---:|---:|---:|
| Delete | 80 | 74 | 90 |
| Insert | 81 | 74 | 90 |
| Duplicate | 71 | 82 | 90 |
| Swap | 51 | 68 | 74 |
| Truncate | 41 | 41 | 41 |
| Missing closer | 11 | 12 | 12 |

The test enforces per-family preservation floors, diagnostic ceilings, stable
case/eligibility counts, and frozen Roslyn comparator results. Changes to the
corpus or oracle require deliberate baseline review. These are regression gates
for current behavior, not parity gates. Wider grammars, seed-driven multi-error
sequences, latency/allocation/work budgets, semantic preservation and running
Nitride itself on these inputs remain outstanding. The largest initial C# gap is
swapped-token locality, followed by duplicated tokens.

Run with:

```powershell
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj -m:1 --filter FullyQualifiedName~RecoveryQualityTests
```

Set `NITROGEN_RECOVERY_REPORT` to an absolute output JSON path to export current
measurements, or `NITROGEN_RECOVERY_TRACE` to record the currently running edit.
The report export does not update the baseline copied into the test output for
that run. Review any baseline replacement rather than accepting new scores
automatically.

## Current Nitrogen behavior

Failed-pass seeding now reuses successful extensible-rule recognition inside
the same parse when every module declares the generated seeding contract.
Repair flags and precedence remain part of memo identity; failures are rerun.
This does not change the cross-edit limitations described below. See the
[optimization evidence](nitride-recovery-optimizations.md) for the differential
oracle and scoped measurements.

`Language.Parse` tries a strict pass, then grammar-derived committed recovery.
`ParserState.Recover` inserts missing elements or skips to FIRST/FOLLOW-derived
synchronization points. List recovery can resume at another item where the
generator supplies a repair site. The runtime retains missing nodes and skipped
source spans and caps repairs at `BuildArena.MaxRepairs`.

`Language.ParseIncremental` and `Language.Reparse` already implement rule reuse
using observed text reads. Recovery does not record reuse data. A reparse that
fails the strict pass repeats that pass without reuse to reconstruct the same
first diagnostic, then runs full recovery. It retains the last successful tree
for reuse after the text becomes valid. Initially malformed documents have no
successful baseline to retain. Thus incremental correctness across an error is
already tested, but efficient parsing while errors remain is a separate gap.

The current served-document constructor in
`Nitrogen.LanguageService/NitrogenLanguageService.cs` and document analysis in
`Nitrogen.Workspace/DocumentAnalysisSession.cs` use `Language.Parse`, rather than
the runtime incremental API. Ranged LSP text synchronization is not itself
incremental syntax parsing. Editor integration needs its own change and tests.

The first repair makes the lexical fallback skip backslash-escaped quotes in
both damaged text and the scan that reconstructs enclosing brackets. Before the
repair, a semicolon inside a quoted unit could become a synchronization point,
and a brace inside it could corrupt the enclosing scope. This fallback is not a
replacement for authored lexical rules: raw, verbatim, interpolated and custom
delimiters need grammar-aware handling.

## Reference contracts

Nitride's local `docs/stage14-evidence.md` and
`fixtures/recovery/manifest.json` describe its grammar-derived recovery quality
corpus. Its bounded search ranks insert/delete repairs within explicit cost,
state and input budgets and reports fallback. Its comparison measures preserved
syntax against each parser's undamaged tree, including paired C# intervals.
Aggregate admission is not per-case equivalence or superiority.

Roslyn's [parser guidance](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Design/Parser.md)
requires incremental and full parsing to agree and explains why ambient context
and diagnostics restrict reuse. Its
[Blender implementation](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Parser/Blender.Reader.cs)
rejects changed, zero-width, incomplete and diagnostic-bearing nodes. Recovery
does not automatically make those nodes reusable.

## Delivery sequence and admission gates

See [Nitride's applied optimization path](nitride-recovery-optimizations.md) for
the recent stage26–44 source/history review. In particular, seed recovery from
failed-pass recognition, avoid redundant fallback, and preserve generated
recognition when designing bounded search; arena optimizations need profiling
against Nitrogen's different representation.

1. **Establish comparable quality evidence.** Bring applicable Nitride fixtures
   into a self-contained Nitrogen test corpus with provenance. Include CSharp4,
   grammar authoring, XML, composed grammars and small focused recovery grammars.
   Pin the Roslyn version and C# language version. Translate UTF-8 reference
   offsets to Nitrogen/Roslyn UTF-16 explicitly, including supplementary scalars.
   Record baseline rejection and inapplicable edits rather than silently
   excluding them. Do not depend on a sibling checkout in CI.
2. **Measure damage locally.** Apply deterministic token deletion, insertion,
   duplication, swapping, truncation and missing-closer edits, plus seeded
   multiple edits. Check exact source coverage, deterministic trees/diagnostics,
   valid-input equivalence and preserved untouched sibling fingerprints.
   Compare structural fingerprints, lexical text and missing state; node counts
   alone cannot establish preservation. Record cascade counts and individually
   worse cases, not just an aggregate score.
3. **Improve recovery against failing cases.** Address root-list and leading
   damage, lexical boundaries, nested/mismatched delimiters and ambiguous
   continuations. Evaluate bounded minimum-cost local repair against the current
   greedy synchronization strategy. Preserve composition-aware commitment and
   strict predicate/lexical behavior. Expose search exhaustion and fallback;
   a repair-count cap alone does not bound search work or guarantee locality.
4. **Reuse healthy regions while input remains malformed.** Initially exclude
   repaired nodes and ancestors with damaged descendants. Record the complete
   read dependencies of reusable healthy regions, including failed alternatives,
   lookahead, trivia and context used to choose repairs. Recovery currently also
   depends on call flags and enclosing brackets. Extend reuse keys and edit
   invalidation before allowing reuse across those contexts. Preserve diagnostics,
   skipped spans, tree ownership and disposal independence. Prove equality to a
   full parse after every edit, starting with both valid and malformed input.
5. **Measure editor cost.** Report strict/full recovery/incremental times,
   allocations, retained bytes, reused nodes and deterministic work counters for
   single and dense damage across increasing file sizes. Exercise consecutive
   malformed edits, correction, undo and redo. Verify workspace/LSP paths use the
   incremental API. Performance admission must cover error states, not only
   the first successful parse after correction.

Run focused regressions after each runtime/generator change, followed by the
solution build and full test project. Publish the corpus results before claiming
Nitride/Roslyn recovery parity or incremental editor latency parity.
