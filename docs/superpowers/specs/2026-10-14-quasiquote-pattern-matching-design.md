# Typed quasiquote pattern matching

Status: approved by the user on 2026-10-14; implemented and independently reviewed. Automated Release and distribution checks pass; the user confirmed all manual VS Code acceptance checks pass on 2026-10-14.

## Intent and agreed scope

Nitrogen can now construct reusable syntax with typed quote helpers. The next feature recognizes
syntax with the same grammar, captures its parts, and feeds those parts into existing quotation
construction. This reduces handwritten tree walking and enables conditional grammar helpers.

The user approved both interfaces, starting with the C# API, then `.ngr` match arms. Matching ignores
trivia, compares exact token spelling, requires structural equality for repeated capture names, and
returns captures borrowing the input tree. The initial list syntax permits at most one variable-length
capture per physical list. Match arms are ordered and have an explicit final fallback.

This specification fixes the detailed contracts for those choices. Written-spec approval and a
reviewed implementation plan precede product code.

## Reuse existing capabilities

- `Language.ParseTemplate` already recognizes typed scalar/list holes through the composed parser.
- `SyntaxTree` supplies immutable node kinds, ordered children, token spans and trivia; its pooled
  storage has an explicit disposal boundary.
- `TemplateLists` records category, separator and minimum-count information at template list sites.
- `QuoteHole`, `QuoteNode` and `QuoteArgument` already express typed scalar/list syntax operands.
- `QuoteTemplate.Instantiate` supplies the construction side and preserves operand source origins.
- The workspace's syntax-only compilation validates composed syntax without executing configured C#.

Use these contracts. Do not introduce a second parser, textual pattern substitution, a new evaluator,
or a separate expression-tree representation. Parsed templates and patterns may share validation
utilities, but matching is a separate operation with its own ownership and diagnostic contract.

## Phase 1: C# interface

Proposed surface:

```csharp
using var compiled = language.CompilePattern("Calc.Expr", "f(1, ..$Args, 2)",
    new Dictionary<string, QuoteHole>
    {
        ["Args"] = QuoteHole.List("Calc.Expr"),
    });

if (compiled.Success)
{
    var match = compiled.Pattern!.Match(new QuoteNode(input.Tree, input.Tree.Root));
    if (match.Matched)
    {
        using var result = replacement.Template!.Instantiate(match.Captures);
        // Bind/check/lower the constructed result through the existing APIs when needed.
    }
}
```

`Language.CompilePattern(category, text, captures)` returns a disposable `PatternCompilation`, with
`Success`, nullable `Pattern`, and diagnostics. The compilation owns parsed pattern storage; disposing
it invalidates further matches. Copy the capture map during compilation so subsequent dictionary
mutations do not alter the compiled contract.

`QuotePattern.Match(QuoteNode)` returns `QuoteMatchResult` with `Matched`, a read-only dictionary of
`QuoteArgument` captures, and diagnostics. A normal mismatch returns false with empty captures and
empty diagnostics. Invalid source input, incompatible category/snapshot, or disposed compilation
returns false with diagnostics and no captures. Invalid API arguments such as a null map follow
existing quotation API argument-validation conventions.

All declared captures must occur in the parsed pattern. Unused capture declarations are compile
errors, since a successful match must provide every declared capture. Opaque dollar text in tokens
and comments is not an occurrence. Declaration detection uses actual parsed hole nodes.

The caller may pass a root or a valid subtree. Matching performs no automatic source traversal:
callers explicitly choose nodes. The root's accepted category must match the compiled category.

## Structural comparison

A non-capture node matches when its syntax kind and ordered structural children match. Leaf token
and literal text compares ordinally, exactly as written. Trivia, comments, offsets, source filenames
and tree quotation identities do not participate in structural equality.

Consequently `a + b` and `a /* note */ + b` match the same pattern. `01` and `1` differ, identifiers
with different spelling differ, and `a + b` does not match `b + a`. Parentheses remain syntax structure:
there is no automatic parenthesis removal, algebraic simplification, semantic equivalence, symbol
identity comparison, name capture avoidance, alpha equivalence, or rewrite expansion.

Ordinary structural wrappers are retained; no generic flattening of aliases or single-child nodes.
The composed parser and generated category metadata determine compatible capture sites.

