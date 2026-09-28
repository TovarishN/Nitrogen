# Declarative Typing and Lowering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `.ngr` clauses type grammar nodes by catalog `SemanticType` and lower them to typed HIR without authored C#, for trusted modules and admitted packages alike (issue 251).

**Architecture:** The grammar front end parses `lowers Op(A, B)`, `lowers literal T F`, and `declares k N type T` into `BindingClause` values. The generator emits them as a `DeclarativeRule` data table on each `SyntaxModule`. `LanguageBuilder` resolves the table against the composed `SemanticCatalog` (`NM0008`–`NM0010`) and registers ordinary `LoweringRegistration`s. A per-file `DeclarativeTypes` computes node types, reports `NT0001`–`NT0004` through `FileSemantics.Diagnostics()`, and builds HIR.

**Tech Stack:** C# / .NET 10, xUnit, Roslyn source generator (`Nitrogen.Generator`), the bootstrap parser (`Nitrogen.Grammar`), and the self-hosted parser (`Nitrogen.Ngr`).

**Spec:** [2026-09-27-declarative-typing-lowering-design.md](../specs/2026-09-27-declarative-typing-lowering-design.md). This plan's refinements, which the spec now matches: the clauses are `BindingClause` kinds (`Lowers`, `LowersLiteral`, and `Declares` with a `Target`); the generated table is `SyntaxModule.DeclarativeRules`; a literal may read `this`, so Geometry keeps its `Sign` field; and `ParameterRef`'s string check is removed because `Parameter`'s `GD0001` already rejects non-`Scalar` parameters.

**Verification commands:** focused tests use `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`. The final gate is `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`.

---

## File map

| File | Change | Responsibility |
| --- | --- | --- |
| `Nitrogen.Grammar/GrammarModel.cs` | modify | `BindingClauseKind.Lowers/LowersLiteral`; `BindingClause.Target/Arguments` |
| `Nitrogen.Grammar/GrammarLexer.cs` | modify | `,` token |
| `Nitrogen.Grammar/GrammarParser.cs` | modify | parse `lowers …` and `declares … type T` |
| `Nitrogen.Grammar/GrammarDumper.cs` | modify | dump the new clauses |
| `Nitrogen.Grammar/GrammarValidator.cs` | modify | field checks, one `lowers` per rule |
| `Nitrogen.Grammar/BindingWriter.cs` | modify | ignore non-binding clauses |
| `Nitrogen.Grammar/DeclarativeWriter.cs` | create | emit the `DeclarativeRules` table |
| `Nitrogen.Grammar/ModuleWriter.cs` | modify | call `DeclarativeWriter` |
| `Nitrogen.Ngr/Nitrogen.ngr`, `Nitrogen.Ngr/NgrMapper.cs` | modify | self-hosted parity |
| `Nitrogen.Runtime/Semantic/DeclarativeRule.cs` | create | generated data record |
| `Nitrogen.Runtime/SyntaxModule.cs` | modify | `DeclarativeRules` virtual |
| `Nitrogen.Runtime/Semantic/DeclarativeLowering.cs` | create | resolve rules against the catalog; type-name lookup |
| `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` | create | per-file typing, diagnostics, HIR |
| `Nitrogen.Runtime/LanguageBuilder.cs`, `Nitrogen.Runtime/Language.cs` | modify | wire resolution into composition |
| `Nitrogen.Runtime/Semantics/FileSemantics.cs` | modify | expose `DeclarativeTypes`; merge diagnostics |
| `Nitrogen.Geometry/Geometry.ngr`, `BoxMeshModule.cs`, `GeometryDefinitionExpander.cs` | modify | migrate to clauses |
| `Nitrogen.Geometry/GeometryHirLowerer.cs` | delete | replaced by `lowers` |
| `Nitrogen.Workspace/Admission/ModuleAdmissionService.cs` | modify | lowered operations must be requested |
| `Nitrogen.Tests/Grammars/Lowered.ngr` | create | test language |
| Tests listed per task | create/modify | |

---

### Task 1: Clause model and bootstrap parser

**Files:**
- Modify: `Nitrogen.Grammar/GrammarModel.cs` (the `BindingClauseKind` enum and `BindingClause` record, around lines 115–137)
- Modify: `Nitrogen.Grammar/GrammarLexer.cs` (`TokenKind` enum; punctuation switch)
- Modify: `Nitrogen.Grammar/GrammarParser.cs` (`IsClauseWord`, `ParseClauses`)
- Modify: `Nitrogen.Grammar/GrammarDumper.cs` (`Dump(BindingClause)`)
- Test: `Nitrogen.Tests/Grammar/GrammarParserTests.cs`

- [ ] **Step 1: Write the failing test.** Append to `GrammarParserTests`:

```csharp
    [Theory]
    [InlineData("syntax R = \"r\" A:X B:Y lowers M.Op(A, B);", "(lowers M.Op(A, B))")]
    [InlineData("syntax R = \"r\" lowers Op();", "(lowers Op())")]
    [InlineData("syntax R = V:X lowers literal Core.Scalar V;", "(lowers literal Core.Scalar V)")]
    [InlineData("syntax R = \"-\"? V:X lowers literal Core.Scalar this;", "(lowers literal Core.Scalar this)")]
    [InlineData("syntax R = \"r\" A:X lowers literal(A);", "(lowers literal(A))")]
    [InlineData("syntax R = N:X T:Y declares v N type T;", "(declares v N type T)")]
    [InlineData("syntax R = N:X declares v N export type Units.Angle;", "(declares v N export type Units.Angle)")]
    public void Declarative_clauses_parse(string rule, string clause)
    {
        var file = ParseOk("syntax module M { symbols { v } " + rule + " }");
        var syntax = (SyntaxRule)file.Modules[0].Rules[0];
        Assert.Equal(clause, GrammarDumper.Dump(Assert.Single(syntax.Clauses)));
    }

    [Fact]
    public void Lowers_clause_spans_its_keyword_to_the_closing_parenthesis()
    {
        const string text = "syntax module M { syntax R = \"r\" A:X lowers M.Op(A) ; }";
        var clause = Assert.Single(((SyntaxRule)ParseOk(text).Modules[0].Rules[0]).Clauses);
        Assert.Equal("lowers M.Op(A)", text.Substring(clause.Span.Start, clause.Span.Length));
        Assert.Equal("M.Op", clause.Target!.Name);
        Assert.Equal(["A"], clause.Arguments.Select(argument => argument.Name));
    }
```

- [ ] **Step 2: Run it and confirm it fails.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarParserTests"`. Expected: the new tests FAIL to compile, because `BindingClause.Target` and `BindingClause.Arguments` don't exist.

- [ ] **Step 3: Extend the model.** In `GrammarModel.cs`, replace the enum and record:

```csharp
public enum BindingClauseKind
{
    Declares,
    References,
    Scope,
    Dynamic,
    Lowers,
    LowersLiteral,
}

/// <summary>A binding clause after a syntax rule or an alternative (issue 237), or a declarative lowering clause (issue 251).</summary>
/// <param name="Kinds">Declares: one kind. References: one or more, in lookup order. Otherwise empty.</param>
/// <param name="Field">A top-level label of the rule's elements, or <c>this</c>; empty for Scope, Dynamic and Lowers.</param>
/// <param name="Optional"><c>references?</c>: counts only when it resolves, and then hides the references inside it.</param>
/// <param name="Export"><c>declares … export</c>: visible project-wide.</param>
/// <param name="Qualifier"><c>references … in K</c> (issue 239): resolve in the scope of the symbol the nearest enclosing reference of kind K names.</param>
/// <param name="Target">Declares: the <c>type</c> field label or qualified type name; Lowers: the operation ID; LowersLiteral: the literal's type (issue 251).</param>
/// <param name="Arguments">Lowers: the argument field labels, in parameter order (issue 251).</param>
public sealed record BindingClause(
    BindingClauseKind Kind,
    EquatableArray<NameDecl> Kinds,
    string Field,
    GrammarSpan FieldSpan,
    bool Optional,
    bool Export,
    GrammarSpan Span,
    NameDecl? Qualifier = null,
    NameDecl? Target = null,
    EquatableArray<NameDecl> Arguments = default);
```

- [ ] **Step 4: Add the comma token.** In `GrammarLexer.cs`, add `Comma,` as the last member of `TokenKind` (after `Caret`), and add `',' => TokenKind.Comma,` to the punctuation `switch` after `'^' => TokenKind.Caret,`.

- [ ] **Step 5: Parse the clauses.** In `GrammarParser.cs`:

Change `IsClauseWord` to:

```csharp
        static bool IsClauseWord(string word) => word is "declares" or "references" or "scope" or "dynamic" or "lowers";
```

In `ParseClauses`, replace the `declares` branch with:

```csharp
                if (AtKeyword("declares"))
                {
                    var keyword = Advance();
                    var kind = Name(Expect(TokenKind.Identifier, "a symbol kind"));
                    var field = Expect(TokenKind.Identifier, "a field label or 'this'");
                    int end = field.End;
                    bool export = false;
                    if (AtKeyword("export"))
                    {
                        end = Advance().End;
                        export = true;
                    }
                    NameDecl? type = null;
                    if (AtKeyword("type"))
                    {
                        Advance();
                        if (!At(TokenKind.Identifier) && !At(TokenKind.QualifiedName)) throw Error("expected a field label or a qualified type name");
                        var name = Advance();
                        type = Name(name);
                        end = name.End;
                    }
                    clauses.Add(new BindingClause(BindingClauseKind.Declares, new[] { kind }, field.Value, field.Span,
                        false, export, GrammarSpan.FromBounds(keyword.Start, end), Target: type));
                }
```

Add this branch before the `scope`/`dynamic` branch:

