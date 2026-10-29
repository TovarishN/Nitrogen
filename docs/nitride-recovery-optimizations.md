# Nitride's applied recovery optimization path

Inspected local Nitride main history through `62f3c0e` on 2026-10-28. This is a
source/history review; its published timings below were not rerun here.

## First applied Nitrogen optimization

Generated recovery `LookaheadSet`s now carry a conservative `AsciiSet` for each
token matcher, using the same grammar FIRST-character analysis as extension
dispatch. A mismatching character skips the callback. Unknown starts remain
unrestricted; non-ASCII starts use the analysis's conservative non-ASCII bit.
EOF handling is unchanged. This is the directly applicable portion of Nitride's
stages 36/44; Nitrogen already uses generated end-only token matchers here.

The original four-argument runtime constructor remains available for existing
generated and handwritten parsers. The new overload validates filter alignment.
Generated snapshots and the 413-mutation recovery corpus verify unchanged
recovery output, including incremental/full equality through error correction.

A Release microbenchmark in `RecoveryProbeFilterTests` reduced callback calls
from 600,000 to 100,000 on mixed numeric/non-numeric probes. One informational
local run measured 19.24 ms to 9.36 ms. This isolated measurement demonstrates
eliminated probe work, not a whole-file speedup or comparison with Roslyn.

Failed-pass seeding requires every composed module to declare
`SupportsRecoverySeeding`. The generator emits this contract. Handwritten and
older generated modules default to false, preserving their original behavior
even if their parsers inspect recovery mode. This gate includes modules adding
alternatives to another module's extension points.

Seeding retains successful extensible-rule memo results from the strict attempt,
along with their arena nodes and child slots. The memo keys still
isolate precedence and recovery flags: a repair-enabled recovery call cannot hit
a strict entry. Failures are discarded. A strict forest containing incremental
grafts is excluded; failed reparses reconstruct their diagnostic pass before
seeding. No additional tracking is added to successful strict parsing.

A reusable second memo table filters successes without retaining failures. The
combined capacity of both tables is subject to the original thread-retention
budget. Retained strict nodes may include discarded speculation; compaction of
the final result still emits only reachable syntax. This is same-input reuse
inside one parse, not reuse across malformed edits or a general syntax-rule cache.

The seeded path is checked against an explicit unseeded test hook, including
2,000 random-edit seeds for each of seven grammar cases and every-position
insertions in a composed ambiguous grammar. Four focused errors reduce recovery
memo misses from 15 to 8, 8 to 4, 11 to 6, and 13 to 6. A 26,005-unit late-error
Calc input measured 6.99 ms unseeded versus 4.48 ms seeded (six alternating warm
Release runs, medians). This is local workload evidence, not a Roslyn comparison
or a guarantee for dense errors, other grammars, or cold processes.

Nitrogen also now avoids the diagnostic-reconstruction strict pass when a failed
incremental attempt created no grafts. No skipped rule bodies means its first
attempt already collected the complete expectations. Grafts from speculative
attempts still force a conservative retry even if backtracked. A parser-call
regression verifies two passes (strict plus recovery), rather than three, for a
failed incremental parse with no reuse. The recovery/incremental oracle continues
to check the cases that do reuse subtrees and still require reconstruction.

## Applied sequence

