# Named patterns, descendant search and ancestor context

Status: approved by the user on 2026-10-29. Implemented on branch named-patterns; Release build has zero warnings and 2,318 tests pass. Live VS Code/Rider visual acceptance is pending.

## Intent

Let one structural pattern describe syntax whose depth is not known in advance, without spelling out
every intervening level. Two shapes are needed:

- "X somewhere inside Y": a pattern finds a matching node at any depth below a position.
- "a structure defined in terms of itself": a pattern refers to itself recursively.

The user expects to need both directions: looking down from an outer node, and looking up from an
inner node to the context it sits in. This is the first slice of a larger series of matcher
extensions. The agreed order is:

1. Named patterns, recursion, descendant search and ancestor context (this document).
2. Token captures and guards that participate in backtracking.
3. Optional captures.
4. Binding-aware comparisons.

Semantic equivalence and automatic rewriting stay separate, later features.

This work targets the C# Nitrogen implementation. The Nitride Rust port has not yet ported list spans
or discards, so it will catch up separately.

## Goals

- Reusable **named patterns** declared once in a grammar module, usable from structural diagnostics,
  syntax-function match arms and C#.
- **Alternatives** inside a named pattern, tried in order. The first alternative that succeeds is
  committed (see Matcher architecture).
- **Recursive** named patterns that are guaranteed to terminate.
- **Descendant search** at a hole position. It finds a node at any depth and takes part in
  backtracking.
- An **ancestor context clause** on structural diagnostics, so each inner item gets its own report.
- A host-level **`FindAll`** operation for searching a whole file, with documented order and limits.

## Non-goals

- Token captures, guards, optional captures and binding-aware equality. These are later slices.
- Negation (`not`) and alternation written inline in a quote. Alternation exists only through named
  patterns in this slice. Both may come later.
- Exporting a named pattern's inner captures to its caller.
- Changing `Match(node)` semantics. It stays anchored to one node with no implicit subtree search.
- The Nitride Rust port.

## Anti-goals

- Patterns that can loop forever or run without a bound. Every new search must count against the
  existing `QuoteMatchBudget`.
- Hidden search. Descendant search happens only where a pattern explicitly asks for it.
- Changing the results of previously accepted patterns.

## Surface

All syntax below is the designer's call, not something the user chose. The builder may adjust
spelling, but must keep the semantics.

### Named patterns

A grammar module declares named patterns at module level. Each has a result category and one or more
ordered alternatives. Each alternative reuses the existing match-arm form, with an optional capture
list followed by a quote:

```ngr
pattern Secret : Element =
  | <[ <password ..$_>..$_</password> ]>
  | <[ <secret ..$_>..$_</secret> ]>
  | (v: Text) <[ <token ..$_>$v</token> ]>;
```

- The category (`Element`) is the syntax category of the node the pattern matches. Every
  alternative's quote is compiled in that category.
- Captures declared inside an alternative are **private**. They constrain the match, and repeated
  names inside one alternative keep structural equality, but the caller never sees them.
- Visibility follows the module's syntax categories: wherever the categories of this module are
  visible, including in extending modules, its patterns are too. Patterns are referenced by simple
  name or qualified name (`Xml.Secret`) using the same resolution rules as categories.
- All named patterns are compiled and validated when the language is composed, including ones that
  nothing references, matching existing diagnostic-pattern validation.

### Using a named pattern at a hole

A capture declaration may name a pattern where it would name a category:

```ngr
match (s: Secret) <[ <credentials ..$_>..$_ $s ..$_</credentials> ]> => warning ...
```

`$s` accepts a node only if `Secret` matches it at that node. The capture's type is the pattern's
category (`Element`), so `s` behaves exactly like a capture of category `Element` in messages. The
pattern's category must be accepted at the hole site under the same rules as an ordinary category.

List captures can use a pattern too: in `(xs: Secret*)`, every captured item must match.

### Descendant search at a hole

Prefixing the pattern name with `..` in the declaration searches at or below the hole position:

```ngr
match (s: ..Secret)
  <[ <credentials ..$_>..$_ $s ..$_</credentials> ]>
  => warning XML1002 : $"credentials contains a plaintext secret: {s}";
```

- The hole position accepts any node valid at that site. The matcher then searches that node and its
  descendants for a node matched by `Secret`, and binds `s` to the **found** node, not to the node at
  the hole position. The capture's type is the pattern's category.