```csharp
                else if (AtKeyword("lowers"))
                {
                    var keyword = Advance();
                    if (AtKeyword("literal") && Next.Kind is TokenKind.Identifier or TokenKind.QualifiedName)
                    {
                        Advance();
                        var type = Name(Advance());
                        var field = Expect(TokenKind.Identifier, "a field label or 'this'");
                        clauses.Add(new BindingClause(BindingClauseKind.LowersLiteral, default, field.Value, field.Span,
                            false, false, GrammarSpan.FromBounds(keyword.Start, field.End), Target: type));
                    }
                    else
                    {
                        if (!At(TokenKind.Identifier) && !At(TokenKind.QualifiedName)) throw Error("expected an operation name or 'literal'");
                        var operation = Name(Advance());
                        Expect(TokenKind.LParen, "'('");
                        var arguments = new List<NameDecl>();
                        if (!At(TokenKind.RParen))
                        {
                            arguments.Add(Name(Expect(TokenKind.Identifier, "a field label")));
                            while (At(TokenKind.Comma))
                            {
                                Advance();
                                arguments.Add(Name(Expect(TokenKind.Identifier, "a field label")));
                            }
                        }
                        var close = Expect(TokenKind.RParen, "',' or ')'");
                        clauses.Add(new BindingClause(BindingClauseKind.Lowers, default, "", default, false, false,
                            GrammarSpan.FromBounds(keyword.Start, close.End), Target: operation, Arguments: arguments.ToArray()));
                    }
                }
```

- [ ] **Step 6: Dump the clauses.** In `GrammarDumper.cs`, replace `Dump(BindingClause)` with:

```csharp
    public static string Dump(BindingClause clause) => clause.Kind switch
    {
        BindingClauseKind.Declares =>
            $"(declares {clause.Kinds[0].Name} {clause.Field}{(clause.Export ? " export" : "")}{(clause.Target is { } type ? " type " + type.Name : "")})",
        BindingClauseKind.References =>
            $"(references{(clause.Optional ? "?" : "")} {string.Join("|", clause.Kinds.Select(k => k.Name))} {clause.Field}{(clause.Qualifier is { } q ? " in " + q.Name : "")})",
        BindingClauseKind.Scope => "(scope)",
        BindingClauseKind.Lowers => $"(lowers {clause.Target!.Name}({string.Join(", ", clause.Arguments.Select(a => a.Name))}))",
        BindingClauseKind.LowersLiteral => $"(lowers literal {clause.Target!.Name} {clause.Field})",
        _ => "(dynamic)",
    };
```

- [ ] **Step 7: Run the tests and confirm they pass.** Run the same filter. Expected: PASS. Then run `--filter "FullyQualifiedName~Grammar"`. Expected: PASS, apart from the self-hosting tests, which still pass because no existing grammar uses the new words.

- [ ] **Step 8: Commit.**

```bash
git add Nitrogen.Grammar Nitrogen.Tests/Grammar/GrammarParserTests.cs
git commit -m "Parse declarative lowers and type clauses (issue 251)"
```

---

### Task 2: Self-hosted grammar parity

**Files:**
- Modify: `Nitrogen.Ngr/Nitrogen.ngr`
- Modify: `Nitrogen.Ngr/NgrMapper.cs` (`Clauses`)
- Modify: `Nitrogen.Tests/Grammar/Snapshots/Recovery/Nitrogen.*` (regenerated)
- Test: `Nitrogen.Tests/Grammar/SelfHostingTests.cs`

- [ ] **Step 1: Write the failing tests.** In `SelfHostingTests`, add to the `Every_expression_form_maps_to_the_bootstrap_model` data:

```csharp
    [InlineData("lowersX literalY")]
```

Add to the module-level theory (the one with the `symbols`/`builtin` rows):

```csharp
    [InlineData("syntax module M { symbols { v } syntax R = \"r\" N:A T:B declares v N type T; syntax S = N:A declares v N export type Units.Angle; }")]
    [InlineData("syntax module M { syntax R = \"r\" A:X B:Y lowers M.Op(A, B); syntax S = V:X lowers literal Core.Scalar V { out T : int = 0; } syntax U = \"u\" lowers Op(); syntax W = \"w\" A:X lowers literal(A); }")]
    [InlineData("syntax module M { extensible syntax E { | N = \"-\"? V:X lowers literal Core.Scalar this | P = L:E \"+\" R:E precedence 6 left lowers M.Add(L, R) } }")]
```

- [ ] **Step 2: Run them and confirm they fail.** Run `--filter "FullyQualifiedName~SelfHostingTests"`. Expected: the three new module rows FAIL, because the self-hosted parser rejects `lowers` and `type`.

- [ ] **Step 3: Extend `Nitrogen.ngr`.** Change these rules:

```
  syntax Clause         = Declares / References / ScopeClause / DynamicClause / Lowers;
  syntax Declares       = "declares" SymbolKind:Identifier Field:Identifier Export:"export"? Type:("type" QualifiedName)?;
```

Add after `DynamicClause`:

```
  // Declarative typing and lowering (issue 251).
  syntax Lowers         = "lowers" Form:(LowersLiteral / LowersCall);
  syntax LowersLiteral  = "literal" Type:QualifiedName Field:Identifier;
  syntax LowersCall     = Operation:QualifiedName "(" Arguments:(Identifier; ",")* Close:")";
```

Add `!"lowers"` to `Reference`:

```
  syntax Reference      = !"precedence" !"except" !"declares" !"references" !"scope" !"dynamic" !"lowers" Name:QualifiedName  references? rule Name;
```

- [ ] **Step 4: Map the clauses.** In `NgrMapper.Clauses`, replace the `Declares` branch with:

```csharp
            if (kind == NitrogenKinds.Declares)
            {
                var d = Cast<DeclaresNode>(node);
                int end = d.Export.HasValue ? d.Export.Value.Span.End : d.Field.Span.End;
                NameDecl? type = null;
                if (d.Type.HasValue)
                {
                    var name = d.Type.Value.Child(1);
                    type = new NameDecl(name.ToString(), Span(name.Span));
                    end = name.Span.End;
                }
                clauses.Add(new BindingClause(BindingClauseKind.Declares, new[] { Name(d.SymbolKind) }, d.Field.ToString(),
                    Span(d.Field.Span), false, d.Export.HasValue, GrammarSpan.FromBounds(d.Span.Start, end), Target: type));
            }
```

Add this branch before the final `else`:

```csharp
            else if (kind == NitrogenKinds.Lowers)
            {
                var l = Cast<LowersNode>(node);
                if (l.Form.Kind == NitrogenKinds.LowersLiteral)
                {
                    var literal = Cast<LowersLiteralNode>(l.Form);
                    clauses.Add(new BindingClause(BindingClauseKind.LowersLiteral, default, literal.Field.ToString(),
                        Span(literal.Field.Span), false, false, GrammarSpan.FromBounds(l.Span.Start, literal.Field.Span.End),
                        Target: new NameDecl(literal.Type.ToString(), Span(literal.Type.Span))));
                }
                else
                {
                    var call = Cast<LowersCallNode>(l.Form);
                    var arguments = new List<NameDecl>();
                    foreach (var argument in call.Arguments) arguments.Add(Name(argument));
                    clauses.Add(new BindingClause(BindingClauseKind.Lowers, default, "", default, false, false,
                        GrammarSpan.FromBounds(l.Span.Start, call.Close.Span.End),
                        Target: new NameDecl(call.Operation.ToString(), Span(call.Operation.Span)), Arguments: arguments.ToArray()));
                }
            }
```

If the generated view exposes `Form` or `Operation` under a different member type, follow the existing patterns: `Cast<T>(node)` for rule nodes, `.ToString()`/`.Span` for `QualifiedName` fields (as `u.Module` is used), and `foreach` over separated `Identifier` lists (as `KindGroupNode.Kinds` is used).

- [ ] **Step 5: Regenerate the commit-point snapshot for the changed `Nitrogen.ngr`.** Run `NITROGEN_UPDATE_SNAPSHOTS=1 dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~CommitSnapshotTests"`, then `git diff Nitrogen.Tests/Grammar/Snapshots/Recovery`. Expected: the only changes are commit points for the new `Declares`, `Lowers`, `LowersLiteral`, and `LowersCall` rules and the `Reference` predicate.

- [ ] **Step 6: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~SelfHostingTests|FullyQualifiedName~NgrGrammarTests|FullyQualifiedName~CommitSnapshotTests"`. Expected: PASS.

- [ ] **Step 7: Commit.**

```bash
git add Nitrogen.Ngr Nitrogen.Tests/Grammar
git commit -m "Map declarative clauses in the self-hosted grammar (issue 251)"
```

---

### Task 3: Validator checks and binding-table isolation

**Files:**
- Modify: `Nitrogen.Grammar/GrammarValidator.cs` (`CheckClauses`, `Keyword`)
- Modify: `Nitrogen.Grammar/BindingWriter.cs` (`Write`, `Clauses`, `Rule`)
- Test: `Nitrogen.Tests/Grammar/GrammarValidatorTests.cs`

- [ ] **Step 1: Write the failing tests.** Append to `GrammarValidatorTests`:

```csharp
    [Fact]
    public void Lowers_arguments_must_be_labels()
    {
        var d = Single(GrammarCodes.UnknownBindingField, "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers M.Op(A, B); }");
        Assert.Contains("'B'", d.Message);
    }

    [Fact]
    public void Lowers_literal_field_must_be_a_label_or_this()
    {
        Single(GrammarCodes.UnknownBindingField, "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers literal Core.Scalar Z; }");
        Assert.Empty(Validate("syntax module M { token T = \"t\"; syntax R = \"-\" A:T lowers literal Core.Scalar this; }"));
    }

    [Fact]
    public void A_rule_takes_one_lowers_clause()
    {
        var d = Single(GrammarCodes.DuplicateClause,
            "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers M.Op(A) lowers literal Core.Scalar A; }");
        Assert.Contains("'lowers'", d.Message);
    }

    [Fact]
    public void Declared_type_is_a_label_or_a_qualified_name()
    {
        Single(GrammarCodes.UnknownBindingField, "syntax module M { symbols { v } token T = \"t\"; syntax R = N:T declares v N type Missing; }");
        Assert.Empty(Validate("syntax module M { symbols { v } token T = \"t\"; syntax R = N:T K:T declares v N type K; }"));
        Assert.Empty(Validate("syntax module M { symbols { v } token T = \"t\"; syntax R = N:T declares v N type Units.Angle; }"));
    }

    [Fact]
    public void Lowers_only_rule_emits_no_binding_table()
    {
        var result = GrammarCompiler.Compile([new GrammarInput("m.ngr",
            "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers M.Op(A); }", "N")]);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("GetBinding", Assert.Single(result.Sources).Code);
    }
```

