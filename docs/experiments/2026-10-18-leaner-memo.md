# Leaner memo (perf/parse-memory)

Date: 2026-10-18. Machine: Apple M4 Pro (14 cores), macOS 27.0.1, .NET 10.0.0 runtime, BenchmarkDotNet 0.15.6.

Before: `5e53a7f` (the branch before the leaner-memo plan, separate `long[]`/`int[]` arrays, grown at 50% load).
After: `4fa4ddd` (one `Entry[]`, grown at 75% load).
Input: the CSNitrogen `CSharp1` benchmark file (300 namespaces, 1,074,151 chars, 562,525 tree nodes), parsed with `Language.Parse` after a small warm-up parse in a fresh process.

## Memo after one large parse (read before any GC)

The large-parse context is held only weakly, so the probe reads it from `t_cached` or `t_oversized` right after the parse. Bytes are array length times element size.

| | Before | After |
|---|---:|---:|
| Entries | 271,808 | 187,502 |
| Slots | 1,048,576 | 262,144 |
| Load | 0.259 | 0.715 |
| Memo bytes | 20.97 MB | 6.29 MB |
| Bytes per slot | 20 | 24 |
| Memo hits | 143,104 | 106,800 |
| Memo misses | 271,808 | 308,112 |
| Hit rate | 34.49% | 25.74% |
| Arena nodes | 698,727 | 660,925 |

The target was 10.5 MB or less (half of 20.97 MB); the memo is 6.29 MB, 70% smaller. The numbers were identical in three fresh processes. An `Entry` is 24 bytes, not 20: the `long` key pads the struct to 8-byte alignment.

The lookup count is the same (143,104 + 271,808 and 106,800 + 308,112 are both 414,912), but fewer lookups hit and 31% fewer entries are stored, so the plan stores fewer results in the memo. The parse tree is identical (562,525 nodes both ways) and time did not regress (below).

Arena nodes fall from 698,727 to 660,925 because `ParserState.Reset` (`Nitrogen.Runtime/ParserState.cs`, about line 82) reclaims a rolled-back parse's arena nodes only when no memo write happened since the mark. Writing fewer failure entries lets more rollbacks reclaim their nodes, which is safe because a failure entry references no node.

The transient peak during the final `Grow` holds the old array and the new one together: 131,072 slots of 24 bytes plus 262,144 slots of 24 bytes is about 9.4 MB, still under the 10.5 MB target.

## Warm full parse (BenchmarkDotNet, `--filter '*FullParse*'`, default job, base then perf in the same sitting, nothing else running)

| | Before (mean) | After (mean) | Allocated before | Allocated after |
|---|---:|---:|---:|---:|
| Roslyn | 12.85 ms | 13.00 ms | 4.23 MB | 4.23 MB |
| NitrogenTree | 77.55 ms | 77.38 ms | 43.71 MB | 26.85 MB |
| NitrogenGrammarOnly | 69.95 ms | 70.16 ms | 25.08 MB | 19.97 MB |
| CSNitrogenFullApi | 102.80 ms | 102.47 ms | 120.48 MB | 92.48 MB |

`NitrogenTree` is no worse (77.38 ms against 77.55 ms, within the error). The allocated column drops because the oversized context is freed by the next collection and reallocated by the next parse, and the memo is now 3.3 times smaller to reallocate. An earlier pair of runs, partly overlapping another benchmark, gave 84.4 ms and 83.4 ms with the same ordering.

## Commands

```sh
# base uses a detached worktree at 5e53a7f, perf uses this worktree
dotnet build <csn-copy>/benchmarks/CSNitrogen.Benchmarks -c Release -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:NitrogenRoot=<root>
NitrogenRoot=<root> dotnet <csn-copy>/benchmarks/CSNitrogen.Benchmarks/bin/Release/net10.0/CSNitrogen.Benchmarks.dll memoprobe
# run from inside the benchmark project directory, or BenchmarkDotNet cannot find the csproj
NitrogenRoot=<root> dotnet bin/Release/net10.0/CSNitrogen.Benchmarks.dll --filter '*FullParse*' --artifacts bdn
```

`memoprobe` is a new mode in the CSNitrogen benchmark project (`Workload.NitrogenMemoProbe`).