- Search order is preorder in document order, starting with the hole node itself.
- The first candidate wins, but each candidate is a choice point. If anything later in the enclosing
  pattern fails, the matcher backtracks to the next candidate, the same way list spans retry.
- The search is a descendant search, not a search of the whole file. It never leaves the subtree at
  the hole position.
- Descendant search applies only to named patterns. `..Category` with a plain category is not part of
  this slice.
- A descendant capture may occur only once in a pattern. A repeated `$s` for an `..P` declaration is
  a compile error. Descendant search on a list capture (`..P*`) is also rejected. (Designer's call;
  either can be revisited later.)

The diagnostic reports at the outer node the match is anchored to, as today. This is intended:
looking down is for questions about the container ("does this block have the problem?"). When
there are several hits under one container, only one diagnostic is produced.

### Ancestor context clause

A structural diagnostic may add one `inside` or `parent` clause after its pattern:

```ngr
syntax Paired = ... {
  match <[ <password ..$_>..$_</password> ]>
    inside (attrs: Attribute*) <[ <credentials ..$attrs>..$_</credentials> ]>
    => warning XML1003 : $"Plaintext password inside credentials ({attrs.Count} attributes).";
}
```

- `inside P` passes if any proper ancestor of the current node matches `P`.
- `parent P` passes only if the immediate parent matches `P`.
- Ancestors whose category does not fit `P` are skipped without counting as a mismatch.
- Captures from the context pattern may be used in the message. If several ancestors match, the
  **nearest** one supplies them.
- The context pattern is an ordinary quote or a named-pattern reference, and may itself use named
  patterns and descendant search.