- [ ] **Step 2: Run them and confirm they fail.** Run `--filter "FullyQualifiedName~GrammarValidatorTests"`. Expected: the five new tests FAIL. The field and duplicate checks don't exist yet, and the lowers-only rule emits `GetBinding` with `dynamic` set to true.

- [ ] **Step 3: Validate.** In `GrammarValidator.CheckClauses`, replace the `seen` loop with:

```csharp
            var seen = new HashSet<BindingClauseKind>();
            foreach (var clause in clauses)
            {
                var key = clause.Kind == BindingClauseKind.LowersLiteral ? BindingClauseKind.Lowers : clause.Kind;
                if (!seen.Add(key))
                    Report(GrammarCodes.DuplicateClause, $"a rule takes at most one '{Keyword(clause.Kind)}' clause", clause.Span, module);
                foreach (var kind in clause.Kinds)
                    if (!kinds.Contains(kind.Name))
                        Report(GrammarCodes.UnknownSymbolKind, $"unknown symbol kind '{kind.Name}'", kind.Span, module);
                if (clause.Qualifier is { } qualifier && !kinds.Contains(qualifier.Name))
                    Report(GrammarCodes.UnknownSymbolKind, $"unknown symbol kind '{qualifier.Name}'", qualifier.Span, module);
                if (clause.Kind is BindingClauseKind.Declares or BindingClauseKind.References or BindingClauseKind.LowersLiteral
                    && clause.Field != "this" && !labels.Contains(clause.Field))
                    Report(GrammarCodes.UnknownBindingField,
                        $"'{clause.Field}' is not a label of this rule's elements; name a labeled element or 'this'", clause.FieldSpan, module);
                foreach (var argument in clause.Arguments)
                    if (!labels.Contains(argument.Name))
                        Report(GrammarCodes.UnknownBindingField,
                            $"'{argument.Name}' is not a label of this rule's elements; name a labeled element", argument.Span, module);
                if (clause.Kind == BindingClauseKind.Declares && clause.Target is { } type
                    && type.Name.IndexOf('.') < 0 && !labels.Contains(type.Name))
                    Report(GrammarCodes.UnknownBindingField,
                        $"'{type.Name}' is not a label of this rule's elements; name a labeled element or a qualified type", type.Span, module);
            }
```

Change `Keyword` to:

```csharp
        static string Keyword(BindingClauseKind kind) => kind switch
        {
            BindingClauseKind.Declares => "declares",
            BindingClauseKind.References => "references",
            BindingClauseKind.Scope => "scope",
            BindingClauseKind.Lowers or BindingClauseKind.LowersLiteral => "lowers",
            _ => "dynamic",
        };
```

- [ ] **Step 4: Isolate binding tables.** In `BindingWriter.cs`, make `Clauses` internal (Task 4 reuses it) and add a binding-only view:

```csharp
    internal static EquatableArray<BindingClause> Clauses(KindInfo kind) =>
        kind.Alternative is { } alternative ? alternative.Clauses
        : kind.Rule is SyntaxRule rule ? rule.Clauses
        : default;

    static BindingClause[] BindingClauses(KindInfo kind) => Clauses(kind)
        .Where(c => c.Kind is BindingClauseKind.Declares or BindingClauseKind.References or BindingClauseKind.Scope or BindingClauseKind.Dynamic)
        .ToArray();
```

In `Write`, change `hasClauses` to `info.Kinds.Any(k => BindingClauses(k).Length > 0)`. In `Rule`, change the first two lines to:

```csharp
        var clauses = BindingClauses(kind);
        if (clauses.Length == 0) return "null";
```

- [ ] **Step 5: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~GrammarValidatorTests|FullyQualifiedName~GeneratedSnapshotTests"`. Expected: PASS, with the existing snapshots unchanged.

- [ ] **Step 6: Commit.**

```bash
git add Nitrogen.Grammar Nitrogen.Tests/Grammar/GrammarValidatorTests.cs
git commit -m "Validate declarative clause fields (issue 251)"
```

---

### Task 4: Generated `DeclarativeRules` table and the `Lowered` test language

**Files:**
- Create: `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`
- Modify: `Nitrogen.Runtime/SyntaxModule.cs` (after `SymbolProperties`)
- Create: `Nitrogen.Grammar/DeclarativeWriter.cs`
- Modify: `Nitrogen.Grammar/ModuleWriter.cs` (after `BindingWriter.Write(b, info);`)
- Create: `Nitrogen.Tests/Grammars/Lowered.ngr`
- Modify: `Nitrogen.Tests/Nitrogen.Tests.csproj`, `Nitrogen.Tests/Grammar/GrammarCompilerTests.cs`, `Nitrogen.Tests/Grammar/GeneratedSnapshotTests.cs`, `Nitrogen.Tests/Grammar/NgrGrammarTests.cs`
- Create: `Nitrogen.Tests/Grammar/Snapshots/Lowered.g.cs.txt` (generated)
- Test: `Nitrogen.Tests/Semantic/DeclarativeRuleTableTests.cs`

- [ ] **Step 1: Add the test language.** Create `Nitrogen.Tests/Grammars/Lowered.ngr`:

```
// Declarative typing and lowering (issue 251). Test.Add takes two Core.Scalar values and
// Test.Turn one Units.Angle; inputs are typed by annotation, 'fixed' by a qualified type,
// and 'plain' not at all. 'wrap' passes its one typed child through.
syntax module Lowered
{
  symbols { input }

  token Digits = ['0'..'9']+;
  token Number = Digits ("." Digits)? ("e" Digits)?;
  token Word   = ['a'..'z' 'A'..'Z' '_'] ['a'..'z' 'A'..'Z' '0'..'9' '_' '.']*;
  token Name   = ['a'..'z' 'A'..'Z' '_'] ['a'..'z' 'A'..'Z' '0'..'9' '_']* except "input" "fixed" "plain" "add" "turn" "wrap";

  syntax File      = Items:Item*;
  syntax Item      = Input / Fixed / Plain / Statement;
  syntax Input     = "input" Name:Name ":" Annotation:Word ";"  declares input Name type Annotation;
  syntax Fixed     = "fixed" Name:Name ";"  declares input Name type Units.Angle;
  syntax Plain     = "plain" Name:Name ";"  declares input Name;
  syntax Statement = Body:Value ";";
  syntax Value     = Add / Turn / Wrap / Num / Ref;
  syntax Add       = "add" "(" Left:Value "," Right:Value ")"  lowers Test.Add(Left, Right);
  syntax Turn      = "turn" "(" Amount:Value ")"  lowers Test.Turn(Amount);
  syntax Wrap      = "wrap" "(" Inner:Value ")";
  syntax Num       = Text:Number  lowers literal Core.Scalar Text;
  syntax Ref       = Name:Name  references input Name;
}
```

In `Nitrogen.Tests.csproj`, add `<AdditionalFiles Include="Grammars\Lowered.ngr" Namespace="Nitrogen.Tests.Lowered" />` after the `TypedExtra.ngr` entry. In `GrammarCompilerTests.TestInputs()`, add `Input("Lowered.ngr", "Nitrogen.Tests.Lowered"),` as the last entry, and append `"Lowered.g.cs"` to the expected array in `Test_grammars_compile_to_one_source_per_module`. Append `"Lowered.g.cs"` to `GeneratedSnapshotTests.HintNames()` and `"Lowered.ngr"` to `NgrGrammarTests.GrammarFiles()`.

- [ ] **Step 2: Write the failing table test.** Create `Nitrogen.Tests/Semantic/DeclarativeRuleTableTests.cs`:

```csharp
using Nitrogen.Semantic;
using Nitrogen.Tests.Lowered;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeRuleTableTests
{
    [Fact]
    public void Generated_table_lists_lowering_and_typed_declarations()
    {
        var rules = LoweredModule.Instance.DeclarativeRules.ToDictionary(rule => rule.LocalKind);

        var add = rules[LoweredModule.LAdd];
        Assert.Equal(DeclarativeForm.Operation, add.Form);
        Assert.Equal("Test.Add", add.Target);
        Assert.Equal([2, 4], add.Arguments);

        var num = rules[LoweredModule.LNum];
        Assert.Equal(DeclarativeForm.Literal, num.Form);
        Assert.Equal("Core.Scalar", num.Target);
        Assert.Equal([0], num.Arguments);

        var input = rules[LoweredModule.LInput];
        Assert.Equal(DeclarativeForm.None, input.Form);
        Assert.Null(input.DeclaredType);
        Assert.Equal(3, input.DeclaredTypeChild);

        var fixedInput = rules[LoweredModule.LFixed];
        Assert.Equal("Units.Angle", fixedInput.DeclaredType);
        Assert.Equal(-1, fixedInput.DeclaredTypeChild);

        Assert.False(rules.ContainsKey(LoweredModule.LPlain));
        Assert.False(rules.ContainsKey(LoweredModule.LWrap));
    }

    [Fact]
    public void Modules_without_clauses_have_no_rules() =>
        Assert.Empty(Nitrogen.Tests.Scopes.ScopesModule.Instance.DeclarativeRules);
}
```

- [ ] **Step 3: Run it and confirm it fails.** Run `--filter "FullyQualifiedName~DeclarativeRuleTableTests"`. Expected: a compile FAIL, because `DeclarativeRules` and `DeclarativeForm` don't exist.

- [ ] **Step 4: Add the runtime record.** Create `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`:

```csharp
namespace Nitrogen.Semantic;

public enum DeclarativeForm
{
    None,
    Operation,
    Literal,
}

/// <summary>One syntax kind's declarative typing and lowering clauses (issue 251), emitted by the generator as data.</summary>
public sealed class DeclarativeRule
{
    public DeclarativeRule(int localKind, DeclarativeForm form, string? target, int[] arguments,
        string? declaredType, int declaredTypeChild)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        LocalKind = localKind;
        Form = form;
        Target = target;
        Arguments = Array.AsReadOnly((int[])arguments.Clone());
        DeclaredType = declaredType;
        DeclaredTypeChild = declaredTypeChild;
    }

    public int LocalKind { get; }

    public DeclarativeForm Form { get; }

    /// <summary>Operation: the operation ID. Literal: the type name. None: null.</summary>
    public string? Target { get; }

    /// <summary>Operation: argument child indices in parameter order. Literal: one child index, -1 for the node itself.</summary>
    public IReadOnlyList<int> Arguments { get; }

    /// <summary>A qualified type the declared symbol has; null when absent or read from <see cref="DeclaredTypeChild"/>.</summary>
    public string? DeclaredType { get; }

    /// <summary>The child whose text names the declared symbol's type; -1 when absent.</summary>
    public int DeclaredTypeChild { get; }
}
```

