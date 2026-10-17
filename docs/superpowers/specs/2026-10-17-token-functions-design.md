# Grammar functions: macros written in `.ngr`

Status: approved by the user on 2026-10-17 and implemented the same day
([plan](../plans/2026-10-17-grammar-functions.md)). Planning revised the syntax so that values and grammar
expressions do not collide: `if … then … else`, `where`, `()` and parenthesized value bounds. The
examples below use the implemented syntax. At the user's direction, Nitrogen ships no function
library: grammars declare the functions they use, so CSNitrogen declares `HexIn` in its own
`Lexical.ngr`. One `function` form covers quote helpers and grammar macros. It supersedes the built-in `hex(W) in C` from
[hex-value tokens](2026-10-17-hex-value-tokens-design.md), which is implemented but unreleased.

## Intent

`hex(W) in C` solves one problem with one hard-coded generator. Grammar authors need to define such
generators themselves, in `.ngr`, and share them between modules. Examples are escapes limited to a
class, case-insensitive keywords, counted repetition, numeric ranges such as `0..255`, and recurring
syntax shapes such as separated or delimited lists.

A **grammar function** is a pure, typed function evaluated at grammar-compile time. A call expands to
ordinary grammar expressions: classes, literals, references, repetitions, sequences and choices.
Generated parsers, the runtime and editors therefore need no new matching machinery.

## One function form

`.ngr` already has `syntax function` quote helpers, which build syntax of the language being defined.
Grammar functions use the same declaration form, and the return type selects the kind:

```ngr
function Twice(Args: Expr*) : Expr = <[ f(..$Args, ..$Args) ]>;                 // quote helper
function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule = ...;  // grammar function
```

| Return type | Kind | Evaluated | Body |
| --- | --- | --- | --- |
| A category of the language, such as `Expr` | Quote helper (unchanged) | When a rewrite is instantiated | A `<[ ... ]>` template or `match` |
| `Grammar.Rule` | Grammar function | Once, when the grammar is compiled | A grammar-function expression (below) |

`syntax function` stays as an alias of `function`, because quote helpers shipped in 0.15. A helper's
existing meaning and checks do not change. Both kinds share declaration parsing, module export,
`using` resolution, navigation, hover, and checking at definition. They differ only in evaluation.

Built-in grammar types live in the `Grammar` namespace. A language may define its own `Token` or
`Class` category without ambiguity.

## Example

```ngr
syntax module MyLanguage.Lexical   // any grammar; Nitrogen ships no library
{
  // The hex digit d, either case.
  function HexDigit(d: Grammar.Int) : Grammar.Rule =
    if d < 10 then [char(48 + d)] else [char(55 + d) char(87 + d)];

  // Exactly `width` hex digits whose value is a member of `values`.
  function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule =
    if width == 0 then ()
    else choice d in 0 .. 15 {
      let size = 16 ^ (width - 1);
      let low = d * size;
      let part = values & [low .. (low + size - 1)];
      where part != [] : HexDigit(d) HexIn(width - 1, part - low)
    };

  // A case-insensitive literal.
  function Keyword(word: Grammar.Text) : Grammar.Rule =
    seq c in word { [upper(c) lower(c)] };

  function Repeat(item: Grammar.Rule, min: Grammar.Int, max: Grammar.Int) : Grammar.Rule =
    seq i in 1 .. max { if i <= min then item else item? };

  // Items with separators, and an optional trailing separator.
  function TrailingList(item: Grammar.Rule, separator: Grammar.Rule) : Grammar.Rule =
    (item; separator)* separator?;
}

syntax module CSharp.Lexical
{
  using MyLanguage.Lexical;
  token IdStart      = [ ... ];
  token UnicodeStart = "\\u" HexIn(4, IdStart) / "\\U0000" HexIn(4, IdStart);
}
```

The implemented `hex(W) in C` becomes `HexIn(W, C)`, declared in `.ngr` by the grammar that needs it.

## Language

**Declaration.** `function Name(p: Type, ...) : Grammar.Rule = Body;`. A module exports a function like a rule.
A call resolves through `using` and qualified names, like a rule reference.

**Types.** All values are known at compile time.

| Type | Values and operations |
| --- | --- |
| `Grammar.Int` | 64-bit integers: `+ - * / % ^`, comparisons |
| `Grammar.Char` | `char(Int)`, `code(Char)`, `upper`, `lower` |
| `Grammar.Text` | quoted strings; `seq c in text` iterates its characters |
| `Grammar.Class` | code-unit sets: class literals with computed bounds `[a .. b]`, `&` intersection, `\|` union, `-` difference, `~` complement within U+0000–U+FFFF, `Class - Int` / `Class + Int` shift, `== []` emptiness |
| `Grammar.Rule` | grammar expressions: literals, classes, rule references, labels, `?` `*` `+`, separated lists, `!` `&`, sequence by juxtaposition, `/` choice, `()` |

A class value converts to a one-character `Grammar.Rule` wherever one is expected. A token rule
whose body is a class, or a choice of class rules, is also a `Grammar.Class` value. That is how
`IdStart` is passed above. Any rule name is a `Grammar.Rule` value.