- The diagnostic still reports at the current node, so every inner item gets its own diagnostic.
  This is how per-item reporting is expressed. Looking up is for questions about the item ("is this
  item a problem here?").
- "Parent" means the semantic parent, as in `FileSemantics.ParentOf`. List, Group and Ambiguous
  wrapper nodes are skipped. Under raw `SyntaxTree.Parent`, the parent of `<password>` would be a
  content list, and `parent` could never match. (Designer's call.)
- An ancestor whose subtree fails input validation (recovered, ambiguous or skipped syntax) is
  treated as non-matching and skipped. This is not reported as an error, so a typo inside
  `<credentials>` silences the `inside` warning until it is fixed rather than turning it into a
  check failure. (User's decision, 2026-10-29.) The main pattern keeps today's validation behavior.
- Category filtering happens before any validation or matching. An ancestor whose category does not
  fit `P` costs one step, and its subtree is never validated.
- Names cannot be shared between the main pattern's captures and the context pattern's captures.
  Sharing a name is a compile error, not an equality join. (Designer's call. Binding-aware joins
  belong to a later slice.)
- One budget covers the whole check: the main pattern, every ancestor visited, and every
  context-pattern attempt. Each fitting ancestor is validated at most once per check. (Designer's
  call.)

The clause is diagnostic-only. `Match(node)` and syntax-function arms have no "above". A
syntax-function argument may be a detached quote, so there is no well-defined parent.

### Recursion

A named pattern may reference itself directly or through other patterns:

```ngr
pattern Nested : Expr =
  | (n: Nested) <[ ($n) ]>
  | (l: Number) <[ $l ]>;
```

Termination rule: every reference cycle must step into at least one child.

- A reference is **non-consuming** if its hole is the entire quote of an alternative (for example
  `(x: P) <[ $x ]>`), or if it is a descendant search (`..P`), which includes the node itself.
- Any other reference consumes at least one level, because it sits inside some structure.
- At composition, build the graph of non-consuming references between named patterns. Reject any
  cycle in it with a new grammar diagnostic that names the cycle.
- Within that rule, recursion depth is bounded by the input tree. All work counts against the
  existing budget.

### C# surface

- The `QuoteHole` capture contract gains two forms. One requires a named pattern at a hole, and the
  other is the descendant-search form, both naming the pattern by qualified name. Suggested names:
  `QuoteHole.Pattern("Xml.Secret")` and `QuoteHole.Descendant("Xml.Secret")`.
- `Language` exposes named patterns as compiled `QuotePattern` instances, so hosts can match or search
  with them directly. The `Language` snapshot owns them, and they live as long as it does. Hosts
  borrow them and do not dispose them.
- A host pattern resolves `QuoteHole.Pattern`/`Descendant` names when it is compiled, against the
  `Language` it is compiled with. It binds to those instances. Referencing a pattern from another
  snapshot is impossible by construction. (Designer's call.)
- New `QuotePattern.FindAll(QuoteNode root, QuoteMatchOptions? options)`:
  - It visits the root and its descendants in preorder, document order, and returns every node where
    the pattern matches, each with its captures.
  - Nested matches are all returned, so an outer and an inner match both appear. Hosts filter if they
    need to.
  - One budget covers the whole search, not one per node.
  - Exhaustion returns the matches found so far plus `NQP0006`, and the result is marked incomplete.
  - Cancellation throws, as `Match` does.

Diagnostics do not use `FindAll`. They already run once per node of their rule.

## Matcher architecture

These are constraints on the implementation. Internal shape is left to the builder.

- Keep the explicit work-list and choice-stack machine in `QuotePatternMatcher`. The new features are
  new job kinds, not recursion on the C# stack:
  - **Pattern-reference job.** Alternatives are tried in order, and the first one that succeeds is
    committed: choice points created inside the callee are cut when it succeeds. This is safe because
    a reference binds the same node whichever alternative matched, and the callee's captures are
    private, so another alternative could never change the caller's outcome. It also keeps
    references from multiplying the search. The callee's private captures live in a fresh capture
    scope that is discarded once the reference succeeds. (Designer's call, made while planning.)
  - **Descendant-search job.** It holds a preorder cursor over the subtree, and each candidate is a
    choice point.
- Charge budget steps for every alternative tried, every descendant visited, and every ancestor
  visited by `inside`/`parent`.
- A compiled named pattern is shared and immutable. Match state belongs to one invocation, as today.
- Do not memoize pattern-reference results across choice points unless it is provably capture-blind.
  Private captures make most references capture-blind to the caller, but repeated-name equality
  inside the callee still depends on that invocation.
- Validation of the input (rejecting recovered, ambiguous or template input) still covers the whole
  matched node, including subtrees reached only by descendant search.
- Budget exhaustion and invalid input in structural diagnostics keep their current behavior: they are
  reported as a check failure. Measure ordinary editing scenarios with `inside` and descendant search
  on large files before release. If the default budget is routinely hit, raise it with the user
  rather than changing the reporting. (Designer's call.)

## Diagnostics

New grammar and pattern diagnostics are needed for:

- an unknown named pattern;
- a named pattern whose category does not fit the hole site;
- a non-consuming reference cycle;
- an `inside`/`parent` clause outside a structural diagnostic;
- a duplicate pattern name in a module.

Codes are left to the builder, following the existing NGR and NQP numbering.

## Editor

Named patterns get go-to-definition, find-references, rename and hover (showing category and
alternatives), as categories do. Capture declarations offer named patterns in completion.

## Acceptance evidence

- Named pattern with several alternatives, used from a diagnostic, a syntax-function arm and C#.
  A failing alternative falls through to the next one, and a successful reference is committed.
- Private captures do not leak on success, and are rolled back on failure.
- Recursive pattern over nested parentheses matches at several depths. A non-consuming cycle is
  rejected at composition with a diagnostic naming the cycle.
- Descendant search: a hit at depth 0, at depth > 1, and none. Moving on to a second candidate when
  the first fails the named pattern. Backtracking from an enclosing list-span choice into a fresh
  descendant search. (Repeated descendant captures are illegal, so a later repeated capture cannot
  reject a candidate.)
- XML example:
  - the outer form gives one warning on `<credentials>`;
  - the `inside` form gives one warning per nested `<password>`, including at different depths.
- `inside` versus `parent`. Nearest-ancestor captures appear in the message.
- `FindAll`: preorder order, nested matches both returned, budget exhaustion returns partial results
  plus `NQP0006`, cancellation.
- Adversarial input (deep trees, many alternatives) hits the budget rather than hanging or overflowing
  the stack.
- Existing pattern tests are unchanged.

## Left to the builder

- Exact keyword spellings (`pattern`, `inside`, `parent`, the `..` prefix) if they conflict with the
  existing grammar-language syntax.
- Internal job and plan representation.
- Diagnostic codes and C# API names.
- Language-guide and XML example updates.
