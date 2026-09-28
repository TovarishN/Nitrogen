# Nitrogen declarative typing and lowering — design

**Issue:** 251. **Date:** 2026-09-27. **Status:** written design for review.
**Base:** standalone `main` at `c579dee`.

## 1. Purpose and first proof

Today a `.ngr` grammar and its HIR lowerer record types separately. `Geometry.ngr` compares type names as strings (`symbol.Type = Annotation.Text`, `Symbol?.Type == "Scalar"`, `Type = "Core.Scalar"`), while `GeometryHirLowerer` and `GeometryDefinitionExpander` hard-code `SemanticTypes.Scalar`. The two agree only by convention. Admitted packages have no typing at all: `DeclarativeGrammarPolicy` rejects every semantics block, and each host profile writes C# lowerers for the generated syntax.

This milestone adds declarative clauses that type grammar nodes by `SemanticType` and lower them to typed HIR. Trusted C# modules and admitted packages use the same clauses and the same generic runtime interpreter; packages still contain no C#. The proofs are:

- the trusted Geometry module lowers `box 1 2 3;` through declarative clauses with identical HIR, origins, and executor output, and `GeometryHirLowerer.cs` is removed;
- a declarative package in this repository is admitted end to end with a pure test handler, and packages with a type mismatch or an ungranted operation are rejected.

## 2. Grammar clauses

Two clause forms join the existing `declares` and `references` clauses on syntax rules and alternatives. The grammar below is illustrative; section 6 gives the actual Geometry migration.

```
syntax Box          = "box" Width:Dimension Height:Dimension Depth:Dimension ";"
                      lowers Geometry.BoxMesh(Width, Height, Depth);
syntax Dimension    = Num / ParameterRef;
syntax Num          = Sign:("+" / "-")? Value:Number  lowers literal Core.Scalar this;
syntax Parameter    = Name:Identifier ":" Annotation:TypeName
                      declares parameter Name type Annotation;
syntax ParameterRef = Name:Identifier  references parameter Name;
```

- `lowers Op.Id(Field, ...)` lowers the node to a `HirOperation` for a catalog operation. Arguments are named fields of the rule, in the operation's parameter order.
- `lowers literal Type.Id Field` lowers the node to a `HirConstant` of the named type. The field's tokens (or, for `this`, the node's tokens), without trivia, are parsed as an invariant-culture float, so a separate sign token is included. There is no unit conversion.
- `type Field` or `type Qualified.Name` follows a `declares` clause and gives the declared symbol a type. The field form reads the type name from the field's source text; the qualified form names the type directly.
- In the argument position of a `lowers` clause, a node without its own `lowers` clause is typed as follows. If it carries `references`, it lowers to a `HirSymbolRef` typed by the resolved symbol's `type`. Otherwise, if it has exactly one child that is typed by these rules, it passes that child's type and HIR through; `Dimension` above uses this. Nodes outside any `lowers` argument are never typed, so existing grammars are unaffected.

The grammar front end treats these clauses as syntax only. The bootstrap `GrammarParser` and the self-hosted `Nitrogen.ngr` both accept them, and `SelfHostingTests` hold the two to the same `GrammarModel`. The clauses are `BindingClause` values: kinds `Lowers` and `LowersLiteral` with `Target` and `Arguments`, and `Declares` with an optional `Target` for its type. This reuses the clause loop, span rules, and the existing duplicate-clause check. `GrammarValidator` checks that referenced fields exist, that a rule has at most one `lowers` clause, and that `type Field` names a field of the same rule. The grammar compiler never sees the semantic catalog.

## 3. Generated data and composition

The generator emits each kind's clauses as an immutable `DeclarativeRule` in a new virtual `SyntaxModule.DeclarativeRules` list, alongside the binding tables. A rule holds the local kind, its form (none, operation, or literal), the operation ID or literal type, the argument child indices, and any declared-symbol type (a qualified name or a child index). A list rather than a per-kind lookup lets admission enumerate lowered operations. It is data; no authored C# is emitted.

When `LanguageBuilder` or `ModuleComposer` composes a language, a new `DeclarativeLowering` component in `Nitrogen.Runtime/Semantic` resolves every `DeclarativeRule` against the composed `SemanticCatalog`:

| Code | Condition |
| --- | --- |
| `NM0008` | `lowers` names an operation absent from the catalog |
| `NM0009` | a literal type or fixed `type` name is absent from the catalog |
| `NM0010` | the argument count differs from the operation's parameter count |