| Stage / commit | Applied change | Transfer to Nitrogen |
|---|---|---|
| 26 / `f4b0473` | Seed recovery with recognition facts from the failed strict attempt; avoid a second interpreted strict attempt. | High priority: preserve healthy strict-pass results instead of clearing all work before recovery. Requires dependency/context-safe reuse, not sharing strict and recovery memo keys indiscriminately. |
| 28 / `6ae9fa9` | Recognize healthy code after the first error with generated parsers. | Nitrogen already generates parsers; preserve that advantage if adding a repair-search engine. Do not interpret every probe. |
| 34 / `f48e180` | Bound prefix reopening and reduce scans per failure. | Instrument repair work, not just repair count, before extending search. |
| 35 / `0cd1f41` | Dense evacuation tables, batched seed-fact merge, one coverage reader, cheaper private recovery-key hashing. | Profile tree freezing and publication separately. Nitrogen's flat arrays differ from Nitride's shared chunk arena, so do not port arena mechanics directly. |
| 36 / `e14512d` | Conservative first-character filters for lexical probes; epoch-stamped visited table for start tests. | Reuse grammar start sets to reject impossible probes. Unknown/nullable starts must remain eligible. |
| 37 / `1bd4a4c` | Retain shared results when the only limit is local search exhaustion; avoid rerunning full flat recovery. | Distinguish local repair exhaustion from global resource failure. Keep diagnostics and explicit fallback semantics. |
| 38 / `22e34a9` | Consume generated probe failures directly, including failures not retained because the cache cap was reached. | Cache/return negative recognition results too; avoid rerunning a probe just because no success node exists. |
| 39 / `39ab922` | Same negative-result optimization for predicates; scan/cache token ends without building nodes or handles. | Give repair search a lightweight end-only lexical interface, separate from syntax construction. |
| 40 / `0d51c8d` | Reject out-of-index fact lookups immediately; dense traversal for recovery metadata. | Cheap reuse-range rejection; measure metadata walks separately from recognition. |
| 41 / `9dd914f`, 42 / `3cd5ffe` | Dense private chunk-slot lookup; transfer child temporaries at commit. | Arena-specific; only pursue equivalent changes if Nitrogen profiles show these costs. |
| 43 / `dfe9aeb` | Interpret only predicate fragment structure; generated rules decide referenced syntax. | Compile recognition callbacks for search predicates rather than interpreting their referenced grammar recursively. |
| 44 / `0954c58` | Reject impossible rule/predicate probes using conservative start sets before generated pre-recognition. | Filter before parser setup, including syntax-rule leading trivia. Preserve observation/dependency correctness for incremental parsing. |

## Actual implementation entry points

In `I:/work/Nitride/crates/nitride-core/src/`:

- `recovery/search.rs`: bounded repair search, `recognized_end`, continuation
  handling and the shared local-exhaustion diagnostic constant.
- `parser/shared.rs`: recovery probe `rule`, `prerecognize`, `token_end`,
  `predicate` and start-set checks. Both cached success and cached failure are
  answers; engine/resource errors still take the conservative fallback.
- `recovery/search/shared.rs`: recovery construction over the shared parser.
- `recovery/incremental.rs`: edit-aware recovery and shared-versus-flat fallback.
  The explicit exception for local exhaustion is present in the implementation.
- `parser/shared/generated.rs`: generated recognition machinery.

The matching `docs/stage26-evidence.md` and `stage34-evidence.md` through
`stage44-evidence.md` record measurements, rejected experiments and validation.
The more recent source takes precedence over stale nearby comments: for example,
the probe constructor still mentions keeping recognition interpreted, but the
actual rule/predicate paths call generated recognition.

## What the measurements do and do not establish

Stage35's paired 4 MiB one-error run improved from 1.664 s to 1.205 s by reducing
publication overhead. Stage37's paired 4 MiB junk run improved from 8.30 s to
1.20 s by avoiding redundant flat recovery. Stage38's paired 4 MiB dense run
improved from 18.6 s to 12.8 s by retaining generated negative answers.

At stage44, 4 MiB dense recovery measured 7.93 s, versus the recorded Roslyn
0.68 s comparison: about 12 times slower. The paired stage44 improvement was
8.57 s to 7.93 s. Single-character edits with an existing error improved roughly
5–10%. These are scoped benchmark results, not universal parity. Do not combine
stage timings from different host conditions into a precise cumulative speedup.

The optimized shared path was compared against the independent flat recovery on
4,334 standard and 1,286 adversarial traces, with zero recorded mismatches at
stage44. That differential check is essential when changing probe accounting:
charging a generated answer like reused recognition can alter budget behavior.

Experiments explicitly **not applied** include skipping recovering-snapshot
compaction (faster cold parse but slower later edits), wider lead prediction,
and consulting recorded facts at every interpreted probe call (no measured gain).
Stage43 identifies fewer search probes as the next structural opportunity;
unlike making equivalent probes cheaper, changing exploration can change repairs.

## Consequence for the Nitrogen work

Keep two tracks distinct:

1. Improve repair locality using the frozen preservation corpus. Nitride's
   bounded continuation search is a design reference; its speed optimizations
   do not themselves repair Nitrogen's swapped-token preservation gaps.
2. Add performance evidence for cold strict parsing, cold recovery and edits
   while errors persist. Preserve failed-pass healthy recognition first, then
   use compiled/end-only probes and conservative filters if bounded search is
   introduced. Keep a full-parse oracle and an unoptimized recovery reference.

Nitrogen already has generated token matchers and flat syntax arrays. Several
Nitride optimizations remove costs Nitrogen does not currently have. The major
remaining transferable capability is reuse across malformed edits with correct
invalidation and context. Same-input failed-pass seeding is now implemented for
modules declaring the contract above.
