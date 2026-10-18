# Compact trees, leaner memo and incremental reparse

Status: proposed, 2026-10-18. Builds on branch `perf/parse-memory`. That branch has 4 commits: oversized scratch dropped or weakly held, exact-size large trees, and child counts derived from a prefix sum.

## Why

Nitrogen should be as fast and as memory-efficient as it can reasonably be, with Roslyn as the yardstick. Tradeoffs are acceptable only where they are real. The owner's words: when there is no obvious tradeoff, do it "as fast as possible and as memory efficient as possible". The 80 ms edit reparse causes no concrete pain today; the gap to Roslyn is the motivation.

Nitrogen has no external users yet. Public tree APIs, generated typed views and `SyntaxDumper` output **may change**, provided Nitrogen's own examples, tests and CSNitrogen are updated in the same effort.

## Measured starting point

The benchmark is `CSNitrogen/benchmarks/CSNitrogen.Benchmarks`. Its input is a generated C# 1 file of 1,074,151 chars and 46,204 lines. Measurements were taken on an Apple M4 Pro with .NET 10, against the `perf/parse-memory` runtime.

| | Nitrogen | Roslyn 5.0 |
|---|---:|---:|
| Live heap while holding the tree (`memoryall`) | 14.67 MB (12.81 tree + 2.15 preprocessed text) | 4.95 MB |
| Warm full parse, preprocessing + grammar (`NitrogenTree`) | ~85 ms | ~14 ms |
| Reparse after a one-character edit | ~85 ms (full parse) | ~1.1 ms (`WithChangedText`) |
| Tree nodes | 562,525 | 146,707 nodes + 189,909 tokens, stored as 55,690 + 7,910 shared objects |

A parse builds 698,727 arena nodes, so 19% are discarded by backtracking. The memo holds 271,808 entries: 121,500 successes and 150,308 failures, with 143,104 hits. It uses 1,048,576 slots of 20 B each.

The tree has these wasteful node shapes:

- 37,207 `Empty` leaves for absent optionals.
- 33,901 empty `List` nodes and 30,901 one-item lists.
- Pure choice rules that wrap their one child. Examples are `BuiltinType -> Literal` (11,700) and the C# `Literal` rule wrapping an `Integer` token (8,100).
- Single-field extensible alternatives that wrap their child. Examples are `NameExpression -> Identifier` (18,900), `StandardStatement -> StandardEmbeddedStatement` (11,700), `StandardModifier -> Literal` (10,800), `BuiltinTypeName -> BuiltinType` (10,800), `LiteralExpression -> Literal` (9,600) and `StandardClassMember` (6,300).

The tree uses about 21 B/node: kind, start, length, child start and child slot are ints, plus a flags byte.

## Targets (agreed)

On the benchmark file, compared with Roslyn on the same machine:

1. **Memory (plain parse):** live heap while holding a tree from a plain parse is at most **1.5× Roslyn's**, about 7.4 MB today. Preprocessed text that Nitrogen creates counts toward this.
2. **Edit reparse (incremental mode):** reparse after a small edit (the benchmark's `TypeChar`, `InsertStatement` and `AddMethod`) is at most **2× Roslyn's incremental reparse**, about 2–3 ms. This covers preprocessing plus the grammar parse, the same fair counterpart `NitrogenTree` is for a full parse. CSNitrogen's syntax policy checks are excluded, as Roslyn defers its own checks.

**Two modes (owner's decision).** The caller always knows which case it is in. An editor wants fast reparses; a batch tool parses once. So:

- A **plain parse** (the default) keeps no reuse data, and target 1 applies to it.
- **Incremental mode** is opt-in at parse time. Its result keeps the data reuse needs: examined lengths, call context and producing rule. Target 2 applies to it. Its extra memory over a plain parse is measured and reported separately in `memoryall` as an "incremental" row, and does not count against target 1. Keep that extra memory reasonable; the builder reports bytes per node. A tree produced by an incremental reparse is itself in incremental mode.
3. **No regressions:** warm full-parse time is no worse than today (~85 ms). Small and medium parses keep their constant, size-independent steady-state allocation, enforced by `AllocationTests`.

## Non-goals

- Speeding up the full parse beyond what these changes give for free. A separate effort can tackle the 6× full-parse gap.
- Sharing identical subtrees as Roslyn does. Flat arrays have no per-object header, so the memory target is reachable without sharing.
- Incremental reparse in the recovery pass, in template or quasiquote parsing, or across preprocessing changes. Those fall back to a full parse.
- Changing what C# syntax CSNitrogen accepts.

## Anti-goals

These would make the work a failure even if the numbers improve:

- **An incremental reparse whose tree differs from a full parse of the same text in any way.** That covers kinds, spans, flags, children, trivia and diagnostics. Correctness beats speed; when reuse safety is uncertain, do not reuse.
- Typed views or semantics that silently read the wrong child after node removal.
- Span lookup becoming non-constant-time. Positions stay absolute (see Part 3).

## Part 1: fewer nodes and a compact layout

**Fewer nodes.** These are decided:

- **Absent optionals and empty lists** produce no node. The parent's child slot holds a **null slot**, and positional child indices stay stable. `ChildCount` keeps counting null slots. `Child(n, k)` returns a "no node" value: -1 or an equivalent sentinel, builder's call. `Optional<T>.HasValue` and `SyntaxList<T>.Count` handle it.
- **One-item lists** store the item directly in the slot. The generated view knows statically that slot k is a list; a node there that is not a `List` kind is a one-item list. The generator keeps the `List` node whenever an item could itself be a `List` node, such as nested repetitions, so this is never ambiguous. Separated lists behave the same way: one item and no separators.
- **Pure choice rules** become **transparent**. A pure choice rule is one whose every alternative is a single element that yields one node, such as `BuiltinType = "bool" / "byte" / …` or `VariableInitializer = ArrayInitializer / Expression`. It produces no node of its own; the chosen child's node stands in its place. Its typed view becomes a "one of these kinds" view. The generator infers this; grammars need no annotation.
- **Single-field pass-through alternatives** can be declared transparent in the grammar. An example is `StandardStatement = Body:(…)`. The marked alternative produces no node; its single child's node stands in, and the extension point's view accepts the child's kinds. The syntax for the marker is the builder's call, and it must be documented in `docs/language-guide.md`. Apply it in CSNitrogen wherever the alternative carries no information beyond "it is one of these". `NameExpression -> Identifier` is **not** transparent unless the binder and semantics never distinguish a name expression from an identifier; the builder checks this.
- **Keyword and punctuation `Literal` leaves stay.** They match Roslyn's tokens, give exact spans to tooling, and trivia is derived from the gaps between leaves.

**Compact layout.** Aim for about 14 B/node or less, which reaches the memory target at about 380k nodes. Decided: positions stay absolute (start and length or end), so `Span` is constant-time. The rest of the layout is the builder's call. Candidates:

- A dense per-language 16-bit kind id with flags packed into its spare bits.
- No child-slot array for leaves.
- A preorder subtree-size field, with a per-node presence mask for null slots.

`Child(n, k)` must stay constant-time or amortized constant-time for the access patterns typed views and lists use: fixed small k and sequential enumeration. Indexing into a list can be O(log n), but no worse.

**Affected code.** The builder audits every reader of `SyntaxKinds.Empty`, `SyntaxKinds.List` and `SyntaxKinds.Group`, and of positional children:

- Runtime: `Views.cs`, `SyntaxList.cs`, `SyntaxDumper.cs`, `Binding/FileBinding.cs`, `Semantics/FileSemantics.cs`, `Semantic/DeclarativeTypes.cs`, `Quotation/*`, `Language.Patterns.cs`, `Language.Quotation.cs`, `ParserState*.cs`.
- Generator: `SyntaxCodeWriter.cs` and `SemanticsWriter.cs`.
- Language service: `Formatter.cs`, `SemanticTokenBuilder.cs`, `NitrogenLanguageService.*` and `QuoteHelperSourceIndex.cs`.
- CLI: `NgrAssist.cs`.
- In CSNitrogen: the parsing policies and every positional child access. The template-list hole machinery (`TemplateLists`, `CloseTemplateList`) relies on list nodes and must keep working.

## Part 2: a leaner memo

Done means the memo's **peak size on the benchmark is at least halved** from 20.97 MB (1,048,576 slots), with no regression in full-parse time. The build arena's size is out of scope. (Builder's call, settled by the author.) Decided directions; the builder tunes and measures:

- **Skip memoizing cheap failures.** An example is a failure where no prefix candidate exists for the first-character bucket, because recomputing it costs less than the slot. Keep failures that ran candidates.
- **Denser slots and a higher load factor.** Options include Robin Hood hashing or a smaller key and stamp encoding. Today a slot is 20 B at at most 50% load.
- Optionally, **drop entries the parse can no longer reach**, for example behind a committed boundary, using the existing commit analysis. Do this only if measurement shows it pays.

Report memo entries, slots, hit rate and full-parse time before and after.

## Part 3: incremental reparse

**API.** The builder chooses the API; it must be explicit and safe to misuse. A plain parse result can't be passed as `previous`: either it is rejected, or it falls back to a full parse, documented. Something like `language.Parse(newText, start, previous: oldResult, edits)`, or a reparse method on `ParseResult` taking the old tree plus a text change list such as `(span, newLength)`. The old result must still be alive; using a disposed one throws. CSNitrogen gets a matching entry point with **incremental preprocessing**. An edit that touches no directive line and no inactive region, and that inserts no `#` or line break that could start a directive, is applied to the masked text unchanged; the directive records and inactive spans shift. Any other edit reruns the preprocessor in full.

**Mechanism (decided).** Reuse is copying, not sharing. When the parser is about to parse a rule R at new position p:

1. Map p to the old position p'. Then look for an old node produced by R that starts at p'. For an extensible rule, the minimum-precedence context must also match (see the stored data below).
2. Reuse that node if its **examined range**, `[p', p' + examinedLength)`, does not intersect any edited range.
3. Reusing means copying the old subtree's contiguous preorder block into the new arena or tree. This covers its node rows and its child-slot range, with child indices rebased and positions shifted by a constant. Then continue after it, at `p + width`.

Absolute positions stay; the copy is a near-memcpy plus constant adds. Copying the whole untouched tree costs about 1–2 ms for 10 MB, so the 2–3 ms target is plausible. If measurement shows the copy dominates, the builder may revisit Roslyn-style relative widths. That would be a documented decision with numbers, not a default.

**Stored data needed for reuse.** The builder chooses the encoding: a side table or spare bits.

- **Examined length per reusable node.** This is how far the parser *looked*, including lookahead past the node's end, failed literal and token attempts, trivia scanning and `&`/`!` predicates. It must be conservative: overestimating only costs reuse, underestimating is a correctness bug. The builder audits every place `ParserState` and generated code read text.
- **Call context for extensible results.** The minimum precedence, and any other parse-state input the result depends on. Audit `ParserState` for hidden state: `CallPoint`, `CallFlags`, `Holes`/`InTemplate`, recovery flags and extension registries. Results that depend on state the reuse check doesn't cover must not be reused.

**Scope.** Only the fast pass reuses. If the incremental fast pass fails (a syntax error), the recovery pass runs as a full parse. A parse whose new text has errors is still correct, just not faster.

**Correctness oracle (required).** Add a randomized test. It takes a corpus that includes the generated C# file, CSNitrogen's test corpus and Nitrogen's example grammars. It applies random single and multi-edits: inserts, deletes and replacements, including edits inside tokens, comments, string literals, lookahead-sensitive spots such as `a < b > c` generics, and near the end of file. It asserts that the incremental tree equals a full parse of the new text, node for node: kind, span, flags, child structure, trivia, skipped spans and diagnostics. Run it in CI with a fixed seed set, plus an opt-in long-running mode.

## Measurement and done

Extend CSNitrogen's benchmark project:

- Add a `NitrogenIncremental` method to `EditReparseBenchmarks`.
- Re-run `validate`, `memoryall 3`, `coldall 10` and `--filter '*'`.
- Record before and after numbers in the benchmark's README and in a short evidence note in `docs/`.

Done means all of the following:

- The three targets are met, or the shortfall is documented with the measured cause.
- `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj` pass, including the new oracle.
- CSNitrogen's `CSNitrogen.Tests` and `CSNitrogen.Oracle.Tests` pass against the new runtime, built through `-p:NitrogenRoot` or the `NitrogenRoot` environment variable.
- CSNitrogen's grammar, policy and view-usage updates are on a CSNitrogen branch, with its pinned dependency (`docs/coverage/dependencies.json`) left for the owner to re-pin.

## Order

Do Part 2 first; it is small and independent. Then Part 1, then Part 3, which depends on Part 1's final layout. Each part lands as its own reviewed commits with measurements.

## Left to the builder

- Exact node layout, the null-slot sentinel and the encoding of examined length and call context.
- The transparent-alternative grammar syntax.
- Memo data structure details.
- The incremental API shape within the constraints above.
- How the CSNitrogen benchmark gets the new runtime.

Everything left to the builder must keep the anti-goals intact.

## Calls made without the owner

- Absolute positions with copy-based reuse instead of Roslyn's relative widths.
- Keeping keyword and punctuation leaves.
- Not reusing in the recovery pass.
- The edit target covers preprocessing plus grammar, with a fast path for incremental preprocessing; policy checks are excluded.
- The memo is done when its peak size is at least halved.
- Inferring pure-choice transparency automatically, while pass-through alternatives need an explicit grammar marker.
- Ordering the parts as Part 2, then Part 1, then Part 3.