In `Nitrogen.Runtime/SyntaxModule.cs`, after the `SymbolProperties` virtual, add:

```csharp
    /// <summary>Declarative typing and lowering clauses (issue 251); empty when the grammar has none.</summary>
    public virtual IReadOnlyList<Nitrogen.Semantic.DeclarativeRule> DeclarativeRules => [];
```

- [ ] **Step 5: Emit the table.** Create `Nitrogen.Grammar/DeclarativeWriter.cs`:

```csharp
using System.Text;

namespace Nitrogen.Grammar;

/// <summary>
/// Emits a module's declarative typing and lowering table (issue 251): one <c>DeclarativeRule</c>
/// per kind with a <c>lowers</c> clause or a typed declaration. A module without them emits nothing.
/// </summary>
internal static class DeclarativeWriter
{
    const string RuleType = "global::Nitrogen.Semantic.DeclarativeRule";
    const string FormType = "global::Nitrogen.Semantic.DeclarativeForm";

    public static void Write(StringBuilder b, ModuleInfo info)
    {
        var entries = info.Kinds.Select(kind => (Kind: kind, Text: Entry(kind))).Where(entry => entry.Text is not null).ToList();
        if (entries.Count == 0) return;
        b.Append("    static readonly ").Append(RuleType).Append("[] s_declarative =\n    {\n");
        foreach (var (kind, text) in entries) b.Append("        ").Append(text).Append(", // ").Append(kind.Name).Append('\n');
        b.Append("    };\n\n")
            .Append("    public override global::System.Collections.Generic.IReadOnlyList<").Append(RuleType)
            .Append("> DeclarativeRules => s_declarative;\n\n");
    }

    static string? Entry(KindInfo kind)
    {
        var clauses = BindingWriter.Clauses(kind);
        var lowers = clauses.FirstOrDefault(c => c.Kind is BindingClauseKind.Lowers or BindingClauseKind.LowersLiteral);
        var declares = clauses.FirstOrDefault(c => c.Kind == BindingClauseKind.Declares && c.Target is not null);
        if (lowers is null && declares is null) return null;

        var elements = SyntaxCodeWriter.Elements(kind.Alternative?.Body ?? ((SyntaxRule)kind.Rule!).Body);
        string form = "None", target = "null", arguments = "new int[0]";
        if (lowers is { Kind: BindingClauseKind.Lowers })
        {
            form = "Operation";
            target = CSharpText.Literal(lowers.Target!.Name);
            arguments = Ints(lowers.Arguments.Select(a => BindingWriter.ChildIndex(elements, a.Name)));
        }
        else if (lowers is not null)
        {
            form = "Literal";
            target = CSharpText.Literal(lowers.Target!.Name);
            arguments = Ints([BindingWriter.ChildIndex(elements, lowers.Field)]);
        }

        string declaredType = "null";
        int declaredChild = -1;
        if (declares?.Target is { } type)
        {
            if (type.Name.IndexOf('.') >= 0) declaredType = CSharpText.Literal(type.Name);
            else declaredChild = BindingWriter.ChildIndex(elements, type.Name);
        }
        return $"new({kind.Local}, {FormType}.{form}, {target}, {arguments}, {declaredType}, {declaredChild})";
    }

    static string Ints(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "new int[0]" : "new[] { " + string.Join(", ", list) + " }";
    }
}
```

In `ModuleWriter.cs`, after `BindingWriter.Write(b, info);`, add `DeclarativeWriter.Write(b, info);`.

- [ ] **Step 6: Generate the snapshot.** Run `NITROGEN_UPDATE_SNAPSHOTS=1 dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GeneratedSnapshotTests"`. Open `Nitrogen.Tests/Grammar/Snapshots/Lowered.g.cs.txt` and check that `s_declarative` has exactly the entries `Input`, `Fixed`, `Add`, `Turn`, `Num`, with no `GetBinding` entry marked dynamic. Check that `git diff Nitrogen.Tests/Grammar/Snapshots` changes no existing snapshot.

- [ ] **Step 7: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~DeclarativeRuleTableTests|FullyQualifiedName~Grammar"`. Expected: PASS.

- [ ] **Step 8: Commit.**

```bash
git add Nitrogen.Runtime Nitrogen.Grammar Nitrogen.Tests
git commit -m "Generate declarative rule tables (issue 251)"
```

---

### Task 5: Resolve declarative rules at composition

**Files:**
- Create: `Nitrogen.Runtime/Semantic/DeclarativeLowering.cs`
- Modify: `Nitrogen.Runtime/LanguageBuilder.cs` (`TryBuild`, `BuildSyntax`)
- Modify: `Nitrogen.Runtime/Language.cs` (constructor and property)
- Test: `Nitrogen.Tests/Semantic/DeclarativeCompositionTests.cs`

- [ ] **Step 1: Write the failing tests.** Create `Nitrogen.Tests/Semantic/DeclarativeCompositionTests.cs`:

```csharp
using Nitrogen.Semantic;
using Nitrogen.Tests.Lowered;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeCompositionTests
{
    internal static readonly SemanticType Angle = SemanticType.Named("Units", "Angle");
    internal static readonly OperationSignature Add = new("Test.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    internal static readonly OperationSignature Turn = new("Test.Turn", Angle, Angle);

    internal static SemanticModule Units() => new("Units", [], [Angle], []);
    internal static SemanticModule Test(params OperationSignature[] operations) => new("Test", ["Units"], [], operations);

    internal static Language Build(params SemanticModule[] extra)
    {
        var builder = new LanguageBuilder().Add(LoweredModule.Instance).AddSemantic(Units()).AddSemantic(Test(Add, Turn));
        foreach (var module in extra) builder.AddSemantic(module);
        return builder.Build();
    }

    static IReadOnlyList<CompositionDiagnostic> Fail(params SemanticModule[] modules)
    {
        var builder = new LanguageBuilder().Add(LoweredModule.Instance);
        foreach (var module in modules) builder.AddSemantic(module);
        Assert.False(builder.TryBuild(out var language, out var diagnostics));
        Assert.Null(language);
        return diagnostics;
    }

    [Fact]
    public void Operation_rules_register_lowerers()
    {
        var catalog = Build().SemanticCatalog;
        Assert.Equal("Test.Add", Assert.Single(catalog.LowerersFor(LoweredKinds.Add)).OperationId);
        Assert.Equal("Test.Turn", Assert.Single(catalog.LowerersFor(LoweredKinds.Turn)).OperationId);
        Assert.Empty(catalog.LowerersFor(LoweredKinds.Num));
    }

    [Fact]
    public void Unknown_operation_is_NM0008()
    {
        var diagnostic = Assert.Single(Fail(Units(), Test(Add)));
        Assert.Equal("NM0008", diagnostic.Code);
        Assert.Contains("Test.Turn", diagnostic.Message);
    }

    [Fact]
    public void Unknown_declared_type_is_NM0009()
    {
        var diagnostic = Assert.Single(Fail(new SemanticModule("Test", [], [],
            [Add, new OperationSignature("Test.Turn", SemanticTypes.Scalar, SemanticTypes.Scalar)])));
        Assert.Equal("NM0009", diagnostic.Code);
        Assert.Contains("Units.Angle", diagnostic.Message);
    }

    [Fact]
    public void Argument_count_mismatch_is_NM0010()
    {
        var diagnostic = Assert.Single(Fail(Units(), Test(Add, new OperationSignature("Test.Turn", Angle, Angle, Angle))));
        Assert.Equal("NM0010", diagnostic.Code);
    }

    [Fact]
    public void Declarative_and_host_lowerer_for_one_kind_is_NC0005()
    {
        var host = new LoweringRegistration(LoweredKinds.Add, Add.Id, (_, _) => null);
        var diagnostic = Assert.Single(Fail(Units(), new SemanticModule("Test", ["Units"], [], [Add, Turn], [host])));
        Assert.Equal("NC0005", diagnostic.Code);
    }

    [Fact]
    public void Language_without_rules_builds_unchanged()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Scopes.ScopesModule.Instance).Build();
        Assert.True(language.Declarative.IsEmpty);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `--filter "FullyQualifiedName~DeclarativeCompositionTests"`. Expected: a compile FAIL, because `Language.Declarative` doesn't exist.

- [ ] **Step 3: Implement resolution.** Create `Nitrogen.Runtime/Semantic/DeclarativeLowering.cs`:

```csharp
namespace Nitrogen.Semantic;

internal enum TypeLookup
{
    Found,
    Missing,
    Ambiguous,
}

/// <summary>A language's declarative rules (issue 251), resolved against its semantic catalog.</summary>
public sealed class DeclarativeLowering
{
    internal sealed record ResolvedRule(DeclarativeRule Rule, OperationSignature? Operation,
        SemanticType? LiteralType, SemanticType? DeclaredType);

    internal static readonly DeclarativeLowering Empty = new(new Dictionary<int, ResolvedRule>());

    readonly IReadOnlyDictionary<int, ResolvedRule> _rules;

    DeclarativeLowering(Dictionary<int, ResolvedRule> rules) => _rules = rules;

    public bool IsEmpty => _rules.Count == 0;

    internal ResolvedRule? RuleFor(int kind) => _rules.TryGetValue(kind, out var rule) ? rule : null;

    /// <summary>An exact qualified ID, or an unqualified name that exactly one catalog type has.</summary>
    internal static TypeLookup TryResolveType(SemanticCatalog catalog, string name, out SemanticType? type)
    {
        if (catalog.Types.TryGetValue(name, out type)) return TypeLookup.Found;
        type = null;
        if (name.Contains('.')) return TypeLookup.Missing;
        var matches = catalog.Types.Values.Where(candidate => candidate.Name == name).ToArray();
        if (matches.Length == 1)
        {
            type = matches[0];
            return TypeLookup.Found;
        }
        return matches.Length == 0 ? TypeLookup.Missing : TypeLookup.Ambiguous;
    }

