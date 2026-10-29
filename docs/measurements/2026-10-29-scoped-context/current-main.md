# Current-main comparison

Baseline: 3be215da (GitHub main fetched2026-10-29). Candidate:9b7e848a7304e3577948eff76acd0a0ae18679d9, including the recovery merge and context-preserving recovery seed copying. Both run Release on .NET10.0.0 with SDK11.0.100-preview.2.26159.112; CSNitrogen consumer source is12bab5a with the local b9350e9 proof commit, whose changes are confined to its test assembly. The benchmark uses the unchanged real CSharp4 production factory.

Harness warm-up is now1000 full parses and1000 reparses per round. Seven rounds are retained, with2000 operations per round except CSharp4 full parsing (500). Baseline then candidate run sequentially with this task's other builds/tests idle; host editor/compiler activity is uncontrolled. Tiered JIT remains enabled. Early rounds still show promotion effects, so these small measurements establish neither timing parity nor a stable speed regression.

| Case | Baseline median ms | Candidate median ms | Baseline bytes/op | Candidate bytes/op |
|---|---:|---:|---:|---:|
| Calc/default | 0.021405 | 0.021430 | 619 | 619 |
| Calc/default/incremental | 0.023721 | 0.023887 | 10707 | 13379 |
| CSharp4/default | 0.271211 | 0.274700 | 640 | 640 |
| CSharp4/default/incremental | 0.011984 | 0.012002 | 21486 | 27990 |

Every paired tree-node count, memo hit/miss count and reused-node count matches. Warm full-parse allocation matches; incremental allocation increases by2672 bytes for Calc and6504 for CSharp4. Raw memo and reuse entries are24 versus32 bytes. Warm allocation excludes initial tables and pooled retained capacity; no peak-memory or total-retained-memory claim is made. The candidate-only scoped case produces482 nodes and reuses476 after the header edit; its raw measurements are in main-current.json.

The earlier README/raw files retain the original pre-recovery measurements rather than overwriting that evidence. These current-main files are the final core comparison before review.

## Exact artifacts

- baseline eng/context-benchmark/Program.cs: SHA256 `e39c5c5773ff99578a26dc842b68690c71ff2f4b6efba94e9e72aae96c0f19e6`
- baseline eng/context-benchmark/bin/Release/net10.0/ContextBenchmark.dll: SHA256 `d61a70b249347b94d85499a4b97da7a731d9e3fefeb8c86aa701579f167407cd`
- baseline eng/context-benchmark/bin/Release/net10.0/Nitrogen.Runtime.dll: SHA256 `3310676f68b3d822c2d0420f105807ddd95e62360cc447b194ff71bc9095bed2`
- candidate eng/context-benchmark/Program.cs: SHA256 `e39c5c5773ff99578a26dc842b68690c71ff2f4b6efba94e9e72aae96c0f19e6`
- candidate eng/context-benchmark/bin/Release/net10.0/ContextBenchmark.dll: SHA256 `a261e3e965013cd848b6fdeea1e2c3d204887d32fec8b9b4ff6d0acaf0c64451`
- candidate eng/context-benchmark/bin/Release/net10.0/Nitrogen.Runtime.dll: SHA256 `0f650195d39a0d1ec88fbddcae3c807880b29f76274080113a596dae3140464b`
