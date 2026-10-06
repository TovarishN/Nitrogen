# Self-hosted grammar projection: HIR → `GrammarModel` replaces `NgrMapper`

Status: implemented (2026-10-06), stage 2 of 2. Builds on
[stage 1](2026-10-06-self-hosted-grammar-lowering-design.md).

## Problem

After stage 1, `Nitrogen.ngr` lowers every grammar to typed `Grammar` HIR, but `NgrParser.Parse` still
builds `GrammarModel` with the hand-written `NgrMapper`, a second syntax walk over the same tree. The
grammar's structure is therefore stated twice: once in the clauses, once in the mapper.

## Goal

`NgrParser.Parse` produces `GrammarModel` from the lowered HIR alone, and `NgrMapper` is deleted. Its
contract is unchanged: for every input the bootstrap `GrammarParser` accepts, the self-hosted result
is identical, spans included; for every input it rejects, the self-hosted parser fails with a
`GrammarCodes.Syntax` diagnostic. Error messages and positions may differ, as today.

`NgrParser.Parse` has only test callers (`SelfHostingTests`); performance is not a goal.

## 1. Framework: syntax-only lowering admission

`HirLowering` admits a node only if its subtree has no recovered syntax (`NH0001`), no unresolved name
(`NH0002`), and no binding or semantic error (`NH0003`). In a `.ngr` file a duplicate declaration (two
`syntax R` in a module) is a binding error, `NB0002`. Both parsers accept such a grammar today, and
`GrammarValidator` reports the duplicate later. HIR-based parsing must keep accepting it.

```csharp
public enum LoweringAdmission
{
    /// <summary>Recovered syntax, unresolved names, and binding or semantic errors block lowering.</summary>
    Full,
    /// <summary>Only recovered syntax blocks lowering; for callers that validate the result themselves.</summary>
    SyntaxOnly,
}
```

- `HirLowering.Lower(FileSemantics, SemanticCatalog, Guid snapshotId, LoweringAdmission admission =
  LoweringAdmission.Full)`; `LoweringContext` gains an `Admission` property (constructor parameter
  defaulting to `Full`) that nested lowering (`LowerNested`, template expansion) inherits.
- `Admit` under `SyntaxOnly` checks only recovery (`NH0001`). Declarative lowering still returns no node
  where types do not fit, so a typing error still yields no root; it is just not pre-empted.
- Every existing caller keeps `Full`: the editor, Geometry, `LowerSelected`.

## 2. `NgrProjector`

`Nitrogen.Ngr/NgrProjector.cs`, `internal sealed class NgrProjector`. Input: the `Grammar.File`
`HirOperation`. Output: `GrammarFile`. It dispatches on `HirOperation.Signature.Id` and reads
`HirText.Value`, `HirOptional.Value`, `HirSequence.Items`, and `Origins[0].Span`. It never reads the
syntax tree.

### Spans

| HIR node | Origin span |
| --- | --- |
| `HirOperation` | its rule node |
| inline `text F` / `optional text F` argument (`HirText`) | the field's token |
| `HirOptional` (any argument) | the field; empty where absent |
| `HirSequence` | the list |
| `HirText` from a `lowers text` rule | that rule's node |

For a `lowers text` wrapper the projector needs the name's own span. The spelled text is exactly one
token, at a fixed end of the wrapper:

| Wrapper | Name at | Name span |
| --- | --- | --- |
| `Word`, `BuiltinName`, `ExceptWord` | whole node | node span |
| `AlternativeName` (`Name "="`), `LabelPrefix` (`Name ":"`) | start | `(node.Start, text.Length)` |
| `InKind`, `DeclaredType`, `RangeEnd`, `InitializerSpec`, `CheckAt` | end | `(node.End − text.Length, text.Length)` |
| `Associativity`, `PropertyFlag`, `PredicateOp`, `RepeatOp` (`this`) | whole node | node span |

Node spans exclude trivia, so a token at a node's end ends where the node does.

### Bootstrap rules the projector reproduces

These come over from `NgrMapper` unchanged in meaning:

- **Collapsing.** A `Choice` of one sequence is that sequence; a `Sequence` of one element is that element;
  an `Element` without a label is its `Unary`; `Unary` wraps its body in one `PredicateExpr` per prefix,
  innermost last; `Postfix` wraps in one `RepeatExpr` per operator. A `Parenthesized` with `GroupClose`
  is its inner expression with the inner span; with `SeparatorTail` it is a `SeparatedListExpr` spanning
  the group.
- **Spans.** Choice and sequence span first to last item; label spans from the label to the inner end;
  a predicate from its operator to the inner end; a repeat from the inner start to the operator end.
- **Alternatives.** The end is the body's end, then the precedence spec's, then the last clause's, then the
  semantics block's, whichever is present last. A nameless alternative whose body is a reference takes the
  reference's last dotted part as an implicit name; otherwise its name is empty.
- **Clauses.** Each kind's span ends at its last present token, as in `NgrMapper.Clauses`, including
  `declares`' order (`Export`, else `FileScope`, else `Sequential`, else the field, then a declared type
  overriding all) and a lowering call ending at its closing parenthesis.
- **Text.** Unquote string and char literals; trim C# code at its end (`CodeText` keeps the start and the
  trimmed length); split an assignment target into self, `Child.Property` or `symbol.Property`.
- **Errors**, raised as `NgrMappingException` and returned as one `GrammarCodes.Syntax` diagnostic: an
  empty literal (in a body or an `except` list), an empty character range, precedence over nine digits,
  an assignment target with more than two parts.

`NgrMappingException` moves into `NgrProjector.cs`.

## 3. `NgrParser.Parse`

1. Parse with `NgrParser.Language`. Failure → one `Syntax` diagnostic, as today.
2. Put the tree in a fresh one-file `Project`; take its `ProjectSemantics` entry.
3. `HirLowering.Lower(file, catalog, Guid.NewGuid(), LoweringAdmission.SyntaxOnly)`.
4. Exactly one root, of type `Grammar.File` → `NgrProjector`. Anything else on a successful parse is a
   bug: throw `InvalidOperationException` naming the first lowering diagnostic's code and message.

## 4. Cutover

- While the projector is built, a differential test compares `NgrProjector` with `NgrMapper` on every
  `SelfHostingTests` input (repository grammars, declaration forms, invalid inputs).
- When they all agree, `NgrParser.Parse` switches to the projector, `NgrMapper.cs` and the differential
  test are deleted, and `SelfHostingTests` alone remain the guarantee.
- `NgrParser`'s summary and `roadmap.md`'s grammar row ("`NgrParser` maps its generated syntax to
  `GrammarModel`") now say it projects lowered HIR. The stage-1 spec's status notes stage 2 is
  implemented. Historical plans are left as written.

## 5. Testing

- **Admission.** A grammar with a duplicate rule lowers under `SyntaxOnly` and is blocked (`NH0003`)
  under `Full`; recovered syntax is blocked (`NH0001`) under both.
- **Parity.** `SelfHostingTests` unchanged. New rows: duplicate rule, duplicate module, and duplicate
  property declarations, accepted by both parsers with identical models.
- **Wrapper spans.** For each wrapper in the table, a grammar whose model's `NameDecl` span equals the
  token's position in the source, checked against the bootstrap parser.
- **Regression.** Full `dotnet build Nitrogen.slnx -warnaserror --no-incremental` and
  `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`.

## Out of scope

- Editor behaviour (stage 1).
- Replacing the bootstrap `GrammarParser`; the generator still needs it to build `Nitrogen.Ngr`.
- Performance of `NgrParser.Parse`.
