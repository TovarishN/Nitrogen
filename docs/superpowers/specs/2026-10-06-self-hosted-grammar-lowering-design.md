# Self-hosted grammar lowering: `lowers` clauses in `Nitrogen.ngr`

Status: approved design (2026-10-06), stage 1 of 2.

## Problem

`Nitrogen.ngr` describes `.ngr` itself and defines every `lowers` form, but none of its own rules use
one. No semantic module defines grammar types or operations, and `NgrParser.Language` (the `ngr`
language in the LSP) is built without a catalog, so a `.ngr` file has no typed meaning: no declarative
types, hover, or HIR inspection. `NgrMapper` alone turns the generated syntax into `GrammarModel`.

## Goal and stages

1. **Stage 1 (this spec).** Add a `Grammar` semantic module, give `Nitrogen.ngr` `lowers` clauses so a
   whole `.ngr` file lowers to one typed `Grammar.File` HIR root, and attach the module to the `ngr`
   language. `NgrMapper` stays the model source; a coverage test proves every repository grammar lowers.
2. **Stage 2 (separate spec).** A HIR → `GrammarModel` projector runs beside `NgrMapper` under
   `SelfHostingTests`, then replaces it. The projector owns what clauses cannot express: bootstrap span
   rules, collapsing one-item sequences and choices, unquoting, integer parsing, and mapping errors
   (empty literal, empty character range, precedence too large).

Stage 1 changes no `GrammarModel` and no parse result.

## 1. Framework: `optional text Field`

A new operation-argument form for optional syntax that is only text, such as a bare keyword:

```ngr
syntax Declares = "declares" SymbolKind:Identifier Field:Identifier Sequential:"sequential"? …
  lowers Grammar.Declares(text SymbolKind, text Field, optional text Sequential, …);
```

- **Type.** The operation input must be `Core.Optional<Core.Text>`; otherwise `NT0001` at the argument.
- **Lowering.** A present field lowers to `HirOptional(Core.Text, HirText(spelling))`, where the spelling
  is the field's tokens without trivia (as `text Field`); an absent field (`Empty`) lowers to an empty
  `HirOptional(Core.Text)`. A field containing missing tokens blocks lowering, as `text Field` does.
- **Validation.** `GrammarValidator` requires the field to be optional (`?`), as for `optional T Field`.
- **Syntax.** `optional text Field` is matched before `optional Type Field`; after `optional`, `text`
  is reserved, as `inferred` is after `sequence`. A type named `text` must be module-qualified.
- **Touched code.** Self-hosted grammar (`LowersOptionalTextArgument` ahead of
  `LowersOptionalArgument`), bootstrap `GrammarParser`, `LoweringArgument.OptionalText`,
  `DeclarativeWriter`/generator (`DeclarativeRule.ArgumentOptionalTexts`, emitted only when used so
  existing generated code is unchanged), `DeclarativeTypes` checks and lowering, `GrammarValidator`,
  `GrammarDumper`, `NgrMapper`, and `NgrAssist` roles. `HirProjector` already handles `HirOptional`.

## 2. The `Grammar` semantic module

`Nitrogen.Ngr/GrammarSemantics.cs` exports `GrammarSemantics.Module`: name `Grammar`, imports `Core`,
no lowerers. All lowered text is raw source spelling: quoted literals keep their quotes and escapes, and
C# code is untrimmed. Interpreting it is stage 2's job.

Lowering checks types exactly, so every alternative of a pure choice rule (`Member`, `Clause`,
`SemanticItem`, `Primary`, `LowersArgument`, `KindList`, …) lowers to one shared type.

### Types

| Type | Produced by |
| --- | --- |
| `Grammar.File` | `File` |
| `Grammar.Module` | `Module` |
| `Grammar.Member` | `Using`, `Symbols`, `SymbolProperty`, `Builtin`, `TokenRule`, `SyntaxRule`, `ExtensibleRule`, `Extend` |
| `Grammar.Except` | `ExceptList` |
| `Grammar.End` | `Terminator`, `RuleSemantics` |
| `Grammar.Alternative` | `Alternative` |
| `Grammar.Precedence` | `PrecedenceSpec` |
| `Grammar.Clause` | `Declares`, `References`, `ScopeClause`, `DynamicClause`, `Lowers` |
| `Grammar.Kinds` | `KindGroup`, `SingleKind` |
| `Grammar.LowersForm` | the nine `Lowers…` forms |
| `Grammar.TypeSpec` | `ComputedType`, `FixedType` |
| `Grammar.OperationSpec` | `ComputedOperation`, `FixedOperation` |
| `Grammar.LoweringArgument` | `LowersSequenceArgument`, `LowersOptionalArgument`, `LowersOptionalTextArgument`, `LowersTextArgument`, `FieldArgument` |
| `Grammar.Semantics` | `Semantics` |
| `Grammar.SemanticItem` | `PropertyDecl`, `Check`, `Assignment` |
| `Grammar.Expression` | `Expression`, `Sequence`, `Element`, `Unary`, `Postfix`, `Literal`, `Any`, `CharClass`, `Parenthesized`, `Reference` |
| `Grammar.Tail` | `SeparatorTail`, `GroupClose` |
| `Grammar.ClassItem` | `ClassItem` |

