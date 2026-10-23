# Sequence patterns and discard wildcards

Status: approved by the user on 2026-10-21, including first-complete-match, shortest-span-first selection.
Implemented locally with automated runtime, helper, editor-service and Release validation. See the sibling implementation plan and validation ledger; live VS Code/Rider visual acceptance remains pending.

## Intent

Recognize one or several elements at arbitrary positions in an ordered syntax list, optionally
capturing intervening sequences. Allow ignored elements and sequences without introducing bindings.
Use the existing typed quotation parser and structural matcher in both C# and grammar match arms.

User requirements:

- Multiple variable-length holes in one list, allowing multiple selected elements anywhere.
- `$_` matches one element without capturing it.
- `..$_` matches zero or more elements without capturing them.

Design recommendations below, especially match selection and search limits, are proposed defaults
rather than previously agreed requirements.

## Surface and examples

| Form | Consumption | Binding |
| --- | --- | --- |
| `$X` | Exactly one accepted syntax node | Scalar `X` |
| `$_` | Exactly one accepted syntax node | None |
| `..$Xs` | Zero or more consecutive list items | Ordered list `Xs` |
| `..$_` | Zero or more consecutive list items | None |

For a language with call expressions and comma-separated arguments:

```text
f(..$_, $X, ..$_)                    // one selected element anywhere
f(..$_, $X, ..$_, $Y, ..$_)         // two selected elements in order
f(..$_, $X, $_, $Y, ..$_)           // exactly one element between X and Y
f(..$Before, $X, ..$Between, $Y, ..$After)
f(..$_, $X, ..$_, $X, ..$_)         // two distinct positions with equal syntax
f(..$_, g($_), ..$_, h($Y), ..$_)   // structural constraints on selected elements
```

All examples match the complete call expression and its complete argument list. There is no
implicit subtree search or implicit skipped prefix/suffix. Scalar slots consume distinct list
positions, even when their syntax is equal. List order is significant. Nested lists are independent
sequence sites within one overall match.

`f($_)` requires exactly one argument; `f(..$_)` accepts every valid argument list, including an empty
one when the grammar permits it. `$_` outside a list matches one syntax node at a supported scalar
hole site; it cannot stand for an absent optional child. `..$_` requires a supported list item site.

## Match selection

Keep `QuotePattern.Match` as a single-result operation. Enumerate candidate sequence splits in
depth-first, pattern-source order, trying each variable-length segment from shortest to longest.
Return the first candidate for which the entire pattern succeeds. This gives the earliest viable
selected positions for ordinary arbitrary-position patterns, with later selected positions used to
break ties. Structural constraints and repeated-name equality can force earlier choices to advance.

For `f(a, b, c)`, the two-element pattern above returns `X = a`, `Y = b`. For
`f(..$A, ..$B)`, return `A = []`, `B = [a, b, c]`. Adjacent segments are legal, including anonymous ones;
their partition is selected by the same rule. No ambiguity diagnostic is produced for multiple
successful splits.

A locally successful nested list split must remain revisitable until the complete outer pattern
succeeds. For example, a repeated capture later in the tree may invalidate the first nested choice.
Branch order is defined by the logical pattern structure, not parser node allocation order.

Existing grammar arms remain ordered: try the next arm only after a complete ordinary mismatch of
the current arm. Its final bare `_` remains the fallback arm and is distinct from `$_` inside quotes.

All-matches enumeration, unique-match checking, unordered matching, guards, greedy modifiers,
alternation and general regular-expression syntax are outside this change.

## Wildcard typing and parsing

Named holes retain explicit category and scalar/list declarations. Wildcards require no declaration
in the C# capture map or grammar arm capture list. Reserve `_` as a capture name in pattern contracts;
reject an explicit `_` declaration with `NQP0002` (and the grammar capture diagnostic at its source).
Each wildcard occurrence is independent, even when scalar and list wildcards share a pattern.

Introduce an internal pattern parsing mode, separate from ordinary parsing and construction templates.
At an existing supported scalar quotation hole site, recognize exact `$_` and record the category
requested by the generated parser. At an existing supported list hole site, recognize exact `..$_`
and record the list's item category. Store category and shape metadata per occurrence; a single
synthetic dictionary entry named `_` cannot represent different categories or shapes.

Use existing composed grammar parsing, list metadata and ambiguity rejection. If more than one
incompatible parse/category interpretation survives, reject compilation rather than guessing.
Wildcards get the same precedence over overlapping ordinary tokens as declared holes at supported
hole sites in pattern mode. Opaque strings, comments and tokens containing dollar text remain opaque.
Recognize identifier boundaries: `$_Name` is a named hole, not a wildcard followed by `Name`.

Discarding does not erase type constraints: wildcard candidates must be accepted by their recorded
category. Existing unsupported token-hole and list-site restrictions remain. Literal separators are
validated as punctuation and are neither matched as items nor captured.

Ordinary source parsing, public `ParseTemplate`, and construction `CompileQuote` keep their existing
behavior. In particular, construction does not acquire discard semantics. An existing construction
contract using the name `_` need not change; reserving the name applies to pattern captures only.

## Existing interfaces

The existing C# entry point needs no new required arguments:

```csharp
using var compiled = language.CompilePattern("Calc.Expr",
    "f(..$_, $X, ..$_, $Y, ..$_)",
    new Dictionary<string, QuoteHole>
    {
        ["X"] = QuoteHole.Node("Calc.Expr"),
        ["Y"] = QuoteHole.Node("Calc.Expr"),
    });
```

Only `X` and `Y` appear in successful captures. Named list captures still use `QuoteHole.List`.
The grammar equivalent uses existing arm-local declarations:

