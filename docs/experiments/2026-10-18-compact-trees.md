# Compact trees (perf/parse-memory)

Date: 2026-10-19. Machine: Apple M4 Pro (14 cores), macOS 27.0, .NET 10.0.0 runtime, BenchmarkDotNet 0.15.6.

Before: `2b3d856` (after the leaner-memo plan, before compact trees).
After: `fb3a81b` (Tasks 1–6 of the compact-trees plan) with CSNitrogen branch `compact-trees`
(grammar marked `transparent`, `CSharpParser.Parse` passing the authored source as `treeText`).
Input: the CSNitrogen `CSharp1` benchmark file (300 namespaces, 1,074,151 chars, 46,204 lines).
Before and after were built the same way (`NitrogenRoot=<worktree>`) and run back to back.

## Result

| | Before | After | Roslyn |
|---|---:|---:|---:|
| Tree nodes | 562,525 | 405,916 | |
| `nitrogen-tree` live heap while holding the tree | 14.71 MB | **7.84 MB** | 4.95 MB |
| `csnitrogen` (public API) live while holding the document | 17.03 MB | 11.69 MB | |
| Tree bytes used | 12.81 MB (22.8 B/node) | 8.00 MB (19.70 B/node) | |
| Masked text retained by the tree | 2.15 MB | 0 | |
| `NitrogenTree` warm mean (BDN) | 79.46 ms | **74.19 ms** | 13.34 ms |
| `NitrogenTree` allocated (BDN) | 35.71 MB | 25.12 MB | 4.23 MB |
| `NitrogenGrammarOnly` warm mean | 73.11 ms | 66.88 ms | |
| `CSNitrogenFullApi` warm mean | 106.19 ms | 94.79 ms | |
| `nitrogen-tree` first parse, fresh process (median of 10) | 406.8 ms | 411.8 ms | 124.3 ms |
| `nitrogen-tree` second parse, same process (median of 10) | 110.7 ms | 107.0 ms | 76.0 ms |

Targets:

- Live heap with the tree at most 1.5x Roslyn (7.4 MB): **missed**, 7.84 MB (1.58x), 0.44 MB over.
- Warm `NitrogenTree` no slower than before: met, 6.6% faster.
- About 400k nodes or fewer: met within rounding, 405,916 (-27.8%).

All edit variants validate (`validate`: Roslyn 0 errors, CSNitrogen accepted, incremental equals full).
The memo is unchanged (187,502 entries, 6.29 MB); arena nodes fall from 660,925 to 452,116.

## Why the memory target is missed

The tree is the whole live cost (`memoryall` shows no arena or memo held after the parse; the
large-parse context is held weakly). Breakdown of the after tree (`treebreakdown`):

| Array | Entries | Bytes/entry | MB |
|---|---:|---:|---:|
| `_kind` (dense ids) | 405,916 | 2 | 0.81 |
| `_start` | 405,916 | 4 | 1.62 |
| `_length` | 405,916 | 4 | 1.62 |
| `_childStart` | 405,917 | 4 | 1.62 |
| `_children` (477,023 slots, 71,108 null) | 477,023 | 4 | 1.91 |
| `_flags` | 405,916 | 1 | 0.41 |
| **Total** | | **19.70/node** | **8.00** |

`_kindOf` is a shared snapshot, `_trivia` and `_parent` are not built by a plain parse.

So the per-node cost is 15 bytes of fixed columns plus 4.7 bytes of child slots. At 19.70 B/node the
target needs about 375k nodes; at 405,916 nodes it needs at most 18.2 B/node. What remains removable in
the grammar was not removed, because each would change what CSNitrogen distinguishes:

- `StandardStatement` (12,600 nodes): its body has sequences (`ConstantDeclaration ";"`), which cannot be
  transparent (NGR0160). Splitting it into separate alternatives would change ordered choice into
  longest-match competition. CSNitrogen's stackalloc policy also tests for this kind.
- `StandardModifier` (10,800): the body starts with a predicate (`!Identifier`), not allowed in a
  transparent body.
- `NameExpression` (18,900), `LiteralExpression` (9,600), `BuiltinTypeName` (10,800): the CSNitrogen
  policies test these kinds by name (assignment target, primary-expression, inferred member and
  constraint checks), so they stay.

The 71,108 null slots cost 0.28 MB; they keep positional child indices stable, which the API requires.

## The masked text

`CSharpParser.Parse` now parses the masked buffer and passes `request.Source` as the tree text, so the
tree no longer holds the 2.15 MB masked copy. `ParseDocument` still holds it through its public
`Preprocessing` (`PreprocessResult.MaskedText`) and `MaskedText` properties, so the public API keeps it
alive for the document's lifetime (the `csnitrogen` row). Dropping it needs an API change.

## Commands

```sh
# Nitrogen baseline worktree and an isolated CSNitrogen copy at its committed HEAD (plus the benchmark copy)
git -C Nitrogen-perf worktree add --detach <scratch>/nitrogen-precompact 2b3d856
git -C CSNitrogen worktree add --detach <scratch>/csn-precompact HEAD
NitrogenRoot=<root> dotnet build benchmarks/CSNitrogen.Benchmarks -c Release -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet <dll> validate
dotnet <dll> memoprobe
dotnet <dll> memoryall 3
dotnet <dll> coldall 10
dotnet <dll> treebreakdown           # after only
cd benchmarks/CSNitrogen.Benchmarks && NitrogenRoot=<root> dotnet bin/Release/net10.0/CSNitrogen.Benchmarks.dll --filter '*FullParse*'
```

The after benchmark's `NitrogenTreeBytes` reads the new layout (ushort kinds, `_childStart` with
NodeCount + 1 entries, `_children` sized by `_childStart[NodeCount]`, lazy trivia, `_kindOf` not
counted); the before run used the benchmark as it was. The `csnitrogen` cold first parse (858.8 ms
before, 520.6 ms after) is not comparable: the review-admission gate short-circuits on the changed
source hashes in the after build (see the Task 7 report).