A failure prevents the language from being built. A resolved operation rule becomes an ordinary `LoweringRegistration`, so `HirLowering` needs no new path, and `NC0005` still reports a host C# lowerer registered for the same kind.

Type names resolve by exact qualified ID (`Core.Scalar`). An unqualified name (`Scalar`) resolves only if exactly one catalog type has that name.

## 4. Per-file typing and lowering

`DeclarativeTypes`, cached per `FileSemantics`, assigns a `SemanticType` to each declaratively lowered node: an operation's signature result, a literal's declared type, a reference's symbol type, or a pass-through child's type. Its diagnostics are appended to `FileSemantics.Diagnostics()`, so the language service shows them, admission examples count them, and `HirLowering` refuses to lower spans that contain them (`NH0003`).

| Code | Condition |
| --- | --- |
| `NT0001` | an argument's type differs from the operation parameter type; reported at the argument |
| `NT0002` | a `type Field` text names no catalog type, or an unqualified name is ambiguous |
| `NT0003` | literal text does not parse to a finite number |
| `NT0004` | an argument has no declarative type: a referenced symbol has no `type` clause, or a pass-through node has zero or several typed children |

A node whose child is already in error is not reported again. The registered lowerer builds `HirOperation`, `HirConstant`, and `HirSymbolRef` nodes from the same typing results, with a source origin for each node. Only the outermost operation node becomes a root; the registration for a nested operation node returns `null`, so no node is lowered twice. Language service hover shows the node's `SemanticType` through the existing typed-node inspection.

## 5. Admission

`DeclarativeGrammarPolicy` continues to reject semantics blocks and symbol properties and accepts the new clauses. A profile's `Describe` supplies the semantic module (types, signatures, imports) and needs no C# lowerer for declaratively lowered rules. Every operation named by a `lowers` clause must be one the package requested and the profile granted; otherwise admission fails with `NA0006` before examples run. Capability matching, example verification, probes, publication, and lease rules from issues 248–249 are otherwise unchanged. This remains a trusted local gate for human-reviewed packages, not a sandbox.

## 6. Geometry migration

- `Box` keeps its three `Num` fields and gains `lowers Geometry.BoxMesh(Width, Height, Depth)`; `Num` gains `lowers literal Core.Scalar Value`. `TemplateBox` gets no `lowers` clause, because definition bodies are expanded by C#, not lowered as roots. `Num` keeps its `Sign` field and uses `lowers literal Core.Scalar this`. `GeometryHirLowerer.cs` and the string `Type` properties on `Box` and `Num` are removed.
- The `Type` symbol property and `symbol.Type = Annotation.Text` are replaced by `declares parameter Name type Annotation`. `ParameterRef`'s string `GD0001` check is removed; `Parameter`'s `GD0001` already rejects any non-`Scalar` parameter.
- `GE0001` (finite, positive dimension) remains a C# check; trusted modules may combine declarative typing with C# value checks.
- `def`/`make` expansion remains in `GeometryDefinitionExpander` but reads parameter types from `DeclarativeTypes` instead of hard-coding `SemanticTypes.Scalar`. `GD0001` remains.
- `box 1 2 3;` must produce the same HIR, origins, and executor result as before.

## 7. Out of scope

Value constraints in declarative form (they stay in C# checks, host preflight, or handlers); declarative `def`/`make` expansion; non-numeric literals; unit conversion; overload resolution and type variables.

## 8. Tests and delivery

Each step starts with a failing focused test.

- Parser and self-hosting: each clause form in both parsers, with model parity.
- Validator: unknown field, duplicate `lowers`, `type Field` naming another rule's field.
- Generator: a snapshot of the emitted `DeclarativeRules` table.
- Composition: `NM0008`, `NM0009`, `NM0010`, and `NC0005` with a conflicting C# lowerer.
- Typing: a new `Nitrogen.Tests/Grammars/Lowered.ngr` covering operations, literals, pass-through, references, a nested operation producing one root, a `references` node outside any `lowers` argument staying untyped, qualified and unqualified type names, and `NT0001`–`NT0004`. The mismatch case passes a `Core.Scalar` literal to an operation that takes `Units.Angle`.
- Geometry: existing tests pass unchanged; a hover test expects `Geometry.Mesh`.
- Admission, end to end: a declarative package with a pure test handler is accepted and executes; a package whose example has a type mismatch is rejected with `NT0001` in its example diagnostics; a package that lowers an ungranted operation is rejected with `NA0006`, and the previously accepted module stays active.

Verify with `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`; CI runs both.