```ngr
syntax function SelectPair(Value: Expr) : Expr = match Value
{
  | (X: Expr, Y: Expr) <[ f(..$_, $X, ..$_, $Y, ..$_) ]> => <[ g($X, $Y) ]>;
  | _ => <[ $Value ]>;
};
```

The call forms assume `f` and `g` are valid syntax in the composed language. Every named declaration
must occur. Repeated names keep structural equality, including repeated list captures in the same
physical list. Scalar/list shape conflicts remain compile errors. Wildcards cannot be referenced as
bindings in result quotes; construction validates its own explicit contracts as before.

## Matcher architecture

Replace the single `HoleIndex` list plan with an ordered sequence plan containing structural items,
named segments and discard segments. Preserve minimum input cardinality and separator metadata.
Normalize supported empty, singleton-collapsed and ordinary lists through existing `ListSlot` rules.
Grammar-required minimum cardinality constrains input validity, not each captured segment's length.

Use an explicit work stack and choice stack. A choice point records input position, next candidate
segment length, remaining pattern work, and a capture rollback checkpoint. Record newly introduced
bindings in a trail. On branch failure, remove bindings added after the checkpoint and restore all
pending work. Repeated bindings compare structurally without overwriting the first occurrence.
Publish immutable capture containers only after full success. Never expose discarded or partial
bindings. Match state belongs to one invocation; compiled plans remain reusable.

Precompute the minimum number of items needed by each remaining sequence suffix to prune impossible
splits. A previously bound repeated list segment has a fixed candidate length. Represent candidate
segments as views over normalized input items while searching, and materialize named capture arrays
only on successful publication. Discard spans allocate no capture arrays.

Retain a deterministic fast path for zero/one-segment lists when it preserves the specified traversal
and rollback semantics. Do not add capture-blind memoization: results can depend on earlier bindings.
Keep structural equality and traversal iterative, including nested choices, to retain stack safety.

## Search bounds and failures

General multiple-segment patterns with repeated bindings can have combinatorial search cost; no
general linear-time promise is made. Bound search explicitly rather than returning false on resource
exhaustion or allowing an editor to run indefinitely.

Proposed API: preserve `Match(QuoteNode)` and add `Match(QuoteNode, QuoteMatchOptions)`, where options
provide a positive `MaxWorkSteps` and a cancellation token. Existing calls and grammar arms use
a default of 1,000,000 steps per complete pattern invocation. Charge a step for each candidate split,
structural node-pair comparison (including repeated-capture equality), and node visited during input
validation. Check cancellation throughout these loops. The exact default is a proposed initial value
to validate with measurements before release, not a measured latency guarantee.

Exhaustion returns `Matched = false`, empty captures, and new `NQP0006`; this is an evaluation error,
not an ordinary mismatch. Propagate it through grammar helper diagnostics and stop arm evaluation;
do not silently take a fallback. Caller cancellation throws `OperationCanceledException` according to
normal .NET conventions and also cannot select a fallback. Reject nonpositive budgets as invalid API
arguments. The invocation counter covers all backtracking and nested lists and never resets on retry.

Existing invalid-input diagnostics and whole-input validation remain, including for ignored syntax.
Recovered syntax cannot be hidden behind a wildcard. Snapshot checks, borrowed capture lifetimes,
origin preservation, exact token comparison and trivia-insensitive equality remain unchanged.

## Editor and compatibility

Update grammar and C# quotation analysis to use the same pattern parsing mode and per-site metadata.
Wildcards have category/shape hover information but no declaration, rename target, reference entry
or capture-map completion. Offer wildcard spellings at supported pattern positions. Named capture
navigation, rename and diagnostics remain unchanged; unknown names remain errors.

Previously accepted patterns retain their results, except explicitly declared pattern capture `_`,
which becomes reserved, and sufficiently expensive matches that now report budget exhaustion.
`NQP0003` still reports unsupported list sites but no longer rejects multiple supported segments.
Document the reserved-name compatibility change. Existing construction and ordinary parsing remain
separate compatibility gates.

## Acceptance evidence

- C# public API and grammar arms match selected first/middle/last items, two and three ordered items,
  adjacent items, empty spans, named surrounding spans and capture-free wildcard patterns.
- Demonstrate shortest-first selection with multiple successful partitions; exercise a failed early
  candidate followed by success, including revisiting nested choices due to later repeated bindings.
- Check repeated scalar/list equality, independent anonymous occurrences and absence of leaked captures
  after mismatch, success after backtracking, budget failure and repeated calls on one compiled plan.
- Cover separated/unseparated lists, empty and collapsed singleton input, required lists, nested lists,
  literal separators, unsupported sites, category mismatch, ambiguous wildcard sites and mixed shapes.
- Verify actual parsed-hole behavior for comments, strings, overlapping tokens and `$_Name`; ensure
  construction and ordinary parsing retain behavior and `_` capture declarations receive diagnostics.
- Check disposal, input snapshot, explicit origins, invalid syntax hidden in discarded spans, stack
  depth, bounded adversarial searches and cancellation; ensure errors do not reach later helper arms.
- Exercise configured language validation for unused arms/helpers, C# and grammar editor hover,
  completion, navigation and rename, then verify the actual editor interaction separately.
- Run focused quotation, helper and language-service tests; broader repository gates follow the
  implementation plan. Measure representative and adversarial searches before finalizing the default.

## Delivery boundary

After design approval, write an implementation plan covering pattern-mode parsing and metadata,
sequence search and rollback, bounded-search propagation, grammar/editor integration and validation.
This document is the reviewable design deliverable; it does not claim these features exist today.