`Core.Text` comes from text-only rules (`Word`, `BuiltinName`, `ExceptWord`, `InKind`, `DeclaredType`,
`AlternativeName`, `Associativity`, `InitializerSpec`, `CheckAt`, `LabelPrefix`, `RangeEnd`,
`RepeatOp`, `PredicateOp`, `PropertyFlag`). Below, `Text` is `Core.Text`, `Seq<T>` is
`Core.Sequence<T>`, and `Opt<T>` is `Core.Optional<T>`; each operation's ID is `Grammar.` plus its name.

### Operations

| Operation | Inputs → result |
| --- | --- |
| `File` | `Seq<Module>` → `File` |
| `Module` | `Text` name, `Seq<Member>` → `Module` |
| `Using` | `Text` module → `Member` |
| `Symbols` | `Seq<Text>` kinds → `Member` |
| `SymbolProperty` | `Text` name, `Kinds`, `Text` type, `Text` default → `Member` |
| `Builtin` | `Text` kind, `Opt<Text>` within, `Seq<Text>` names → `Member` |
| `TokenRule` | `Text` name, `Expression` body, `Opt<Except>` → `Member` |
| `Except` | `Seq<Text>` words → `Except` |
| `SyntaxRule` | `Text` name, `Expression` body, `Seq<Clause>`, `End` → `Member` |
| `Terminator` | () → `End` |
| `RuleSemantics` | `Semantics` → `End` |
| `ExtensibleRule` | `Text` name, `Seq<SemanticItem>` properties, `Seq<Alternative>` → `Member` |
| `Extend` | `Text` target, `Seq<Alternative>` → `Member` |
| `Alternative` | `Opt<Text>` name, `Expression` body, `Opt<Precedence>`, `Seq<Clause>`, `Opt<Semantics>` → `Alternative` |
| `Precedence` | `Text` level, `Opt<Text>` associativity → `Precedence` |
| `Declares` | `Text` kind, `Text` field, `Opt<Text>` sequential, `Opt<Text>` file scope, `Opt<Text>` export, `Opt<Text>` type → `Clause` |
| `References` | `Opt<Text>` question, `Kinds`, `Text` field, `Opt<Text>` within → `Clause` |
| `KindGroup` | `Seq<Text>` → `Kinds` |
| `SingleKind` | `Text` → `Kinds` |
| `Scope`, `Dynamic` | () → `Clause` |
| `Lowers` | `LowersForm` → `Clause` |
| `LowersTemplate`, `LowersExpand` | `Text` field, `Text` list → `LowersForm` |
| `LowersRepeat` | `Text` type, `Text` count, `Text` iterator, `Text` children → `LowersForm` |
| `LowersLiteral`, `LowersText`, `LowersSequence` | `Text` type, `Text` field → `LowersForm` |
| `LowersValue` | `TypeSpec`, `Text` field → `LowersForm` |
| `ComputedType` | `Text` property → `TypeSpec` |
| `FixedType` | `Text` name → `TypeSpec` |
| `LowersReference` | `Text` property, `Opt<Text>` initializer → `LowersForm` |
| `LowersCall` | `OperationSpec`, `Seq<LoweringArgument>` → `LowersForm` |
| `ComputedOperation` | `Opt<Text>` question, `Text` property → `OperationSpec` |
| `FixedOperation` | `Text` name → `OperationSpec` |
| `SequenceArgument`, `OptionalArgument` | `Text` type, `Text` field → `LoweringArgument` |
| `OptionalTextArgument`, `TextArgument`, `FieldArgument` | `Text` field → `LoweringArgument` |
| `Semantics` | `Seq<SemanticItem>` → `Semantics` |
| `PropertyDecl` | `Text` direction, `Seq<Text>` flags, `Text` name, `Text` type, `Text` default → `SemanticItem` |
| `Check` | `Opt<Text>` code, `Text` condition, `Text` message, `Opt<Text>` at → `SemanticItem` |
| `Assignment` | `Text` target, `Text` value → `SemanticItem` |
| `Choice` | `Seq<Expression>` → `Expression` |
| `Sequence` | `Seq<Expression>` → `Expression` |
| `Element` | `Opt<Text>` label, `Expression` → `Expression` |
| `Unary` | `Seq<Text>` prefixes, `Expression` → `Expression` |
| `Postfix` | `Expression`, `Seq<Text>` operators → `Expression` |
| `Literal` | `Text` quoted → `Expression` |
| `Any` | () → `Expression` |
| `CharClass` | `Opt<Text>` negated, `Seq<ClassItem>` → `Expression` |
| `ClassItem` | `Text` first, `Opt<Text>` last → `ClassItem` |
| `Parenthesized` | `Expression` inner, `Tail` → `Expression` |
| `SeparatorTail` | `Expression` separator, `Text` op → `Tail` |
| `GroupClose` | () → `Tail` |
| `Reference` | `Text` name → `Expression` |

