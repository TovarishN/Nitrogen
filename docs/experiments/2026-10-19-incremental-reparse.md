# Incremental reparse (perf/parse-memory)

Date: 2026-10-19. Machine: Apple M4 Pro (14 cores), macOS 27.0, .NET 10.0.0 runtime, BenchmarkDotNet 0.15.6.

Nitrogen: `perf/parse-memory`, incremental reparse (Tasks 1–6 of `docs/superpowers/plans/2026-10-19-incremental-reparse.md`)
plus the linear-time reuse-table build. CSNitrogen: branch `compact-trees` at `de5de1e` (incremental preprocessing,
`CSharpParser.ParseIncremental`/`Reparse`). Input: the CSNitrogen `CSharp1` benchmark file (300 namespaces,
1,074,151 chars, 46,204 lines). Edits: the benchmark's `TypeChar`, `InsertStatement` (adds a line break) and
`AddMethod` (adds several lines), all in the middle namespace.

## Result

| Edit | `RoslynIncremental` | `NitrogenIncremental` | Ratio | Target | `CSNitrogenIncrementalFullApi` |
|---|---:|---:|---:|---:|---:|
| TypeChar | 1.062 ms | **1.808 ms** | 1.70× | ≤ 2× | 20.98 ms |
| InsertStatement | 1.074 ms | **1.827 ms** | 1.70× | ≤ 2× | 20.89 ms |
| AddMethod | 1.099 ms | **1.877 ms** | 1.71× | ≤ 2× | 21.07 ms |

`NitrogenIncremental` is `ConditionalPreprocessor.Reprocess` (spliced for all three edits) plus `Language.Reparse`:
the fair counterpart of Roslyn's `WithChangedText`, as `NitrogenTree` is of `ParseText`. A full Nitrogen parse of the
same text takes 77 ms, so a reparse is about 42× faster. `CSNitrogenIncrementalFullApi` also recomputes the syntax
policy issues over the whole tree (about 19 ms), which the target excludes.

`validate` checks each edit: the reparse equals a full parse node for node, and reuses 405,881 of 405,916 nodes
(`TypeChar`), 405,888 of 405,926 (`InsertStatement`), 405,899 of 405,948 (`AddMethod`).

### Where a reparse spends its time

`TypeChar`, best of 30 after warm-up (a temporary stopwatch probe, since removed):

| Phase | Before the table fix | After |
|---|---:|---:|
| Incremental preprocessing (splice) | 0.12 ms | 0.12 ms |
| Grammar run (371 arena nodes, 335 grafts) | 0.14 ms | 0.14 ms |
| Freeze (copying ~405k grafted nodes) | 0.9 ms | 0.9 ms |
| Reuse-table build (200,740 entries) | 5.2 ms | 0.45 ms |
| Total | 6.08 ms | 1.64 ms |

The first version carried the previous table's entries over and sorted the whole table with a comparison delegate.
Each grafted block's entries are already a sorted slice of the previous table moved by a constant, so the table is
now built in one pass that merges those slices with the few new records.

### Plain parses are unchanged

Same sitting, baseline `6044ca6` (before this plan) against the current branch, CSNitrogen `3fe197d`:

| | Baseline | Current |
|---|---:|---:|
| `NitrogenTree` | 77.18 ms | 77.22 ms |
| `NitrogenGrammarOnly` (two interleaved runs each) | 69.27 / 72.44 ms | 70.10 / 70.70 ms |

### Memory (`memoryall 3`, live heap while holding the result, MB)

| | Live |
|---|---:|
| Roslyn | 4.95 |
| `nitrogen-tree` (plain) | 7.44 |
| `nitrogen-incremental` | 14.91 |

Incremental mode keeps about 7.5 MB more for this file (about 18 B per tree node): the reuse table (200,740 entries
of 24 bytes, about 4.8 MB) and the preprocessed text the result keeps for the next reparse (2.15 MB). The plain-parse
memory target does not apply to it; it is opt-in for editors.

### After the final review's fixes

Re-measured after fixing the review's findings (trivia peek reads, duplicate carried entries, CR LF splicing):
`NitrogenIncremental` 1.873 / 1.862 / 1.846 ms against `RoslynIncremental` 1.099 / 1.079 / 1.078 ms (1.70–1.73×), with
the same reuse. Over a session the reuse table no longer grows: 50 consecutive reparses of a 100-statement Calc
program keep it within 20 entries of its first size (it grew from 901 to 5,951 before the fix).

## Correctness checks

- Nitrogen's randomized oracle (`IncrementalOracleTests`): six grammar cases, 200 seeds by default, 5,000 passed in
  long mode, every reparse compared node for node with a full parse, including diagnostics. It found a failed
  reparse reporting a smaller expected set at the furthest failure (fixed: retry the fast pass without reuse).
- CSNitrogen's `Reparse_equals_full_parse_on_csharp_corpus`: 100 seeds over the Mono corpus and a generated C# 1 file
  with directives, comments and strings, including directive, comment-opening and string edits. It found rolled-back
  grafts corrupting carried-over reuse entries (fixed in `ReuseTable`).
- The final whole-branch review found a peek past trivia that under-reported what the trivia function read (a `/` that
  starts no comment), so a reparse could reuse an operand whose continuation an edit changed; it is fixed, pinned by a
  `Divide` test grammar and an oracle case (which fails without the fix). It also found the table growth above and a
  CR LF case in CSNitrogen's splice, both fixed with tests.

## Commands

```sh
cd ~/work/CSNitrogen-compact/benchmarks/CSNitrogen.Benchmarks
dotnet build -c Release -p:NitrogenRoot=$HOME/work/Nitrogen-perf
dotnet bin/Release/net10.0/CSNitrogen.Benchmarks.dll validate
NitrogenRoot=$HOME/work/Nitrogen-perf dotnet bin/Release/net10.0/CSNitrogen.Benchmarks.dll --filter '*EditReparseBenchmarks*'
NitrogenRoot=$HOME/work/Nitrogen-perf dotnet bin/Release/net10.0/CSNitrogen.Benchmarks.dll memoryall 3
dotnet bin/Release/net10.0/CSNitrogen.Benchmarks.dll phases
```