Scalar holes capture one accepted syntax node, preserving its original tree/node identity and origin.
If a capture name occurs again, its new value must be structurally equal to its first value. Equal
subtrees at different input positions are allowed; the returned capture is the first occurrence.

A capture-free pattern is valid and returns an empty capture map on success. Matching does not change
input text, nodes, bindings, checks, semantic caches or source origins.

## List comparison

A supported list pattern may contain fixed items before and after one list hole, for example
`f(1, ..$Args, 2)`. After matching the fixed prefix and suffix, capture the remaining middle items in
source order. Separators are structural punctuation used to identify items; they are not captures.

An empty middle is a valid capture. The candidate tree must itself be valid for its grammar's list
contract; matching cannot make a required list empty. Both literal-separated and supported unseparated
syntax lists use existing metadata. Unsupported list sites are compilation errors.

There may be one variable-length capture per physical list, including repeated occurrences of the same
capture name. A name can occur in different lists, where the same structural equality requirement
applies to the ordered item sequences. Nested lists each have their own one-hole allowance.

Patterns such as `f(..$Before, $Pivot, ..$After)` are rejected in this version. No backtracking rule,
greedy/shortest split choice, or ambiguity resolution is implicit. List captures contain item nodes,
not a synthesized parent list or copied text.

## Lifetimes, input validity and origins

A successful result borrows source syntax. Capture arrays/dictionaries are independent immutable
containers, but their `QuoteNode`s refer to the original tree. They can be used with a compiled
replacement while the input remains alive; disposing the pattern does not invalidate those captures.
Disposing the input invalidates later access or instantiation under existing quotation checks.

Require the same `Language` snapshot identity as the compiled pattern, even when another snapshot has
identically named kinds. Reject disposed trees, out-of-range nodes, recovered/ambiguous syntax,
missing/skipped/error nodes, and uninstantiated hole syntax with diagnostics. Reuse the applicable
source-validity checks from quotation construction.

Carry an explicitly supplied `QuoteNode.Origin` through the first captured occurrence. When no explicit
origin is supplied, retain the source tree/node spans and let the existing construction-origin path
handle them. Pattern positions describe diagnostics; they do not become the origins of captured source
nodes. The matcher does not claim to infer original document paths from an unannotated tree.

Matching syntax that declares names is read-only and is permitted when the category supports it.
This does not permit return quotations to author new declarations: the existing construction restriction
continues to apply. Token holes are not introduced; capture categories remain syntax/extensible rules.

## Diagnostics and determinism

Introduce documented `NQP` pattern diagnostics (the existing `NP` family belongs to HIR projection) rather than overloading ordinary non-match as an error:
invalid category/pattern, unused/invalid capture contract, unsupported list layout, disposed/invalid
source, and snapshot/category incompatibility. Use `NQP0001` for invalid category/pattern syntax, `NQP0002` for an invalid or unused capture contract,
`NQP0003` for unsupported list layouts, `NQP0004` for disposed/invalid source or pattern storage, and
`NQP0005` for snapshot/category incompatibility. These codes are unused in the current repository.

Do not expose partial captures when any comparison fails. Pattern compilation and matching must use
real parsed holes, including when a language token can match the same dollar text. Tests must cover
both opaque-dollar tokens and tokens overlapping declared capture names.

Use iterative structural comparison/work stacks to avoid introducing recursion over untrusted tree
depth. One variable-length capture per list makes slicing deterministic. Equality for repeated captures
must use the same comparer; there is no separate textual equality shortcut. No quantitative latency
claim is made until measured.

## Phase 2: `.ngr` helper match bodies

Extend typed syntax functions with an explicitly selected scalar input and ordered arms:

```text
syntax function SwapAddition(Value: Expr) : Expr = match Value
{
  | (Left: Expr, Right: Expr) <[ $Left + $Right ]> => <[ $Right + $Left ]>;
  | _ => <[ $Value ]>;
};

| Swapped = Value:Expr "swapped" precedence 30 rewrites SwapAddition(Value)
```

The selector must be one scalar syntax parameter. Arm captures have explicit scalar/list category
annotations using the existing `Name: Category` / `Name: Category*` form. Explicit types allow the
composed parser to inject captures without guessing their categories from text. A capture-free arm
may omit the declaration list. Type inference is a possible later convenience.

