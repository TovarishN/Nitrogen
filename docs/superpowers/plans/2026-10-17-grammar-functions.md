# Grammar Functions Implementation Plan

**Spec:** [grammar functions](../specs/2026-10-17-token-functions-design.md), approved by the user on 2026-10-17.
**Status:** implemented 2026-10-17. Nitrogen.Tests passes 1848/1848. CSNitrogen was regenerated with `HexIn` and built
against this checkout; it passes 754/754 tests and 3241/3241 oracle tests. Nothing is committed.
**Replaces:** the unreleased built-in `hex(W) in C` ([hex-value tokens](../specs/2026-10-17-hex-value-tokens-design.md)).

## Syntax decisions made while planning

The approved examples used `c ? a : b`, `empty` and arithmetic directly inside grammar expressions.
Grammar syntax already uses `?`, `*`, `+`, `/`, `!` and `&`, so `a * b` would be ambiguous. The
implementation therefore keeps two syntactic sorts:

- **Grammar expressions** are the existing syntax, plus these forms, valid only in function bodies:
  `if V then G else G`, `choice x in V [.. V] { Block }`, `seq x in V [.. V] { Block }`, the empty
  sequence `()`, and class bounds computed from values. Calls `F(V, ...)` may appear in any rule body.
- **Value expressions (V)** appear only in delimited positions: `let x = V;`, after `if` and `where`, in
  comprehension ranges, as call arguments, and as class bounds. A bound is a character, a name, a call
  or `(V)`. Value operators are `or and not`, `== != < <= > >=`, `|`, `&`, `+ -`, `* / %`, `^`, unary
  `- ~`. Atoms are integers, characters, strings, names, calls, class literals (`[]` is the empty
  class), `(V)`, and `{ Block }`, a grammar fragment used as a value.
- **Block:** `{ let x = V; ... [where V :] G }`. A function body is `let x = V; ... G`.
- **Call:** a name immediately followed by `(`, lexed as one token (`CallHead`), so `Name (X)` keeps its
  meaning. `then` and `else` are no longer unqualified rule references.

Example: `if d < 10 then [char(48 + d)] else [char(55 + d) char(87 + d)]`.

## Architecture

- **Model** (`GrammarModel.cs`): `GrammarFunctionDecl` in `ModuleDecl.Functions`; `ValueExpr` records;
  `GrammarCallExpr(Name, Args, Span, Expansion?)`, `IfExpr`, `ComprehensionExpr`, `ValueClassExpr`, and
  `FunctionBlock` with `LetBinding`. The empty sequence is a `SequenceExpr` with no items.
- **Parsing:** the bootstrap parser and `Nitrogen.ngr`, `GrammarSemantics` and `NgrProjector` produce
  identical models. `function` and `syntax function` share one header. A `<[` or `match` body makes a
  quote helper, which is unchanged. Otherwise the declaration is a grammar function and must return
  `Grammar.Rule`. A shared post-parse check (`GrammarFunctionSyntax.Check`) rejects function-only forms
  outside function bodies, with the same message and span from both parsers.
- **Expansion stage** (`GrammarFunctions.Expand(modules)`): the composed modules supply the functions.
  - Type-check every function at definition. Evaluate every call with memoization keyed by function
    and argument values, and with depth and node limits.
  - Resolve each rule reference in a body from the function's module, then qualify it.
  - Factor choices in token-rule expansions. Re-span each expansion to its call.
  - Warn on character-level expansions in syntax rules.
  - `GrammarCompiler` and `CompositionWriter` run it before `GrammarValidator`. It replaces each call
    with its expansion, so validation and code generation see only ordinary nodes. A failed call
    becomes a placeholder once its error is reported. Editor features read the parsed model.
- **No library:** a built-in `Nitrogen.Lexical` module was implemented, then removed at the user's
  direction. Grammars declare the functions they use; tests declare `HexDigit`, `HexIn`, `Keyword`,
  `Repeat` and `TrailingList` themselves.
- **Diagnostics** NGR0150–NGR0159: function type error, unknown function, argument count, argument
  type, evaluation limit, evaluation error (such as division by zero), function-only form outside a
  function, return type, duplicate function, and character-level call in a syntax rule (warning).
- **Editor:** grammar-rule navigation also indexes function definitions, call names, and rule names
  used as values. Diagnostics reach the editor through `GrammarCompiler`.

## Tasks

1. **Model, lexer and both parsers.** Add the tokens `< > <= >= == != - % ~` and `CallHead`. Parse
   headers, bodies, values, blocks and calls. Add self-hosting parity rows for every form, and invalid
   rows that fail in both parsers. Keep `hex(...)` until task 4.
2. **Evaluator and type checker.** Implement values (Int, Bool, Char, Text, Class, Rule), operators,
   the built-ins `char code upper lower length`, conversions (Char→Class→Rule, Text→Rule, class rule
   name→Class), memoization, limits and diagnostics. Unit-test each operator and error.
3. **Expansion stage.** Run it in `GrammarCompiler` and `CompositionWriter`. Add consumer cases for
   `GrammarCallExpr`, factoring for token rules, and context checks. Use generated-grammar runtime tests.
4. **Removal of `hex`.** Port `HexValueTests` to `HexIn`, including
   the exhaustive 65,536-value property test. Delete `HexValueExpr`, `HexValueExpansion`, the `hex`
   syntax and the hex-value spec's construct. Update the language guide.
5. **Editor navigation:** go to definition and usages for functions and their calls.
6. **CSNitrogen:** generator emits `HexDigit` and `HexIn` into `Lexical.ngr`, then `HexIn(4, IdStart)`. Regenerate, then
   build and test against this checkout.
7. **Verification:** `dotnet build Nitrogen.slnx`, the full `Nitrogen.Tests` suite, snapshot review,
   and both CSNitrogen suites.

Deferred to a follow-up: hover with expansion size, expansion preview, semantic-token roles for
functions, completion, and rename.