`ExtensibleRule` properties use `Grammar.SemanticItem` because `PropertyDecl` also appears in
semantics blocks and has one type; the grammar already restricts that list to properties.

## 3. `Nitrogen.ngr` changes

Every syntax rule except token rules and pure choice rules gets one `lowers` clause matching the table.
Optional keywords use `optional text`; required single tokens use `text`. Where a field cannot lower as
written, the grammar is reshaped without changing the accepted language:

- **Token lists** become lists of text-only rules: `Word = Value:Identifier` (`Symbols`, `KindGroup`),
  `BuiltinName = Value:QualifiedName` (`Builtin`), `ExceptWord = Value:String` (`ExceptList`).
- **Token alternatives** in choice rules become rules: `Literal = Value:String` in `Primary`,
  `SingleKind = Kind:Identifier` in `KindList`, `FieldArgument = Field:Identifier` in `LowersArgument`.
- **Optional keyword groups** become named rules used as optional fields: `ExceptList`
  (`"except" Words:ExceptWord+`), `InKind` (`"in" Kind:Identifier`), `DeclaredType`
  (`"type" Name:QualifiedName`), `AlternativeName` (`Name:Identifier "="`), `PrecedenceSpec`
  (`"precedence" Level:Integer Associativity:Associativity?`), `InitializerSpec`
  (`"initializer" Name:Identifier`), `CheckAt` (`"at" Name:Identifier`), `LabelPrefix`
  (`Name:Identifier ":"`), `RangeEnd` (`".." Last:Char`).
- **Mixed alternatives** become rules: `End:(Terminator / RuleSemantics)`, `Tail:(SeparatorTail /
  GroupClose)`, `Type:(ComputedType / FixedType)` in `LowersValue`, `Operation:(ComputedOperation /
  FixedOperation)` in `LowersCall`.
- **Literal-choice rules** gain a label so they lower as text: `RepeatOp`, `PredicateOp`,
  `PropertyFlag`, `Associativity`. `PropertyDecl.Direction` lowers with `text Direction`.
- `Declares.FileScope:("in" "file")?` stays as written and lowers with `optional text`; only its
  presence is meaningful.

`NgrMapper` and `NgrAssist` are updated to the new generated view shapes. The `GrammarModel`
`NgrMapper` produces, spans included, must not change; `SelfHostingTests` hold it to the bootstrap
parser's model for every repository grammar, including `Nitrogen.ngr` itself. Generated-code snapshots
for `Nitrogen` change and are re-approved.

## 4. Wiring

`NgrParser.Language` is built with `.AddSemantic(GrammarSemantics.Module)`. The `ngr` language entry
in `LspCommand` therefore gets declarative types, hover, typed colouring, and HIR inspection.
`NgrParser.Parse` is unchanged.

## 5. Errors

- Composition (`NM0008`–`NM0010`, `NC0003`–`NC0004`) runs when `NgrParser.Language` is built; a
  mismatch between clauses and the module fails the build of the language and its tests.
- Semantic checks (`NT0001`–`NT0004`) and lowering diagnostics (`NH0001`–`NH0006`) on a valid `.ngr`
  file indicate a bug in the clauses or module. Recovered or partial syntax in the editor reports
  `NH0001` as in any language and does not affect parsing.

## 6. Testing

- **Coverage.** Every `.ngr` file in the repository (`Nitrogen.Ngr`, `Nitrogen.Geometry`, `examples`,
  `Nitrogen.Tests/Grammars`) parses with `NgrParser.Language`, has no semantic diagnostics, and lowers
  to exactly one root of type `Grammar.File` with no lowering diagnostics.
- **Shape.** A small grammar's HIR is checked structurally: a present and an absent optional keyword,
  an optional group, a separated list, a nested choice, a character range, and a semantics block.
- **`optional text`.** Bootstrap and self-hosted parsers agree on the clause; validation rejects a
  required field; checking rejects a non-`Core.Optional<Core.Text>` input; lowering covers present,
  absent, and missing-token fields.
- **Regression.** `SelfHostingTests` unchanged; full `dotnet build Nitrogen.slnx -warnaserror` and
  `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`.

## Out of scope

- HIR → `GrammarModel` projection and removing `NgrMapper` (stage 2).
- Typing rule references across modules (a reference's target rule stays text).
- Interpreting raw text (unquoting, C# trimming, integer values) in HIR; stage 2's projector does it.
