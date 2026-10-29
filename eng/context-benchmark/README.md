# Scoped context measurement

Run Release after builds/tests are idle. This is a small repeatable measurement harness, not a performance unit test. It validates input, warms each case with1000 operations, then records seven rounds of elapsed time and thread allocation per parse, plus tree and memo counts. The default Calc case runs everywhere; active contexts use the generated context fixture. Set EnableContexts=false when replaying this exact harness against the pre-feature base. An optional CSNitrogenRoot adds the real CSharp4 factory; NitrogenRoot must point its project references at the same checkout being measured.

```sh
dotnet run --project eng/context-benchmark/ContextBenchmark.csproj -c Release -p:UseSharedCompilation=false -p:NuGetAudit=false
```

For a baseline, copy this harness into a separate clean checkout of the recorded pre-feature commit and set `-p:EnableContexts=false`. Record the exact source revisions, SDK/runtime, build configuration, input sizes, all samples and the memo/reuse-entry sizes. Compare medians and spread; do not infer an editor latency or allocation improvement from a single run. The reported warmed allocations exclude initial table allocation; entry sizes expose the raw table storage cost separately.