Arm-local capture names must be unique and cannot shadow helper parameters. Each declared arm capture
must occur in its pattern. Result quotes may use the helper parameters and that arm's captures, with
exact category and scalar/list checking. Arm captures are not visible in other arms or in the fallback.
Repeated occurrences inside a pattern follow Phase 1 structural equality.

Match the selector's original syntax, before expanding or lowering that operand. Select the first
matching arm. Require exactly one final `_` fallback, which returns a quote using helper parameters.
Reject duplicate/nonfinal fallbacks. No arbitrary C# guards, nested helper calls in arms, or implicit
no-match exception are included in this slice.

All patterns and all result quotations are validated against the full composed language, including
unused helpers/arms. Static grammar checks cover names, selector type, arm scope and category/shape;
composed validation checks actual patterns, list layouts and returned syntax.

Emit declarative match-arm metadata and invoke the shared runtime matcher during rewrite expansion.
Bind captured source nodes to the selected return template through existing structural construction.
Keep existing simple quote helpers and inline rewrites unchanged, with the same rewrite-cycle guards,
ownership, caller binding and related-source diagnostic behavior. No additional handwritten evaluator
or dynamic C# condition execution is required.

A captured source node must keep the caller's semantic context when inserted by an arm; the arm's
module scope must not capture its identifiers. Errors in selected return syntax identify the invocation
and retain the arm/helper definition as related source. Syntax-only extraction/validation must not
execute configured C# initializers.

## Editor behavior

Support definition/declaration, references and completion for the selector, arm capture declarations,
pattern occurrences, result occurrences, categories and quoted keywords. Resolve scopes structurally:
identically named captures in different arms are distinct identities.

Rename updates actual parsed captures only and preserves all existing helper call resolutions and
pattern/capture structure across every connected composition. Refuse a rename that turns opaque token
text into a capture, hides a real capture, captures an imported helper, or leaves a known consumer
unchanged. Signed grammar files use masked text with original source offsets.

Recognize constant C# `CompilePattern` patterns/maps for quotation navigation using the same method-
identity checks as `CompileQuote`; avoid treating unrelated methods named `CompilePattern` as Nitrogen.
Expose diagnostics at original pattern/capture source positions. Changes in development mode refresh
consuming language documents without a server restart.

Existing Extract Quote Helper keeps extracting complete inline return quotes. Moving an entire match
helper or extracting a subpattern is outside this slice and must be refused safely by incompatible
refactorings. Do not silently drop arms when old extraction/model visitors encounter them.

## Alternatives and limits

A textual/regex matcher would be smaller but would lose precedence, custom tokens, categories and
source identity; reject it. Matching lowered HIR would serve semantic equivalence but would lose
syntax details and require a distinct contract; defer it. Multiple list captures would require a split
policy or backtracking; defer them as the user selected.

Also deferred: token captures, capture type inference, optional captures, wildcard syntax beyond
arm fallback, semantic guards, binding-aware equality, global automatic rewrites, nested helper
composition, traversal/replacement APIs and pattern-based Extract Block.

## Acceptance gates

1. C# scalar and list matching, capture-free success, ordinary non-match and zero partial captures.
2. Exact kinds/leaf text, ignored trivia, precedence/parenthesis structure and repeated structural equality.
3. Prefix/suffix and empty/nested lists; one hole per list; rejected unsupported or multiple-hole layouts.
4. Real holes versus opaque dollar text, including tokens that overlap declared capture spellings.
5. Root/subtree categories, nominal category mismatch, different snapshot, disposal and recovered input.
6. Borrowed capture lifetime and source origins; match→instantiate round-trip through existing bind/check/lower.
7. `.ngr` bootstrap/self-host parity, ordered arms, mandatory fallback, arm-local scope, imports and unused-arm validation.
8. Actual program parity with caller-bound operands, selected-arm errors and preserved source origins/cycle guards.
9. Complete grammar/C# editor navigation and rename safety, signed files, connected compositions and live refresh.
10. Full solution/client/generator/package checks and native VS Code acceptance for a concrete match helper demo.

Implement and verify Phase 1 first, then Phase 2 using that same matcher. Preserve the standalone
Nitrogen workspace boundary and the user's unrelated dirty documentation in the primary checkout.
