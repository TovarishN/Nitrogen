# Scoped parsing context measurements

These are small parser measurements, not editor latency or CSharp conformance evidence. Both runs use .NET 10.0.0, Release, SDK 11.0.100-preview.2.26159.112 on the same host. Baseline is ed69ac8aaa3bbc7c8d44c97cf969d1b6ddd89ea2; candidate runtime/compiler measured at ddc3daa722a38686d59b750aed977e9f06df8938. Consumer is CSNitrogen 12bab5a78793ffb179b0454f22692df475fb5cfe, using the actual CSharp4 composition. Harness source is committed in eng/context-benchmark. All inputs must parse successfully without diagnostics.

Each row is the median of seven rounds, milliseconds per operation. Each case warms 50 parses; incremental rounds warm 50 reparses. Measurements run sequentially with this task's builds/tests idle. Other host workloads were not isolated; later process inspection showed unrelated editor/compiler activity, so timing variation includes uncontrolled host load. Default runs were baseline then candidate; stable runs were candidate then baseline with DOTNET_TieredCompilation=0. The latter changes JIT policy and is a sensitivity check, not the default deployment setting.

| Case | Default baseline | Default candidate | No-tier baseline | No-tier candidate |
|---|---:|---:|---:|---:|
| Calc/default | 0.126790 | 0.130619 | 0.026160 | 0.030321 |
| Calc/default/incremental | 0.039060 | 0.039100 | 0.006708 | 0.007381 |
| CSharp4/default | 0.492355 | 0.438172 | 0.392466 | 0.500037 |
| CSharp4/default/incremental | 0.039982 | 0.041966 | 0.013328 | 0.018038 |

Timing varies materially with warm-up and JIT policy. These short runs do not establish timing parity or a stable percentage regression. The candidate has additional cache-key work and larger tables; no speed improvement is claimed. Larger paired workloads are needed before a performance acceptance claim.

Tree counts, memo hits/misses and unaffected-node reuse counts match in every paired case. Warm full-parse allocation matches (616 bytes at default JIT; 640 Calc and 715 CSharp4 with tiering disabled). Incremental default allocation increases from 10,704 to 13,376 bytes for Calc and 21,480 to 27,984 for CSharp4. With tiering disabled the increases are the same 2,672 and 6,504 bytes respectively. Raw memo-entry and reuse-record sizes both grow from 24 to 32 bytes (33.3%). Warm allocation excludes initial cache allocation and pooled retained capacity; entry size is the storage cost, not total retained-memory measurement.

The candidate-only active-context case parses 480 characters, yielding 482 nodes. Default-JIT median full parse is 0.033475 ms; the incremental header edit reuses 476 nodes. These observations validate execution of active scopes; there is no equivalent baseline feature to compare. All samples are retained in the adjacent JSON files.

## Artifact identity

- baseline eng/context-benchmark/Program.cs: SHA256 `16bce464cbc74464b127b5094b96d54576a7ac3f7dd1e92e98ebe9bc29fb09c2`
- baseline eng/context-benchmark/bin/Release/net10.0/ContextBenchmark.dll: SHA256 `4651ffce9973476f4fc9f7e8270931c9cf14dc715ebe6a94644e666b7940a843`
- baseline eng/context-benchmark/bin/Release/net10.0/Nitrogen.Runtime.dll: SHA256 `f1de9427628a8e8252304b9878c818b935a4ef88e0688f1de769cce8f23d38c8`
- candidate eng/context-benchmark/Program.cs: SHA256 `16bce464cbc74464b127b5094b96d54576a7ac3f7dd1e92e98ebe9bc29fb09c2`
- candidate eng/context-benchmark/bin/Release/net10.0/ContextBenchmark.dll: SHA256 `c743396c8ba214e0c025e6454c6479e96993ed83939e134ad974997302325c84`
- candidate eng/context-benchmark/bin/Release/net10.0/Nitrogen.Runtime.dll: SHA256 `f6e84bc53af4232133d4ffcdccdf5e0c144c0e58b986c3b71fcce56ae503ee38`