    internal static bool TryResolve(IReadOnlyList<SyntaxModule> modules, SemanticCatalog catalog,
        out DeclarativeLowering? lowering, out SemanticModule? registrations,
        out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        var errors = new List<CompositionDiagnostic>();
        var rules = new Dictionary<int, ResolvedRule>();
        var lowerers = new List<LoweringRegistration>();
        foreach (var module in modules)
            foreach (var rule in module.DeclarativeRules)
            {
                int kind = module.KindBase | rule.LocalKind;
                string where = $"'{module.Name}.{module.GetKindName(rule.LocalKind)}'";
                OperationSignature? operation = null;
                SemanticType? literal = null, declared = null;
                if (rule.Form == DeclarativeForm.Operation)
                {
                    if (!catalog.Operations.TryGetValue(rule.Target!, out operation))
                        errors.Add(new CompositionDiagnostic("NM0008", [module.Name],
                            $"{where} lowers to operation '{rule.Target}', which the semantic catalog does not export."));
                    else if (operation.Inputs.Count != rule.Arguments.Count)
                    {
                        errors.Add(new CompositionDiagnostic("NM0010", [module.Name],
                            $"{where} passes {rule.Arguments.Count} arguments to '{rule.Target}', which takes {operation.Inputs.Count}."));
                        operation = null;
                    }
                }
                else if (rule.Form == DeclarativeForm.Literal &&
                         TryResolveType(catalog, rule.Target!, out literal) != TypeLookup.Found)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} has literal type '{rule.Target}', which the semantic catalog does not export."));
                if (rule.DeclaredType is { } fixedType && TryResolveType(catalog, fixedType, out declared) != TypeLookup.Found)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} declares type '{fixedType}', which the semantic catalog does not export."));

                rules[kind] = new ResolvedRule(rule, operation, literal, declared);
                if (operation is not null)
                    lowerers.Add(new LoweringRegistration(kind, operation.Id,
                        (context, node) => context.File.DeclarativeTypes.LowerRoot(context, node)));
            }

        diagnostics = errors;
        if (errors.Count > 0)
        {
            lowering = null;
            registrations = null;
            return false;
        }
        lowering = rules.Count == 0 ? Empty : new DeclarativeLowering(rules);
        registrations = lowerers.Count == 0 ? null : new SemanticModule("Nitrogen.Declarative", [], [], [], lowerers);
        return true;
    }
}
```

`context.File.DeclarativeTypes.LowerRoot` is added in Task 6. So that this task compiles on its own, add the property and a stub now. In `FileSemantics`, add `DeclarativeTypes? _declarative;` and

```csharp
    /// <summary>Types from the language's declarative clauses (issue 251).</summary>
    public Nitrogen.Semantic.DeclarativeTypes DeclarativeTypes =>
        _declarative ??= new Nitrogen.Semantic.DeclarativeTypes(this, _language.Declarative, _language.SemanticCatalog);
```

Then create `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` as this stub, which Task 6 replaces:

```csharp
using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

public sealed class DeclarativeTypes
{
    internal DeclarativeTypes(FileSemantics file, DeclarativeLowering lowering, SemanticCatalog catalog) { }

    internal HirNode? LowerRoot(LoweringContext context, int node) => null;
}
```

- [ ] **Step 4: Wire it into the build.** In `Language.cs`, add a `DeclarativeLowering declarative` last parameter to the internal constructor, assign `Declarative = declarative;`, and add:

```csharp
    /// <summary>The composed declarative rules (issue 251).</summary>
    public DeclarativeLowering Declarative { get; }
```

Confirm `LanguageBuilder` is the only caller with `git grep -n "new Language(" -- '*.cs'`. In `LanguageBuilder.cs`, replace `TryBuild` and `BuildSyntax` with:

```csharp
    public bool TryBuild(out Language? language, out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        language = null;
        var catalog = SemanticCatalog.Compose(_semanticModules, out diagnostics);
        if (catalog is null) return false;
        if (!DeclarativeLowering.TryResolve(_modules, catalog, out var declarative, out var registrations, out diagnostics))
            return false;
        if (registrations is not null)
        {
            catalog = SemanticCatalog.Compose(_semanticModules.Append(registrations), out diagnostics);
            if (catalog is null) return false;
        }
        language = BuildSyntax(catalog, declarative!);
        return true;
    }

    Language BuildSyntax(SemanticCatalog catalog, DeclarativeLowering declarative)
    {
        var registry = new ExtensionRegistry();
        foreach (var module in _modules)
        {
            registry.Current = module;
            module.Register(registry);
        }
        registry.Current = null;
        var modules = _modules.ToArray();
        return new Language(modules, registry.Freeze(modules), _trivia, _triviaStart, catalog, declarative);
    }
```

- [ ] **Step 5: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~DeclarativeCompositionTests|FullyQualifiedName~ModuleComposerTests|FullyQualifiedName~SemanticCatalog"`. Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add Nitrogen.Runtime Nitrogen.Tests/Semantic/DeclarativeCompositionTests.cs
git commit -m "Resolve declarative rules against the semantic catalog (issue 251)"
```

---

### Task 6: Per-file typing and `NT` diagnostics

**Files:**
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` (replace the stub)
- Modify: `Nitrogen.Runtime/Semantics/FileSemantics.cs` (`HasReference`, `Diagnostics`)
- Test: `Nitrogen.Tests/Semantic/DeclarativeTypesTests.cs`

- [ ] **Step 1: Write the failing tests.** Create `Nitrogen.Tests/Semantic/DeclarativeTypesTests.cs`:

```csharp
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Lowered;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeTypesTests
{
    internal static void Run(string source, Action<FileSemantics> assert, params SemanticModule[] extra)
    {
        var language = DeclarativeCompositionTests.Build(extra);
        using var parsed = language.Parse(source, LoweredModule.File);
        Assert.True(parsed.Success, source);
        var project = new Project(language);
        project.Set("test.low", parsed.Tree);
        assert(new ProjectSemantics(project)["test.low"]);
    }

    static string At(string source, SemanticDiagnostic diagnostic) =>
        source.Substring(diagnostic.Span.Start, diagnostic.Span.Length);

    static int First(FileSemantics file, int kind) =>
        Enumerable.Range(0, file.Tree.NodeCount).First(node => file.Tree.Kind(node) == kind);

    [Fact]
    public void Operation_and_literals_type_check()
    {
        Run("add(1, 2);", file =>
        {
            Assert.Empty(file.Diagnostics());
            Assert.Equal(SemanticTypes.Scalar, file.DeclarativeTypes.TypeOf(First(file, LoweredKinds.Add)));
            Assert.Equal(SemanticTypes.Scalar, file.DeclarativeTypes.TypeOf(First(file, LoweredKinds.Num)));
        });
    }

    [Fact]
    public void Wrong_argument_type_is_NT0001_at_the_argument()
    {
        const string source = "turn(1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0001", diagnostic.Code);
            Assert.Equal("1", At(source, diagnostic));
            Assert.Contains("Units.Angle", diagnostic.Message);
            Assert.Contains("Core.Scalar", diagnostic.Message);
        });
    }

    [Fact]
    public void References_take_their_symbols_declared_types()
    {
        Run("input x : Scalar; fixed a; add(x, 1); turn(a);", file => Assert.Empty(file.Diagnostics()));
        const string source = "fixed a; add(a, 1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0001", diagnostic.Code);
            Assert.Equal("a", At(source, diagnostic));
        });
    }

    [Fact]
    public void Qualified_and_unique_unqualified_names_resolve() =>
        Run("input x : Core.Scalar; input y : Angle; add(x, 1); turn(y);", file => Assert.Empty(file.Diagnostics()));

    [Fact]
    public void Unknown_or_ambiguous_type_name_is_NT0002()
    {
        const string unknown = "input x : Nope; x;";
        Run(unknown, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0002", diagnostic.Code);
            Assert.Equal("Nope", At(unknown, diagnostic));
        });
        Run("input y : Angle;", file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0002", diagnostic.Code);
            Assert.Contains("qualify", diagnostic.Message);
        }, new SemanticModule("Other", [], [SemanticType.Named("Other", "Angle")], []));
    }

    [Fact]
    public void Nonfinite_literal_is_NT0003_without_a_cascade()
    {
        const string source = "add(1e999, 1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0003", diagnostic.Code);
            Assert.Equal("1e999", At(source, diagnostic));
        });
    }

    [Fact]
    public void Untyped_argument_is_NT0004()
    {
        const string source = "plain p; add(p, 1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0004", diagnostic.Code);
            Assert.Equal("p", At(source, diagnostic));
        });
    }

    [Fact]
    public void Pass_through_takes_its_single_typed_child()
    {
        Run("add(wrap(1), 2);", file => Assert.Empty(file.Diagnostics()));
        const string source = "turn(wrap(1));";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0001", diagnostic.Code);
            Assert.Equal("wrap(1)", At(source, diagnostic));
        });
    }

    [Fact]
    public void Nodes_outside_lowers_arguments_are_not_checked() =>
        Run("plain p; p; wrap(p);", file => Assert.Empty(file.Diagnostics()));
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `--filter "FullyQualifiedName~DeclarativeTypesTests"`. Expected: a compile FAIL, because `DeclarativeTypes.TypeOf` doesn't exist.

- [ ] **Step 3: Implement typing.** Replace `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` with:

```csharp
using System.Globalization;
using System.Text;
using Nitrogen.Binding;
using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

/// <summary>
/// One file's declarative types (issue 251). A node is typed by its own lowers clause, by the
/// symbol its reference resolves to, or, with neither, by its single typed child. Only lowers
/// arguments, literals and declared types are checked. <see cref="SemanticTypes.Error"/> marks a
/// node whose problem is reported elsewhere, so errors do not cascade.
/// </summary>
public sealed class DeclarativeTypes
{
    readonly FileSemantics _file;
    readonly DeclarativeLowering _lowering;
    readonly SemanticCatalog _catalog;
    readonly Dictionary<int, SemanticType?> _types = new();
    List<SemanticDiagnostic>? _diagnostics;

    internal DeclarativeTypes(FileSemantics file, DeclarativeLowering lowering, SemanticCatalog catalog)
    {
        _file = file;
        _lowering = lowering;
        _catalog = catalog;
    }

