# Hex-value token expressions

Status: superseded the same day by [grammar functions](2026-10-17-token-functions-design.md). The built-in was
implemented, then removed before release; CSNitrogen now declares a `HexIn(W, C)` grammar function in its own `Lexical.ngr`. The text
below records the original design.

Original status: requested by the user on 2026-10-17 ("Tier 2: the built-in `hex(width) in Class` expansion").
Implemented the same day. The Nitrogen suite passes, 1811/1811. CSNitrogen regenerated `Lexical.ngr` with the
construct and was built against this working tree. Its tests pass, 754/754 and 3241/3241 oracle.

## Intent

Languages with `\uXXXX` escapes often restrict which code points an escape may name. C# identifiers
accept `\u0041` but not `\u0020`. Today a grammar can express this only with a hand-unrolled trie of
hex-digit classes. CSNitrogen's generated `Lexical.ngr` spends about 640KB in 424 token rules on that
trie. The new construct states the constraint directly:

```ngr
token IdStart      = ['A'..'Z' '_' ...];
token UnicodeStart = "\\u" hex(4) in IdStart / "\\U0000" hex(4) in IdStart;
```

## Contract

`hex(W) in C` is a primary token expression. It matches exactly `W` hexadecimal digits in either case.
The digits' value must name a character in the class `C`.

- `W` is an integer from 1 to 8. Values above `U+FFFF` never match because a class holds UTF-16 code
  units. With `W > 4`, the leading digits must therefore be `0`.
- `C` is an inline character class, which may be negated, or a reference to a token rule in the same
  file. That rule's body must be a class, a reference to such a rule, or a choice of those.
  A negated class means its complement within `U+0000`–`U+FFFF`.
- The construct is a compile-time expansion. Each parser replaces it with ordinary character-class,
  sequence and choice expressions immediately after parsing. Every later stage — validation, first-set
  analysis, code generation and editor features — sees only existing expression forms. The expansion's
  nodes all carry the span of the whole `hex(W) in C` expression.
- The expansion is a digit trie. Sibling digits whose remaining suffixes are equal share one class, and
  a fully covered suffix becomes a run of full hex-digit classes. Alternatives start with disjoint digit
  classes and have the same fixed length, so ordered choice cannot change the match.
- `hex` is contextual. It means this construct only when `(`, an integer, `)` and `in` follow. A rule
  named `hex` stays usable as a reference.

## Diagnostics

Each failure is an `.ngr` syntax error at the stated span; both parsers report it identically.

| Failure | Span |
| --- | --- |
| Width outside 1–8 | the width |
| Class reference to an unknown rule, a syntax rule, or a token rule whose body is not a class | the reference |
| A cyclic class reference | the reference |
| No `W`-digit value names a character in the class | the whole expression |

## Out of scope

Counted repetition (`X{n}`), case-insensitive literals, Unicode category classes, user-defined
parameterized token macros, and class references across files or composed modules.

## Validation

- Bootstrap and self-hosted parsers produce identical expanded models (`SelfHostingTests`).
- Expansion shape tests on small classes, including grouping, full suffixes, negation, references,
  and width 8.
- A generated test grammar matches accepted escapes and rejects excluded ones at runtime.
- CSNitrogen regenerates `Lexical.ngr` with the construct. Its build and tests pass unchanged.
