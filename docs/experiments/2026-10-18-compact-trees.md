# Compact trees (perf/parse-memory)

Date: 2026-10-19. Machine: Apple M4 Pro (14 cores), macOS 27.0, .NET 10.0.0 runtime, BenchmarkDotNet 0.15.6.

Before: `2b3d856` (after the leaner-memo plan, before compact trees).
After: `fb3a81b` (Tasks 1–6 of the compact-trees plan) with CSNitrogen branch `compact-trees`
(grammar marked `transparent`, `CSharpParser.Parse` passing the authored source as `treeText`).
Packed: `0743164` (node flags packed into the top four bits of the dense kind, no `_flags` array) with
CSNitrogen `compact-trees` at `3fe197d`, measured on 2026-10-19 the same way.
Input: the CSNitrogen `CSharp1` benchmark file (300 namespaces, 1,074,151 chars, 46,204 lines).
Before and after were built the same way (`NitrogenRoot=<worktree>`) and run back to back.

## Result

| | Before | After | Packed | Roslyn |
|---|---:|---:|---:|---:|
| Tree nodes | 562,525 | 405,916 | 405,916 | |
| `nitrogen-tree` live heap while holding the tree | 14.71 MB | 7.84 MB | **7.44 MB** | 4.95 MB |
| `csnitrogen` (public API) live while holding the document | 17.03 MB | 11.69 MB | 11.28 MB | |
| Tree bytes used | 12.81 MB (22.8 B/node) | 8.00 MB (19.70 B/node) | 7.59 MB (18.70 B/node) | |
| Masked text retained by the tree | 2.15 MB | 0 | 0 | |
| `NitrogenTree` warm mean (BDN) | 79.46 ms | 74.19 ms | **73.13 ms** | 13.21 ms |
| `NitrogenTree` allocated (BDN) | 35.71 MB | 25.12 MB | 18.87 MB | 4.22 MB |
| `NitrogenGrammarOnly` warm mean | 73.11 ms | 66.88 ms | 66.05 ms | |
| `CSNitrogenFullApi` warm mean | 106.19 ms | 94.79 ms | 94.45 ms | |
| `nitrogen-tree` first parse, fresh process (median of 10) | 406.8 ms | 411.8 ms | not rerun | 124.3 ms |
| `nitrogen-tree` second parse, same process (median of 10) | 110.7 ms | 107.0 ms | not rerun | 76.0 ms |

The Roslyn column is from the packed run (the after run measured 13.34 ms and 4.23 MB). The BDN
`allocated` figure for `NitrogenTree` moves between runs by more than the packing explains (the flags
array was 0.41 MB): the large-parse context is held only weakly between iterations, so how often its
arena and memo are reallocated depends on when the GC runs.

Targets:

- Live heap with the tree at most 1.5x Roslyn (7.4 MB): **missed narrowly**, 7.44 MB (1.50x of 4.95 MB,
  0.04 MB over 7.4 MB) after packing the flags; 7.84 MB (1.58x) before it.
- Warm `NitrogenTree` no slower than before: met, 73.13 ms against 79.46 ms (8.0% faster; 74.19 ms
  before packing, a change within noise).
- About 400k nodes or fewer: met within rounding, 405,916 (-27.8%).

All edit variants validate (`validate`: Roslyn 0 errors, CSNitrogen accepted, incremental equals full),
before and after packing.
The memo is unchanged (187,502 entries, 6.29 MB); arena nodes fall from 660,925 to 452,116.

## Why the memory target is still missed

The tree is the whole live cost (`memoryall` shows no arena or memo held after the parse; the
large-parse context is held weakly). Breakdown of the packed tree (`treebreakdown`):

| Array | Entries | Bytes/entry | MB |
|---|---:|---:|---:|
| `_kind` (12-bit dense id + 4 flag bits) | 405,916 | 2 | 0.81 |
| `_start` | 405,916 | 4 | 1.62 |
| `_length` | 405,916 | 4 | 1.62 |
| `_childStart` | 405,917 | 4 | 1.62 |
| `_children` (477,023 slots, 71,108 null) | 477,023 | 4 | 1.91 |
| **Total** | | **18.70/node** | **7.59** |

`_kindOf` is a shared snapshot (256 entries for this language), `_trivia` and `_parent` are not built by
a plain parse. "Live w/ tree" (7.44 MB) is below the tree's arrays (7.59 MB) because `MemoryOnce`
subtracts a baseline taken after a small warm-up parse, and that baseline includes small-parse scratch
(the pooled thread context) that the large parse no longer holds.

So the per-node cost is 14 bytes of fixed columns plus 4.7 bytes of child slots. In the live metric the
tree costs 18.33 B/node (7.44 MB / 405,916), so 7.4 MB allows about 404k nodes (before packing,
19.31 B/node live, the threshold was about 383k); at 405,916 nodes the tree needs at most 18.6 B/node.

Layout levers:

| Lever | Status | Saving on this input |
|---|---|---:|
| Dense 16-bit kinds instead of 32-bit | done (Task 5) | 0.81 MB |
| Flags in the top 4 bits of the dense kind (12-bit ids, at most 4095 kinds per language; CSNitrogen's grammar has 321 module kinds plus 10 built-ins) | done | 0.41 MB |
| `_length` as `ushort` with a sorted overflow side table for the rare node longer than 65,535 chars | not done | about 0.8 MB |
| Leaf length from the literal's text (`Literal` leaves are 126,905 nodes, 31%) | not done; needs a separate leaf column or a per-kind literal table | rough: up to 0.5 MB |
| `_childStart` only for interior nodes (an index from node to interior row) | not done; costs a lookup per `Child` | rough: 0.3 to 0.6 MB net |
| Drop null slots | rejected: positional child indices are API | 0.28 MB |

The `_length` lever alone would meet the target (projected about 6.6 MB live); it was not taken here
because it adds a branch to every `Span` call and an overflow table to the freezer, and the gap is 0.04 MB.

What remains removable in the grammar was not removed, because each would change what CSNitrogen
distinguishes:

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
counted; it sums every array field, so the packed run needed no change for the removed `_flags`); the before run used the benchmark as it was. The `csnitrogen` cold first parse (858.8 ms
before, 520.6 ms after) is not comparable: the review-admission gate short-circuits on the changed
source hashes in the after build (see the Task 7 report).