    /// <summary>The node's type; null when untyped, <see cref="SemanticTypes.Error"/> when invalid.</summary>
    public SemanticType? TypeOf(int node)
    {
        if (_types.TryGetValue(node, out var cached)) return cached;
        var type = Compute(node);
        _types[node] = type;
        return type;
    }

    /// <summary>The type the symbol's declaring clause gives it; null when it has none.</summary>
    public SemanticType? TypeOfSymbol(Symbol symbol)
    {
        if (symbol.IsBuiltin || symbol.Path is null) return null;
        var declaring = symbol.Path == _file.Path ? this : _file.RelatedFile(symbol.Path).DeclarativeTypes;
        return declaring.DeclaredTypeAt(symbol.Node);
    }

    SemanticType? Compute(int node)
    {
        var tree = _file.Tree;
        if ((tree.Flags(node) & NodeFlags.Missing) != 0) return SemanticTypes.Error;
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous)
            return tree.ChildCount(node) > 0 ? TypeOf(tree.Child(node, 0)) : SemanticTypes.Error;
        if (_lowering.RuleFor(kind) is { } rule && rule.Rule.Form != DeclarativeForm.None)
            return rule.Rule.Form == DeclarativeForm.Operation
                ? rule.Operation!.Result
                : TryLiteral(node, rule.Rule, out _) ? rule.LiteralType : SemanticTypes.Error;
        if (_file.HasReference(node))
            return _file.SymbolOf(node) is { } symbol ? TypeOfSymbol(symbol) : SemanticTypes.Error;

        SemanticType? found = null;
        int count = 0;
        for (int k = 0; k < tree.ChildCount(node); k++)
        {
            var child = TypeOf(tree.Child(node, k));
            if (child is null) continue;
            if (child.Equals(SemanticTypes.Error)) return SemanticTypes.Error;
            found = child;
            count++;
        }
        return count == 1 ? found : null;
    }

    SemanticType? DeclaredTypeAt(int node)
    {
        if (_lowering.RuleFor(_file.Tree.Kind(node)) is not { } rule) return null;
        if (rule.DeclaredType is { } fixedType) return fixedType;
        if (rule.Rule.DeclaredTypeChild < 0) return null;
        var text = Spelled(_file.Tree.Child(node, rule.Rule.DeclaredTypeChild));
        return text is not null && DeclarativeLowering.TryResolveType(_catalog, text, out var type) == TypeLookup.Found
            ? type
            : SemanticTypes.Error;
    }

    internal IReadOnlyList<SemanticDiagnostic> Diagnostics()
    {
        if (_diagnostics is not null) return _diagnostics;
        _diagnostics = new List<SemanticDiagnostic>();
        if (_lowering.IsEmpty) return _diagnostics;
        var tree = _file.Tree;
        var stack = new Stack<int>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            int kind = tree.Kind(node);
            if (kind == SyntaxKinds.Ambiguous)
            {
                if (tree.ChildCount(node) > 0) stack.Push(tree.Child(node, 0));
                continue;
            }
            if ((tree.Flags(node) & NodeFlags.Missing) == 0 && _lowering.RuleFor(kind) is { } rule) Check(node, rule);
            for (int k = tree.ChildCount(node) - 1; k >= 0; k--) stack.Push(tree.Child(node, k));
        }
        return _diagnostics;
    }

    void Check(int node, DeclarativeLowering.ResolvedRule rule)
    {
        var tree = _file.Tree;
        if (rule.Rule.Form == DeclarativeForm.Operation)
        {
            var operation = rule.Operation!;
            for (int i = 0; i < rule.Rule.Arguments.Count; i++)
            {
                int argument = tree.Child(node, rule.Rule.Arguments[i]);
                var expected = operation.Inputs[i];
                var actual = TypeOf(argument);
                if (actual is null)
                    Report("NT0004", argument, $"'{Text(argument)}' has no declared type; '{operation.Id}' needs {expected}");
                else if (!actual.Equals(SemanticTypes.Error) && !actual.Equals(expected))
                    Report("NT0001", argument, $"'{operation.Id}' needs {expected}, not {actual}");
            }
        }
        else if (rule.Rule.Form == DeclarativeForm.Literal && !TryLiteral(node, rule.Rule, out _))
        {
            Report("NT0003", node, $"'{Text(node)}' is not a finite number");
        }

        if (rule.Rule.DeclaredTypeChild >= 0)
        {
            int child = tree.Child(node, rule.Rule.DeclaredTypeChild);
            if (Spelled(child) is { } text)
            {
                var lookup = DeclarativeLowering.TryResolveType(_catalog, text, out _);
                if (lookup == TypeLookup.Missing) Report("NT0002", child, $"'{text}' is not a type in the semantic catalog");
                else if (lookup == TypeLookup.Ambiguous) Report("NT0002", child, $"'{text}' names more than one type; qualify it");
            }
        }
    }

    internal HirNode? LowerRoot(LoweringContext context, int node)
    {
        var tree = _file.Tree;
        for (int parent = tree.Parent(node); parent >= 0; parent = tree.Parent(parent))
            if (_lowering.RuleFor(tree.Kind(parent)) is { Rule.Form: DeclarativeForm.Operation }) return null;
        return Lower(context, node);
    }

    HirNode? Lower(LoweringContext context, int node) => null;

    bool TryLiteral(int node, DeclarativeRule rule, out float value)
    {
        int child = rule.Arguments[0];
        var text = Spelled(child < 0 ? node : _file.Tree.Child(node, child));
        value = 0;
        return text is not null &&
               float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               float.IsFinite(value);
    }

    /// <summary>The node's tokens without trivia; null when empty or any part is Missing.</summary>
    string? Spelled(int node)
    {
        var text = new StringBuilder();
        return Append(node, text) && text.Length > 0 ? text.ToString() : null;
    }

    bool Append(int node, StringBuilder text)
    {
        var tree = _file.Tree;
        if ((tree.Flags(node) & NodeFlags.Missing) != 0) return false;
        int count = tree.ChildCount(node);
        if (count == 0)
        {
            int kind = tree.Kind(node);
            if (kind != SyntaxKinds.Empty && kind != SyntaxKinds.List) text.Append(tree.GetText(node));
            return true;
        }
        for (int k = 0; k < count; k++)
            if (!Append(tree.Child(node, k), text)) return false;
        return true;
    }

    string Text(int node) => Spelled(node) ?? "";

    void Report(string code, int node, string message) =>
        _diagnostics!.Add(new SemanticDiagnostic(code, _file.Tree.Span(node), message));
}
```

- [ ] **Step 4: Expose references and merge diagnostics.** In `FileSemantics.cs`, move the `_references` initialization out of `SymbolOf` into a helper and add `HasReference`:

```csharp
    Dictionary<int, Reference> ReferencesByNode()
    {
        if (_references is null)
        {
            _references = new Dictionary<int, Reference>();
            foreach (var reference in Binding.References) _references.TryAdd(reference.Node, reference);
        }
        return _references;
    }

    /// <summary>Whether the node carries a reference clause (issue 251).</summary>
    internal bool HasReference(int node) => ReferencesByNode().ContainsKey(node);
```

and change `SymbolOf` to begin `if (!ReferencesByNode().TryGetValue(node, out var found)) return null;`. In `Diagnostics()`, change the return to:

```csharp
        return _checks.Concat(_evaluation).Concat(DeclarativeTypes.Diagnostics())
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();
```

- [ ] **Step 5: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~DeclarativeTypesTests|FullyQualifiedName~Semantics|FullyQualifiedName~Typed"`. Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add Nitrogen.Runtime Nitrogen.Tests/Semantic/DeclarativeTypesTests.cs
git commit -m "Type declarative lowering arguments and report NT diagnostics (issue 251)"
```

---

### Task 7: Declarative lowering to HIR

**Files:**
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` (`Lower`)
- Test: `Nitrogen.Tests/Semantic/DeclarativeLoweringTests.cs`

- [ ] **Step 1: Write the failing tests.** Create `Nitrogen.Tests/Semantic/DeclarativeLoweringTests.cs`:

```csharp
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeLoweringTests
{
    static void Lower(string source, Action<FileSemantics, LoweringResult> assert) =>
        DeclarativeTypesTests.Run(source, file => assert(file, HirLowering.Lower(file, file.Tree.Language!.SemanticCatalog)));

    static string Text(string source, HirNode node) =>
        source.Substring(node.Origins[0].Span.Start, node.Origins[0].Span.Length);

    [Fact]
    public void Operation_lowers_to_typed_constants_with_origins()
    {
        const string source = "add(1, 2);";
        Lower(source, (_, lowered) =>
        {
            Assert.Empty(lowered.Diagnostics);
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            Assert.Equal(DeclarativeCompositionTests.Add, root.Signature);
            Assert.Equal("add(1, 2)", Text(source, root));
            Assert.Equal([1f, 2f], root.Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value));
            Assert.Equal(["1", "2"], root.Arguments.Select(argument => Text(source, argument)));
            Assert.All(root.Arguments, argument => Assert.Equal(SemanticTypes.Scalar, argument.Type));
        });
    }

    [Fact]
    public void Nested_operation_is_one_root()
    {
        Lower("add(add(1, 2), 3);", (_, lowered) =>
        {
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            Assert.IsType<HirOperation>(root.Arguments[0]);
        });
    }

    [Fact]
    public void Reference_lowers_to_a_typed_symbol_ref()
    {
        Lower("input x : Scalar; add(x, 1);", (_, lowered) =>
        {
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            var reference = Assert.IsType<HirSymbolRef>(root.Arguments[0]);
            Assert.Equal(SemanticTypes.Scalar, reference.Type);
            Assert.Equal("x", reference.Symbol.Binding.Name);
            Assert.Equal("Lowered", reference.Symbol.Module);
        });
    }

    [Fact]
    public void Pass_through_lowers_its_typed_child()
    {
        const string source = "add(wrap(1), 2);";
        Lower(source, (_, lowered) =>
        {
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            Assert.Equal("1", Text(source, Assert.IsType<HirConstant>(root.Arguments[0])));
        });
    }

    [Fact]
    public void Type_errors_prevent_lowering()
    {
        Lower("turn(1);", (_, lowered) =>
        {
            Assert.Empty(lowered.Roots);
            Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
        });
    }

    [Fact]
    public void Lowered_program_evaluates_through_a_bound_host()
    {
        Lower("input x : Scalar; add(x, 1);", (file, lowered) =>
        {
            var root = Assert.Single(lowered.Roots);
            var catalog = file.Tree.Language!.SemanticCatalog;
            var registry = HostOperationRegistry.Bind(catalog, [new HostOperationBinding(DeclarativeCompositionTests.Add,
                (Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)(arguments =>
                    new ExecutionValue(SemanticTypes.Scalar, arguments[0].Number + arguments[1].Number)))]);
            var x = ((HirSymbolRef)((HirOperation)root).Arguments[0]).Symbol.Binding;
            var result = HirEvaluator.Evaluate(root, registry,
                new Dictionary<Symbol, ExecutionValue> { [x] = new(SemanticTypes.Scalar, 41f) });
            Assert.Empty(result.Diagnostics);
            Assert.Equal(42f, result.Value!.Number);
        });
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `--filter "FullyQualifiedName~DeclarativeLoweringTests"`. Expected: FAIL. `Roots` is empty because the `Lower` stub returns null, except in `Type_errors_prevent_lowering`, which passes.

- [ ] **Step 3: Implement `Lower`.** In `DeclarativeTypes.cs`, replace the `Lower` stub with:

```csharp
    HirNode? Lower(LoweringContext context, int node)
    {
        var type = TypeOf(node);
        if (type is null || type.Equals(SemanticTypes.Error)) return null;
        var tree = _file.Tree;
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous) return Lower(context, tree.Child(node, 0));
        if (_lowering.RuleFor(kind) is { } rule && rule.Rule.Form != DeclarativeForm.None)
        {
            if (rule.Rule.Form == DeclarativeForm.Literal)
                return TryLiteral(node, rule.Rule, out var value) ? new HirConstant(value, type, context.Origin(node)) : null;
            var operation = rule.Operation!;
            var arguments = new HirNode[rule.Rule.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                var argument = Lower(context, tree.Child(node, rule.Rule.Arguments[i]));
                if (argument is null || !argument.Type.Equals(operation.Inputs[i])) return null;
                arguments[i] = argument;
            }
            return new HirOperation(operation, arguments, [context.Origin(node)]);
        }
        if (_file.HasReference(node))
        {
            var symbol = _file.SymbolOf(node)!;
            return new HirSymbolRef(SemanticSymbol.From(symbol, ModuleOf(symbol), type), context.Origin(node));
        }
        for (int k = 0; k < tree.ChildCount(node); k++)
        {
            int child = tree.Child(node, k);
            if (TypeOf(child) is not null) return Lower(context, child);
        }
        return null;
    }

    string ModuleOf(Symbol symbol)
    {
        var declaring = symbol.Path == _file.Path ? _file : _file.RelatedFile(symbol.Path!);
        int kind = declaring.Tree.Kind(symbol.Node);
        return declaring.Tree.Language!.ModuleById(SyntaxKinds.ModuleOf(kind))!.Name;
    }
```

- [ ] **Step 4: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~DeclarativeLoweringTests|FullyQualifiedName~DeclarativeTypesTests|FullyQualifiedName~Hir"`. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add Nitrogen.Runtime Nitrogen.Tests/Semantic/DeclarativeLoweringTests.cs
git commit -m "Lower declarative clauses to typed HIR (issue 251)"
```

---

### Task 8: Migrate Geometry

**Files:**
- Modify: `Nitrogen.Geometry/Geometry.ngr`, `Nitrogen.Geometry/BoxMeshModule.cs`, `Nitrogen.Geometry/GeometryDefinitionExpander.cs`
- Delete: `Nitrogen.Geometry/GeometryHirLowerer.cs`
- Test: `Nitrogen.Tests/Geometry/GeometryDefinitionTests.cs`, `Nitrogen.Tests/Geometry/GeometryModuleTests.cs`

- [ ] **Step 1: Add characterization tests, which pass before the migration.** Create `Nitrogen.Tests/Geometry/GeometryDefinitionTests.cs`:

```csharp
using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryDefinitionTests
{
    static ModuleComposition Compose() => ModuleComposer.Compose([BoxMeshModule.Descriptor],
        [new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))]);

    [Fact]
    public void Call_in_another_file_expands_to_the_box_operation()
    {
        var composition = Compose();
        var start = composition.StartRules[("Geometry", "Document")];
        using var definitions = composition.Language.Parse("def cube(w: Scalar, h: Scalar, d: Scalar) = box w h d;", start);
        using var call = composition.Language.Parse("make cube(1, 2, 3);", start);
        Assert.True(definitions.Success);
        Assert.True(call.Success);
        var project = new Project(composition.Language);
        project.Set("defs.geom", definitions.Tree);
        project.Set("use.geom", call.Tree);
        var semantics = new ProjectSemantics(project);
        Assert.Empty(semantics["defs.geom"].Diagnostics());
        var lowered = HirLowering.Lower(semantics["use.geom"], composition.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal(BoxMeshModule.BoxSignature, root.Signature);
        Assert.Equal([1f, 2f, 3f], root.Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value));
        Assert.All(root.Arguments, argument => Assert.Equal(SemanticTypes.Scalar, argument.Type));
    }

    [Fact]
    public void Mesh_parameter_is_GD0001()
    {
        var composition = Compose();
        using var parsed = composition.Language.Parse("def bad(w: Mesh, h: Scalar, d: Scalar) = box 1 h d;",
            composition.StartRules[("Geometry", "Document")]);
        var project = new Project(composition.Language);
        project.Set("defs.geom", parsed.Tree);
        Assert.Contains(new ProjectSemantics(project)["defs.geom"].Diagnostics(), diagnostic => diagnostic.Code == "GD0001");
    }

    [Fact]
    public void Hover_shows_mesh_and_scalar_types()
    {
        var composition = Compose();
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("geometry", composition.Language,
            new Dictionary<string, Rule> { [".geom"] = composition.StartRules[("Geometry", "Document")] }));
        using var service = new NitrogenLanguageService(registry);
        service.Open("file:///w/a.geom", 1, "box 1 2 3;");
        Assert.Contains("`Geometry.Mesh`", service.Hover("file:///w/a.geom", new DocumentPosition(0, 1))!.Markdown);
        Assert.Contains("`Core.Scalar` = 1", service.Hover("file:///w/a.geom", new DocumentPosition(0, 4))!.Markdown);
    }
}
```

Run `--filter "FullyQualifiedName~GeometryDefinitionTests"`. Expected: PASS. These tests record the current behavior.

- [ ] **Step 2: Write the failing migration test.** Append to `GeometryModuleTests`:

```csharp
    [Fact]
    public void Geometry_types_and_lowers_through_declarative_rules()
    {
        var rules = Nitrogen.Geometry.Syntax.GeometryModule.Instance.DeclarativeRules.ToDictionary(rule => rule.LocalKind);
        var box = rules[Nitrogen.Geometry.Syntax.GeometryModule.LBox];
        Assert.Equal(DeclarativeForm.Operation, box.Form);
        Assert.Equal("Geometry.BoxMesh", box.Target);
        Assert.Equal([1, 2, 3], box.Arguments);
        var num = rules[Nitrogen.Geometry.Syntax.GeometryModule.LNum];
        Assert.Equal(DeclarativeForm.Literal, num.Form);
        Assert.Equal("Core.Scalar", num.Target);
        Assert.Equal([-1], num.Arguments);
        Assert.Equal(2, rules[Nitrogen.Geometry.Syntax.GeometryModule.LParameter].DeclaredTypeChild);
        Assert.DoesNotContain(BoxMeshModule.Descriptor.Semantics!.Lowerers,
            lowerer => lowerer.SyntaxKind == Nitrogen.Geometry.Syntax.GeometryKinds.Box);
    }
```

Run `--filter "FullyQualifiedName~Geometry_types_and_lowers_through_declarative_rules"`. Expected: FAIL, because `rules` has no `Box` key.

- [ ] **Step 3: Migrate the grammar.** In `Nitrogen.Geometry/Geometry.ngr`, delete the `symbol property Type for parameter …` line and replace the `Parameter`, `ParameterRef`, `Num`, and `Box` rules with:

```
  syntax Parameter = Name:Identifier ":" Annotation:("Scalar" / "Mesh") declares parameter Name type Annotation
  {
    check GD0001 Annotation.Text == "Scalar" : "box parameters must be Scalar" at Annotation;
  }
```

```
  syntax ParameterRef = Name:Identifier references parameter Name;
```

```
  syntax Num = Sign:("+" / "-")? Value:Number lowers literal Core.Scalar this
  {
    out Amount : float? = null;
    Amount = GeometryValues.Parse(Value.Text, Sign?.Text == "-");
    check GE0001 GeometryValues.Positive(Amount) : "box dimensions must be finite and positive";
  }
  syntax Box = "box" Width:Num Height:Num Depth:Num ";" lowers Geometry.BoxMesh(Width, Height, Depth);
```

- [ ] **Step 4: Drop the C# lowerer.** Delete `Nitrogen.Geometry/GeometryHirLowerer.cs`. In `BoxMeshModule.cs`, change the lowerer list to `[GeometryDefinitionExpander.Registration]`.

- [ ] **Step 5: Read types in the expander.** In `GeometryDefinitionExpander.ExpandBoundCall`, replace the loop body's symbol construction with:

```csharp
            var parameter = definitionFile.Binding.Declarations.SingleOrDefault(candidate =>
                candidate.Kind == "parameter" && candidate.Node == parameters[i].Index);
            var value = Dimension(context, caller, call.Args[i].Index, callerArguments);
            if (parameter is null || value is null) return null;
            var parameterType = definitionFile.DeclarativeTypes.TypeOfSymbol(parameter);
            if (parameterType is null || parameterType.Equals(SemanticTypes.Error)) return null;
            var declaration = new HirSymbolRef(
                Nitrogen.Semantic.SemanticSymbol.From(parameter, "Geometry", parameterType),
                Origin(definitionFile, context.SnapshotId, parameter.Node));