**Body expressions.** `if c then a else b`; leading `let x = e;` lines; calls, including recursive calls;
`choice x in a .. b { ... }` and `seq x in a .. b { ... }` comprehensions over integer ranges or text,
each block with optional `let` lines and a `where cond :` filter; `()` for the empty sequence.
Value expressions appear only in delimited positions: after `let`, `if` and `where`, in ranges, in
call arguments, and as class bounds, where anything other than a character, name or call is
parenthesized. See the [plan](../plans/2026-10-17-grammar-functions.md) for the grammar.
Functions have no side effects, no I/O and no C#.

**Calls.** `Name(arg, ...)` with no whitespace before `(` is a call. `Name (X)` stays a reference followed
by a group, as it is today. A space before `(` after a function name is diagnosed with a fix.

## Where a call may appear

One `Grammar.Rule` result works in both token and syntax rules. The expansion is validated where
it is called, exactly like handwritten text in the same place.

- **In a token rule**, the expansion must reference token rules only. The existing check
  (`TokenReferencesSyntax`) reports a violation at the call, with the reference inside the
  expansion as a related location.
- **In a syntax rule**, trivia is skipped between elements, as for handwritten elements. That is
  right for `TrailingList(Arg, ",")`. A call whose expansion is a sequence of single-character
  classes, such as `HexIn`, would then accept `\u 00 41`. It gets a warning suggesting a token rule.
- Labels inside an expansion become fields of the calling rule. Two calls that introduce the same
  label report the existing duplicate-label diagnostic at the second call.

## Semantics and evaluation

- **Typed and checked at definition.** Each body is type-checked even when unused, as quote helpers
  are. Argument types are checked at each call.
- **Pure, memoized and hash-consed.** Evaluation memoizes calls on their argument values. Produced
  expressions are structurally shared. `HexIn(3, full)` is computed once however often it appears. A
  fully covered 4-digit trie costs four calls, not 65,536 leaves.
- **Bounded.** Recursion depth and the produced node count have fixed limits. Exceeding one is a
  call-site error with the call chain as related locations. Because evaluation is total, the source
  generator stays deterministic and cacheable.
- **Factoring.** A general optimizer merges choice alternatives `[a] S / [b] S` into `[ab] S` when
  `S` is structurally equal. It merges only when every alternative in between has a disjoint first
  set, so ordered-choice behavior is unchanged. It runs only on expansions in token rules, because view
  and field shapes of syntax rules must not change. Handwritten rules are unchanged. It replaces the
  special-purpose trie minimization in `HexValueExpansion`.
- **Stage.** Calls cross modules, so expansion moves from right after parsing to just before
  validation in `GrammarCompiler` and `CompositionWriter`. Both parsers keep a `GrammarCallExpr` node
  with its name, arguments and spans. The expansion stage replaces each call with its expansion in the
  modules that validation and code generation see. Editor features read the parsed model, which still
  has the calls. Quote helpers are unaffected: their calls stay in `rewrites`
  clauses and are still instantiated at rewrite time. The `HexValueExpr` work already threads such a
  node through every token-level consumer. Syntax-level consumers (`SyntaxCodeWriter`, `ViewWriter`,
  `BindingWriter`, `CommitAnalysis`, `QuotationWriter`) gain the same case.

## Editor support

- Go to definition, usages and rename for functions and parameters, shared with quote helpers. Rule
  references inside arguments stay navigable, as `IdStart` is today.
- Hover on a call shows the signature and the expansion's size: nodes, classes and depth. A
  command previews the expanded expression.
- Diagnostics: type errors at the argument or body expression; evaluation-limit errors and
  context errors at the call, with the chain as related locations.
- Semantic tokens color functions, parameters and the body keywords
  (`choice`, `seq`, `let`, `where`, `if`, `then`, `else`).
- Completion inside a rule body offers grammar functions; inside `rewrites`, quote helpers.

## Out of scope for the first version

User-defined grammar types, higher-order functions (passing a function as an argument), functions
that declare whole rules, and C#-backed functions. C# would need execution inside the source
generator and the language server, which the syntax-only toolchain deliberately avoids.

## Plan sketch

1. Model and both parsers: the `function` form (`syntax function` kept as an alias), body expression
   syntax, and `GrammarCallExpr`. Bootstrap/self-hosted parity tests.
2. Type checker and evaluator: values, memoization, hash-consing, limits, diagnostics.
3. Expansion stage after composition, with call-site context checks for token and syntax rules.
   Consumers switch from `HexValueExpr` to `GrammarCallExpr`.
4. Choice-factoring optimizer for token rules, with ordered-choice safety tests.
5. Example functions `HexIn`, `Keyword`, `Repeat` and `TrailingList` in test grammars, with no shipped library. Remove
   `hex(...)` and port its exhaustive property test to `HexIn`.
6. Editor features. Regenerate CSNitrogen `Lexical.ngr` with `HexIn`.