```

In `Dimension`, replace both hard-coded types:

```csharp
        int number = Find(tree, node, GeometryKinds.Num);
        if (number >= 0)
        {
            var amount = new NumNodeSemantics(file, number).Amount;
            var numberType = file.DeclarativeTypes.TypeOf(number);
            return GeometryValues.Positive(amount) && numberType is not null && !numberType.Equals(SemanticTypes.Error)
                ? new HirConstant(amount!.Value, numberType, Origin(file, context.SnapshotId, number))
                : null;
        }
        int referenceNode = Find(tree, node, GeometryKinds.ParameterRef);
        if (referenceNode < 0) return null;
        var binding = file.SymbolOf(referenceNode);
        var referenceType = file.DeclarativeTypes.TypeOf(referenceNode);
        if (binding is null || referenceType is null || referenceType.Equals(SemanticTypes.Error) ||
            !arguments.TryGetValue(binding, out var replacement)) return null;
        var reference = new HirSymbolRef(Nitrogen.Semantic.SemanticSymbol.From(binding, "Geometry", referenceType),
            Origin(file, context.SnapshotId, referenceNode));
        return HirTraversal.Rewrite(reference, _ => replacement);
```

- [ ] **Step 6: Run all Geometry tests and confirm they pass.** Run `--filter "FullyQualifiedName~Geometry"`. Expected: PASS, including the unchanged `GeometryHirLowererTests`, `GeometryExecutorTests`, and `Invalid_dimensions_report_literal_spans`. That last test uses `Assert.Single` with a `GE0001` filter, so the extra `NT0003` on `1e999` doesn't break it.

- [ ] **Step 7: Commit.**

```bash
git add -A Nitrogen.Geometry Nitrogen.Tests/Geometry
git commit -m "Migrate Geometry to declarative typing and lowering (issue 251)"
```

---

### Task 9: Admission of declarative packages

**Files:**
- Modify: `Nitrogen.Workspace/Admission/ModuleAdmissionService.cs` (`TryCandidate`)
- Test: `Nitrogen.Tests/Workspace/Admission/DeclarativeGrammarPolicyTests.cs`, `Nitrogen.Tests/Workspace/Admission/DeclarativeAdmissionTests.cs`

- [ ] **Step 1: Add a policy characterization test.** Append to `DeclarativeGrammarPolicyTests`:

```csharp
    [Fact]
    public void Declarative_typing_and_lowering_clauses_are_allowed()
    {
        const string grammar = """
            syntax module Rules
            {
              symbols { thing }
              token Word = ['a'..'z']+;
              token Digits = ['0'..'9']+;
              syntax Doc = "use" Name:Word ":" Kind:Word declares thing Name type Kind;
              syntax Num = Text:Digits lowers literal Core.Scalar Text;
              syntax Pair = "pair" Left:Num Right:Num lowers Rules.Pair(Left, Right);
            }
            """;
        Assert.Empty(DeclarativeGrammarPolicy.Validate(Package(grammar)));
    }
```

- [ ] **Step 2: Write the end-to-end tests.** Create `Nitrogen.Tests/Workspace/Admission/DeclarativeAdmissionTests.cs`:

```csharp
using System.Globalization;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class DeclarativeAdmissionTests
{
    static readonly SemanticType Angle = SemanticType.Named("Units", "Angle");
    static readonly OperationSignature Area = new("Shapes.Area", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly OperationSignature Turn = new("Shapes.Turn", Angle, Angle);

    const string Grammar = """
        syntax module Shapes
        {
          token Digits = ['0'..'9']+;
          syntax Document = Shape:Rect;
          syntax Rect = "rect" Width:Num Height:Num ";" lowers Shapes.Area(Width, Height);
          syntax Num = Text:Digits lowers literal Core.Scalar Text;
        }
        """;

    static Dictionary<string, HostModuleProfile> Profiles() => new()
    {
        ["shapes"] = new HostModuleProfile("shapes", "Shapes",
            syntax => new ModuleDescriptor("Shapes", syntax,
                new SemanticModule("Shapes", [], [Angle], [Area, Turn]), ["Document"], [Area]),
            [],
            new HostCapabilitySet([new HostCapability(Area, new HostOperationBinding(Area,
                (Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)(arguments =>
                    new ExecutionValue(SemanticTypes.Scalar, arguments[0].Number * arguments[1].Number))),
                CapabilityEffect.Pure)]),
            Probe),
    };

    static string? Probe(ModuleComposition composition, FileSemantics file, IReadOnlyList<HirNode> roots, string? expected)
    {
        var registry = HostOperationRegistry.Bind(composition.Language.SemanticCatalog, composition.HostBindings.Values);
        var result = HirEvaluator.Evaluate(Assert.Single(roots), registry, new Dictionary<Symbol, ExecutionValue>());
        string actual = result.Value?.Number.ToString(CultureInfo.InvariantCulture) ?? "error";
        return actual == expected ? null : $"expected {expected}, got {actual}";
    }

    static ModulePackage Package(string grammar, string hash) => new("shapes-demo", "shapes", "Shapes", "Shapes.Document",
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["shapes.ngr"] = grammar },
        [new ModuleExample("area", "area.shapes", "rect 2 3;", [], "6")], ["Shapes.Area"], hash);

    static string Describe(AdmissionResult result) =>
        string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}"));

    [Fact]
    public void Declarative_package_is_admitted_and_executes()
    {
        using var service = new ModuleAdmissionService();
        var result = service.Admit(Package(Grammar, "good"), Profiles());
        Assert.True(result.Accepted, Describe(result));
        Assert.Equal("good", service.Active!.Sha256);
    }

    [Fact]
    public void Type_mismatch_in_an_example_is_rejected_with_NT0001()
    {
        var mismatched = Grammar.Replace("lowers literal Core.Scalar Text", "lowers literal Units.Angle Text");
        using var service = new ModuleAdmissionService();
        var result = service.Admit(Package(mismatched, "mismatch"), Profiles());
        Assert.False(result.Accepted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("NA0004", diagnostic.Code);
        Assert.Contains("NT0001", diagnostic.Message);
        Assert.Null(service.Active);
    }

    [Fact]
    public void Lowering_an_unrequested_operation_is_NA0006_and_keeps_the_active_module()
    {
        using var service = new ModuleAdmissionService();
        Assert.True(service.Admit(Package(Grammar, "good"), Profiles()).Accepted);
        var turning = Grammar
            .Replace("syntax Document = Shape:Rect;", "syntax Document = Shape:(Rect / Spin);")
            .Replace("syntax Num =", "syntax Spin = \"spin\" Amount:Deg \";\" lowers Shapes.Turn(Amount);\n  syntax Deg = Text:Digits \"deg\" lowers literal Units.Angle Text;\n  syntax Num =");
        var result = service.Admit(Package(turning, "turning"), Profiles());
        Assert.False(result.Accepted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("NA0006", diagnostic.Code);
        Assert.Contains("Shapes.Turn", diagnostic.Message);
        Assert.Equal("good", service.Active!.Sha256);
    }
}
```

- [ ] **Step 3: Run them and confirm the expected results.** Run `--filter "FullyQualifiedName~DeclarativeAdmissionTests|FullyQualifiedName~DeclarativeGrammarPolicyTests"`. Expected: the policy test and the first two admission tests PASS. `Lowering_an_unrequested_operation…` FAILS, because the `turning` package is accepted: its example only uses `rect`.

- [ ] **Step 4: Enforce requested operations.** In `ModuleAdmissionService.TryCandidate`, directly after the `host descriptor does not allow the package start rule` check and before `ModuleDescriptor[] descriptors = …`, add:

```csharp
            var unrequested = modules[0].DeclarativeRules
                .Where(rule => rule.Form == DeclarativeForm.Operation)
                .Select(rule => rule.Target!)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !package.RequestedCapabilities.Contains(id, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unrequested.Length > 0)
            {
                snapshot.Dispose();
                return new CandidateResult(null, unrequested.Select(id =>
                    CapabilityError($"operation '{id}' is lowered by the grammar but not requested")).ToArray());
            }
```

- [ ] **Step 5: Run the tests and confirm they pass.** Run `--filter "FullyQualifiedName~Admission"`. Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add Nitrogen.Workspace Nitrogen.Tests/Workspace
git commit -m "Admit declarative packages and require lowered operations (issue 251)"
```

---

### Task 10: Documentation and full verification

**Files:**
- Modify: `docs/roadmap.md` (Current state, gap 1)
- Modify: `.agents/skills/nitrogen/SKILL.md` (step 3)
- Modify: `issues/251-nitrogen-declarative-typing-lowering.md` (status and log)

- [ ] **Step 1: Update the roadmap.** In `docs/roadmap.md`, replace known gap 1 with:

```
1. **Resolved in issue 251.** `.ngr` clauses (`lowers Op(...)`, `lowers literal T F`, `declares k N type T`) type nodes by catalog `SemanticType` and lower them to HIR without C#, for trusted modules and admitted packages. Semantics blocks remain for value checks such as `GE0001`.
```

- [ ] **Step 2: Update the agent guide.** In `.agents/skills/nitrogen/SKILL.md` step 3, after the built-in types sentence, add: `Prefer declarative clauses (`lowers`, `lowers literal`, `declares … type`) for typing and lowering; they are checked against the composed catalog (`NM0008`–`NM0010`, `NT0001`–`NT0004`).` Add these rows to the repair table:

```
| `NM0008`–`NM0010` | `lowers` operation, literal or declared type, and argument count against the catalog |
| `NT0001`–`NT0004` | Argument type, type name, literal text, and missing declared type |
```

- [ ] **Step 3: Run the full verification.** Run:

```bash
dotnet build Nitrogen.slnx -warnaserror --no-incremental
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj
git diff --check main
```

Expected: build succeeded with 0 warnings; every test passes; no whitespace errors. Record the pass count.

- [ ] **Step 4: Log the issue.** Set `Status: Implemented; awaiting review` in `issues/251-nitrogen-declarative-typing-lowering.md` and add a dated log entry that states the test count from Step 3, the four refinements listed in this plan's header, and that no merge or push has been performed.

- [ ] **Step 5: Commit.**

```bash
git add docs/roadmap.md .agents/skills/nitrogen/SKILL.md issues/251-nitrogen-declarative-typing-lowering.md
git commit -m "Document declarative typing and lowering (issue 251)"
```
