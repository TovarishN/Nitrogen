# Self-hosted grammar lowering (stage 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Nitrogen.ngr` `lowers` clauses so every `.ngr` file lowers to one typed `Grammar.File` HIR root, and serve that in the `ngr` language without changing its parse results or colouring.

**Architecture:** One new argument form (`optional text Field`) goes through the grammar layer (model, bootstrap parser, validator, dumper, writer) and the runtime (rule table, composition, checks, lowering). `Nitrogen.ngr` is reshaped so every field is lowerable, keeping `GrammarModel` identical (held by `SelfHostingTests`). Then a `Grammar` semantic module and the clauses are added, `NgrParser.Language` gets the module, and a `Presentation.ColorFromLowering` flag keeps `.ngr` colouring unchanged.

**Tech Stack:** C# / .NET 10, xUnit, the Nitrogen source generator (`Nitrogen.Generator`), `.ngr` grammars.

**Spec:** `docs/superpowers/specs/2026-10-06-self-hosted-grammar-lowering-design.md`

**Conventions:**
- Build: `dotnet build Nitrogen.slnx -warnaserror`. Focused tests: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Generated C# is build output; never edit it. `Nitrogen.Ngr` regenerates its parser from `Nitrogen.ngr` on build, using the bootstrap `GrammarParser`.
- Snapshot updates: `NITROGEN_UPDATE_SNAPSHOTS=1 dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~CommitSnapshotTests"`, then review the diff.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.Grammar/GrammarModel.cs` | `LoweringArgument.OptionalText` | 1 |
| `Nitrogen.Grammar/GrammarParser.cs` | bootstrap parsing of `optional text Field` | 1 |
| `Nitrogen.Grammar/GrammarValidator.cs` | optional field required | 1 |
| `Nitrogen.Grammar/GrammarDumper.cs` | dump form | 1 |
| `Nitrogen.Grammar/DeclarativeWriter.cs` | emit `argumentOptionalTexts` | 2 |
| `Nitrogen.Runtime/Semantic/DeclarativeRule.cs` | `ArgumentOptionalTexts` | 2 |
| `Nitrogen.Runtime/Semantic/DeclarativeLowering.cs` | composition check (`NM0011`) | 2 |
| `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` | type check, expected type, lowering | 2 |
| `Nitrogen.Tests/Grammars/OptionalText.ngr` + `Semantic/OptionalTextLoweringTests.cs` | runtime tests | 2 |
| `Nitrogen.Ngr/Nitrogen.ngr`, `Nitrogen.Ngr/NgrMapper.cs`, `Nitrogen.Cli/NgrAssist.cs` | self-hosted `optional text` | 3 |
| `Nitrogen.Ngr/Nitrogen.ngr`, `Nitrogen.Ngr/NgrMapper.cs` | reshape, model unchanged | 4 |
| `Nitrogen.Ngr/GrammarSemantics.cs` | the `Grammar` semantic module | 5 |
| `Nitrogen.Ngr/Nitrogen.ngr`, `Nitrogen.Ngr/NgrParser.cs`, `Nitrogen.Tests/Grammar/GrammarLoweringTests.cs` | clauses, wiring, coverage | 6 |
| `Nitrogen.LanguageService/Presentation.cs`, `NitrogenLanguageService.cs`, `Nitrogen.Cli/LspCommand.cs`, `Nitrogen.Tests/LanguageService/NgrEditorLoweringTests.cs` | colour opt-out, editor checks | 7 |
| `README.md`, spec status | docs | 8 |

---

### Task 1: `optional text` in the grammar layer

**Files:**
- Modify: `Nitrogen.Grammar/GrammarModel.cs:105-107`
- Modify: `Nitrogen.Grammar/GrammarParser.cs:409-418`
- Modify: `Nitrogen.Grammar/GrammarValidator.cs:424`
- Modify: `Nitrogen.Grammar/GrammarDumper.cs:80`
- Test: `Nitrogen.Tests/Grammar/GrammarParserTests.cs`, `Nitrogen.Tests/Grammar/GrammarValidatorTests.cs`

- [ ] **Step 1: Write the failing tests**

In `GrammarParserTests.Declarative_clauses_parse`, add after the `sequence inferred` row:

```csharp
    [InlineData("syntax R = Mark:\"!\"? V:X lowers M.Op(optional text Mark, V);", "(lowers M.Op(optional text Mark, V))")]
```

Add to `GrammarParserTests`:

```csharp
    [Fact]
    public void Optional_text_argument_keeps_its_field_and_span()
    {
        const string text = "syntax module M { syntax R = Mark:\"!\"? lowers M.Op(optional text Mark); }";
        var clause = Assert.Single(((SyntaxRule)ParseOk(text).Modules[0].Rules[0]).Clauses);
        var argument = Assert.Single(clause.Arguments);
        Assert.True(argument.OptionalText);
        Assert.Null(argument.OptionalElementType);
        Assert.Equal("Mark", argument.Name);
        Assert.Equal(text.LastIndexOf("Mark", StringComparison.Ordinal), argument.Span.Start);
    }
```

Add to `GrammarValidatorTests` after `Optional_lowering_argument_requires_an_optional_field`:

```csharp
    [Fact]
    public void Optional_text_argument_requires_an_optional_field()
    {
        var d = Single(GrammarCodes.OptionalArgumentNeedsOptionalField,
            "syntax module M { token T = ['a'..'z']+; syntax R = Mark:\"!\" V:T lowers M.Build(optional text Mark, text V); }");
        Assert.Contains("optional element", d.Message);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarParserTests|FullyQualifiedName~GrammarValidatorTests"`
Expected: build error `'LoweringArgument' does not contain a definition for 'OptionalText'`.

- [ ] **Step 3: Implement**

`GrammarModel.cs`: replace the `LoweringArgument` record with

```csharp
/// <summary>An operation input from a field, optionally projecting a repeated field as a sequence.</summary>
public sealed record LoweringArgument(string Name, GrammarSpan Span, NameDecl? SequenceElementType = null,
    bool AsText = false, NameDecl? OptionalElementType = null, bool InferSequence = false, bool OptionalText = false);
```

`GrammarParser.cs`, at the top of `ParseLoweringArgument` (before the existing `optional` branch, which would otherwise read `text` as a type):

```csharp
            if (AtKeyword("optional") && Next.Kind == TokenKind.Identifier && Next.Value == "text" &&
                TokenAt(_position + 2).Kind == TokenKind.Identifier)
            {
                Advance();
                Advance();
                var field = Name(Advance());
                return new LoweringArgument(field.Name, field.Span, OptionalText: true);
            }
```

`GrammarValidator.cs:424`: change the condition to

```csharp
                    else if (argument.OptionalElementType is not null || argument.OptionalText)
```

`GrammarDumper.cs:80`: in the argument projection, test `OptionalText` first:

```csharp
clause.Arguments.Select(a => a.OptionalText ? $"optional text {a.Name}" : a.InferSequence ? $"sequence inferred {a.Name}" : a.SequenceElementType is { } type ? $"sequence {type.Name} {a.Name}" : a.OptionalElementType is { } optional ? $"optional {optional.Name} {a.Name}" : a.AsText ? $"text {a.Name}" : a.Name)
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarParserTests|FullyQualifiedName~GrammarValidatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Grammar Nitrogen.Tests/Grammar/GrammarParserTests.cs Nitrogen.Tests/Grammar/GrammarValidatorTests.cs
git commit -m "Parse and validate optional text lowering arguments

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `optional text` in the generator and runtime

**Files:**
- Create: `Nitrogen.Tests/Grammars/OptionalText.ngr`
- Create: `Nitrogen.Tests/Semantic/OptionalTextLoweringTests.cs`
- Modify: `Nitrogen.Tests/Nitrogen.Tests.csproj` (AdditionalFiles list, ~line 68)
- Modify: `Nitrogen.Grammar/DeclarativeWriter.cs:54-69`
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeLowering.cs:72-80`
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs:69, 185, 378`

- [ ] **Step 1: Write the test grammar and failing tests**

`Nitrogen.Tests/Grammars/OptionalText.ngr`:

```ngr
// optional text Field: an optional keyword lowers to Core.Optional<Core.Text>.
syntax module OptionalText
{
  token Name = ['a'..'z']+;

  syntax Flagged = "flag" Mark:"!"? Value:Name lowers Test.Flag(optional text Mark, text Value);
}
```

In `Nitrogen.Tests.csproj`, after the `Templates.ngr` line:

```xml
    <AdditionalFiles Include="Grammars\OptionalText.ngr" Namespace="Nitrogen.Tests.OptionalText" />
```

`Nitrogen.Tests/Semantic/OptionalTextLoweringTests.cs`:

```csharp
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.OptionalText;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class OptionalTextLoweringTests
{
    static readonly SemanticType Flag = SemanticType.Named("Test", "Flag");

    static LanguageBuilder Builder(SemanticType markInput) =>
        new LanguageBuilder().Add(OptionalTextModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [Flag],
                [new OperationSignature("Test.Flag", Flag, markInput, SemanticTypes.Text)]));

    [Theory]
    [InlineData("flag ! left", "!")]
    [InlineData("flag left", null)]
    public void Optional_text_lowers_presence_and_spelling(string source, string? mark)
    {
        var language = Builder(SemanticTypes.OptionalOf(SemanticTypes.Text)).Build();
        using var parsed = language.Parse(source, OptionalTextModule.Flagged);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("flag.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["flag.txt"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var optional = Assert.IsType<HirOptional>(root.Arguments[0]);
        Assert.Equal(SemanticTypes.OptionalOf(SemanticTypes.Text), optional.Type);
        if (mark is null)
        {
            Assert.Null(optional.Value);
        }
        else
        {
            var text = Assert.IsType<HirText>(optional.Value);
            Assert.Equal(mark, text.Value);
            Assert.Equal(source.IndexOf('!'), text.Origins[0].Span.Start);
        }
        Assert.Equal("left", Assert.IsType<HirText>(root.Arguments[1]).Value);
    }

    [Fact]
    public void Optional_text_requires_an_optional_text_input()
    {
        Assert.False(Builder(SemanticTypes.Text).TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0011" &&
            diagnostic.Message.Contains("passes optional text", StringComparison.Ordinal));
    }

    [Fact]
    public void Optional_text_input_is_not_an_expected_type()
    {
        var language = Builder(SemanticTypes.OptionalOf(SemanticTypes.Text)).Build();
        using var parsed = language.Parse("flag ! left", OptionalTextModule.Flagged);
        var project = new Project(language);
        project.Set("expected.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["expected.txt"];
        var mark = Enumerable.Range(0, file.Tree.NodeCount)
            .First(node => file.Tree.ChildCount(node) == 0 && file.Tree.GetText(node).ToString() == "!");

        Assert.Null(file.DeclarativeTypes.ExpectedTypeOf(mark));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~OptionalTextLoweringTests"`
Expected: FAIL. The generator does not emit the optional-text flag yet, so `Mark` is passed as a plain field: the theory fails at `Assert.Empty(file.Diagnostics())` with `NT0004`, and `Optional_text_requires_an_optional_text_input` fails because `TryBuild` succeeds.

- [ ] **Step 3: Implement the rule table**

`DeclarativeRule.cs`: add a constructor parameter at the end, `bool[]? argumentOptionalTexts = null`, and after `ArgumentInferredSequences` is assigned:

```csharp
        ArgumentOptionalTexts = Array.AsReadOnly(argumentOptionalTexts is null
            ? new bool[arguments.Length] : (bool[])argumentOptionalTexts.Clone());
```

Extend the metadata validation: add `ArgumentOptionalTexts.Count != arguments.Length ||` to the count checks, and to the per-argument `Any(...)` add

```csharp
                (ArgumentOptionalTexts[i] && (ArgumentTexts[i] || ArgumentSequenceTypes[i] is not null ||
                    ArgumentOptionalTypes[i] is not null || ArgumentInferredSequences[i])) ||
```

Add the property next to `ArgumentTexts`:

```csharp
    /// <summary>Per argument: an optional field lowered as <c>Core.Optional&lt;Core.Text&gt;</c> of its spelling.</summary>
    public IReadOnlyList<bool> ArgumentOptionalTexts { get; }
```

`DeclarativeWriter.cs`: in the `Any(...)` condition at line 54 add `|| a.OptionalText`, and after the `InferSequence` append (line 68):

```csharp
                if (lowers.Arguments.Any(a => a.OptionalText))
                    extraArguments += $", argumentOptionalTexts: new[] {{ {string.Join(", ", lowers.Arguments.Select(a => a.OptionalText ? "true" : "false"))} }}";
```

- [ ] **Step 4: Implement composition, checking, and lowering**

`DeclarativeLowering.cs`, first statement inside the per-argument loop (before the `ArgumentTexts` check at line 74):

```csharp
                            if (rule.ArgumentOptionalTexts[i])
                            {
                                if (operation is not null && !operation.Inputs[i].Equals(SemanticTypes.OptionalOf(SemanticTypes.Text)))
                                    errors.Add(new CompositionDiagnostic("NM0011", [module.Name],
                                        $"{where} passes optional text to '{operation.Id}', which needs {operation.Inputs[i]}."));
                                continue;
                            }
```

`DeclarativeTypes.cs:69` (`ExpectedTypeOf`):

```csharp
                if (rule.Rule.ArgumentTexts[i] || rule.Rule.ArgumentOptionalTexts[i]) continue;
```

`DeclarativeTypes.cs`, in the operation check loop before `if (rule.Rule.ArgumentTexts[i])` (line 185):

```csharp
                if (rule.Rule.ArgumentOptionalTexts[i])
                {
                    if (tree.Kind(argument) != SyntaxKinds.Empty && Spelled(argument) is null)
                        Report("NT0005", argument, "text argument has no complete source text");
                    continue;
                }
```

`DeclarativeTypes.cs`, in `Lower` before `if (rule.Rule.ArgumentTexts[i])` (line 378), turning that `if` into `else if`:

```csharp
                if (rule.Rule.ArgumentOptionalTexts[i])
                {
                    HirText? text = null;
                    if (tree.Kind(source) != SyntaxKinds.Empty)
                    {
                        if (Spelled(source) is not { } spelled) return null;
                        text = new HirText(spelled, context.Origin(source));
                    }
                    argument = new HirOptional(SemanticTypes.Text, text, context.Origin(source));
                }
                else if (rule.Rule.ArgumentTexts[i])
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~OptionalTextLoweringTests|FullyQualifiedName~DeclarativeRuleTableTests|FullyQualifiedName~GeneratedSnapshotTests"`
Expected: PASS. Existing generated snapshots are unchanged because the new emission happens only when a rule uses `optional text`.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Grammar/DeclarativeWriter.cs Nitrogen.Runtime/Semantic Nitrogen.Tests/Grammars/OptionalText.ngr Nitrogen.Tests/Semantic/OptionalTextLoweringTests.cs Nitrogen.Tests/Nitrogen.Tests.csproj
git commit -m "Lower optional text arguments to Core.Optional<Core.Text>

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `optional text` in the self-hosted parser

**Files:**
- Modify: `Nitrogen.Ngr/Nitrogen.ngr:68-71`
- Modify: `Nitrogen.Ngr/NgrMapper.cs:252-264`
- Modify: `Nitrogen.Cli/NgrAssist.cs:20-26`
- Test: `Nitrogen.Tests/Grammar/SelfHostingTests.cs`

- [ ] **Step 1: Write the failing test**

In `SelfHostingTests.Every_declaration_form_maps_to_the_bootstrap_model`, add after the `optional Core.Scalar` row:

```csharp
    [InlineData("syntax module M { token T = ['a'..'z']+; syntax R = Mark:\"!\"? Value:T lowers M.Build(optional text Mark, text Value); }")]
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests"`
Expected: the new row FAILs at the final `Assert.Equal(expected, actual)`. The self-hosted parser reads `text` as an optional element type, so both dumps print `optional text Mark` but the records differ (`OptionalText: true` vs `OptionalElementType: text`).

- [ ] **Step 3: Implement**

`Nitrogen.ngr`: change `LowersArgument` and add the rule ahead of `LowersOptionalArgument`:

```ngr
  syntax LowersArgument = LowersSequenceArgument / LowersOptionalTextArgument / LowersOptionalArgument / LowersTextArgument / Identifier;
  syntax LowersSequenceArgument = "sequence" Type:QualifiedName Field:Identifier;
  syntax LowersOptionalTextArgument = "optional" "text" Field:Identifier;
  syntax LowersOptionalArgument = "optional" Type:QualifiedName Field:Identifier;
```

`NgrMapper.cs`, in the argument loop before the `LowersOptionalArgument` branch:

```csharp
                        else if (argument.Kind == NitrogenKinds.LowersOptionalTextArgument)
                        {
                            var field = argument.Child(2);
                            arguments.Add(new LoweringArgument(field.ToString(), Span(field.Span), OptionalText: true));
                        }
```

`NgrAssist.cs`: add `NitrogenKinds.LowersOptionalTextArgument` to the `Clauses` set. `Classify` already returns `Role.Field` for it, and the `text` literal is skipped as a non-identifier leaf.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests|FullyQualifiedName~NgrGrammarTests|FullyQualifiedName~LoweredLanguageTests"`
Expected: PASS.

- [ ] **Step 5: Update the recovery snapshot if it changed**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~CommitSnapshotTests"`
If `Nitrogen.commits.txt` fails, regenerate it with `NITROGEN_UPDATE_SNAPSHOTS=1` (see Conventions), check that `git diff Nitrogen.Tests/Grammar/Snapshots/Recovery/Nitrogen.commits.txt` only mentions `LowersOptionalTextArgument`/`LowersArgument`, and rerun to PASS.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Ngr Nitrogen.Cli/NgrAssist.cs Nitrogen.Tests/Grammar
git commit -m "Map optional text arguments in the self-hosted grammar

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Reshape `Nitrogen.ngr` without changing the model

Every field must become lowerable in Task 6. This task only restructures; `SelfHostingTests` prove the `GrammarModel` is unchanged.

**Files:**
- Modify: `Nitrogen.Ngr/Nitrogen.ngr:35-112`
- Modify: `Nitrogen.Ngr/NgrMapper.cs`
- Test: existing `SelfHostingTests`, `NgrGrammarTests`, `RecoverySoundnessTests`, `CommitSnapshotTests`, LanguageService tests

- [ ] **Step 1: Confirm the baseline passes**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests|FullyQualifiedName~NgrGrammarTests"`
Expected: PASS. These are the regression tests for this task.

- [ ] **Step 2: Replace the rules of `Nitrogen.ngr`**

Keep the header comment, tokens, `CsAtTail`/`CsMessage` and the `symbols { module rule property }` block unchanged. Replace everything from `syntax File` to the closing `}` with:

```ngr
  syntax File           = Modules:Module*;
  syntax Module         = "syntax" "module" Name:QualifiedName "{" Members:Member* "}"  declares module Name scope;
  syntax Member         = Using / Symbols / SymbolProperty / Builtin / TokenRule / SyntaxRule / ExtensibleRule / Extend;
  syntax Symbols        = "symbols" "{" Kinds:Word+ "}";
  syntax Word           = Value:Identifier;
  syntax Builtin        = "builtin" SymbolKind:Identifier Within:InKind? "{" Names:BuiltinName+ "}";
  syntax BuiltinName    = Value:QualifiedName;
  syntax InKind         = "in" Name:Identifier;
  syntax Using          = "using" Module:QualifiedName ";"  references? module Module;
  syntax TokenRule      = "token" Name:Identifier "=" Body:Expression Except:ExceptList? ";"  declares rule Name;
  syntax ExceptList     = "except" Words:ExceptWord+;
  syntax ExceptWord     = Value:String;
  syntax SyntaxRule     = "syntax" Name:Identifier "=" Body:Expression Clauses:Clause* End:(Terminator / RuleSemantics)  declares rule Name scope;
  syntax Terminator     = ";";
  syntax RuleSemantics  = Block:Semantics;
  syntax ExtensibleRule = "extensible" "syntax" Name:Identifier "{" Properties:PropertyDecl* Alternatives:Alternative* "}"  declares rule Name scope;
  syntax Extend         = "extend" "syntax" Target:QualifiedName "{" Alternatives:Alternative* "}"  references? rule Target;
  syntax Alternative    = "|" Named:AlternativeName? Body:Expression Precedence:PrecedenceSpec? Clauses:Clause* Semantics:Semantics?;
  syntax AlternativeName = Name:Identifier "=";
  syntax PrecedenceSpec = "precedence" Level:Integer Associativity:Associativity?;
  syntax Associativity  = "left" / "right";

  // Binding clauses (issue 237).
  syntax Clause         = Declares / References / ScopeClause / DynamicClause / Lowers;
  syntax Declares       = "declares" SymbolKind:Identifier Field:Identifier Sequential:"sequential"? FileScope:("in" "file")? Export:"export"? Type:DeclaredType?;
  syntax DeclaredType   = "type" Name:QualifiedName;
  syntax References     = "references" Question:"?"? Kinds:KindList Field:Identifier Within:InKind?;
  syntax KindList       = KindGroup / SingleKind;
  syntax KindGroup      = "(" Kinds:(Word; "|")+ ")";
  syntax SingleKind     = Name:Identifier;
  syntax ScopeClause    = "scope";
  syntax DynamicClause  = "dynamic";

  // Declarative typing and lowering (issue 251).
  syntax Lowers         = "lowers" Form:(LowersLiteral / LowersText / LowersSequence / LowersValue / LowersReference / LowersRepeat / LowersTemplate / LowersExpand / LowersCall);
  syntax LowersTemplate = "template" Field:Identifier "(" Parameters:Identifier Close:")";
  syntax LowersExpand   = "expand" Field:Identifier "(" Arguments:Identifier Close:")";
  syntax LowersRepeat   = "repeat" Type:QualifiedName Count:Identifier Iterator:Identifier Children:Identifier;
  syntax LowersLiteral  = "literal" Type:QualifiedName Field:Identifier;
  syntax LowersText     = "text" Type:QualifiedName Field:Identifier;
  syntax LowersSequence = "sequence" Type:QualifiedName Field:Identifier;
  syntax LowersValue    = "value" Type:(ComputedType / FixedType) Field:Identifier;
  syntax ComputedType   = "type" Property:Identifier;
  syntax FixedType      = Name:QualifiedName;
  syntax LowersReference = "reference" "type" Property:Identifier Initializer:InitializerSpec?;
  syntax InitializerSpec = "initializer" Name:Identifier;
  syntax LowersCall     = Operation:(ComputedOperation / FixedOperation) "(" Arguments:(LowersArgument; ",")* Close:")";
  syntax ComputedOperation = "operation" Question:"?"? Property:Identifier;
  syntax FixedOperation = Name:QualifiedName;
  syntax LowersArgument = LowersSequenceArgument / LowersOptionalTextArgument / LowersOptionalArgument / LowersTextArgument / FieldArgument;
  syntax LowersSequenceArgument = "sequence" Type:QualifiedName Field:Identifier;
  syntax LowersOptionalTextArgument = "optional" "text" Field:Identifier;
  syntax LowersOptionalArgument = "optional" Type:QualifiedName Field:Identifier;
  syntax LowersTextArgument = "text" Field:Identifier;
  syntax FieldArgument  = Field:Identifier;

  // Semantics (issue 239).
  syntax SymbolProperty = "symbol" "property" Name:Identifier "for" Kinds:KindList ":" Type:CsType "=" Default:CsCode ";"  declares property Name;
  syntax Semantics      = "{" Items:SemanticItem* "}";
  syntax SemanticItem   = PropertyDecl / Check / Assignment;
  syntax PropertyDecl   = Direction:("out" / "in") Flags:PropertyFlag* Name:Identifier ":" Type:CsType "=" Default:CsCode ";"  declares property Name;
  syntax PropertyFlag   = "hover" / "expected";
  syntax Check          = "check" Code:CheckCode? Condition:CsCond ":" Message:CsMessage At:CheckAt? ";";
  syntax CheckAt        = "at" Name:Identifier;
  syntax Assignment     = Target:QualifiedName "=" Value:CsCode ";"  references? property Target;

  syntax Expression     = Choices:(Sequence; "/")+;
  syntax Sequence       = Elements:Element+;
  syntax Element        = Label:LabelPrefix? Body:Unary;
  syntax LabelPrefix    = Name:Identifier ":";
  syntax Unary          = Prefixes:PredicateOp* Body:Postfix;
  syntax PredicateOp    = "!" / "&";
  syntax Postfix        = Body:Primary Operators:RepeatOp*;
  syntax RepeatOp       = "?" / "*" / "+";
  syntax Primary        = StringLiteral / CharClass / Any / Parenthesized / Reference;
  syntax StringLiteral  = Value:String;
  syntax Any            = ".";
  syntax Parenthesized  = "(" Inner:Expression Tail:(SeparatorTail / GroupClose);
  syntax SeparatorTail  = ";" Separator:Expression ")" Op:("*" / "+");
  syntax GroupClose     = ")";
  syntax Reference      = !"precedence" !"except" !"declares" !"references" !"scope" !"dynamic" !"lowers" Name:QualifiedName  references? rule Name;
  syntax QualifiedName  = DottedName / Identifier;
  syntax CharClass      = "[" Negated:"^"? Items:ClassItem+ "]";
  syntax ClassItem      = First:Char Last:RangeEnd?;
  syntax RangeEnd       = ".." Last:Char;
}
```

- [ ] **Step 3: Build and list the mapper errors**

Run: `dotnet build Nitrogen.Ngr/Nitrogen.Ngr.csproj`
Expected: errors in `NgrMapper.cs` where views changed from tokens or groups to rule nodes. Generated views have only `Tree`, `Index`, `Span`, `IsMissing`, `IsSkipped` and their labeled fields: no `ToString`, `Kind` or `Child`.

- [ ] **Step 4: Update `NgrMapper.cs`**

TokenRule `except`:

```csharp
                if (r.Except.HasValue)
                {
                    foreach (var word in r.Except.Value.Words)
                    {
                        string value = Unquote(word.Value.ToString());
                        if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(word.Span));
                        except.Add(new LiteralExpr(value, Span(word.Span)));
                    }
                }
```

SyntaxRule end:

```csharp
                SemanticsBlock? semantics = r.End.Kind == NitrogenKinds.RuleSemantics
                    ? Semantics(Cast<RuleSemanticsNode>(r.End).Block) : null;
```

Symbols kinds: `foreach (var k in s.Kinds) kinds.Add(Name(k.Value));`

Builtin:

```csharp
                foreach (var n in b.Names) names.Add(new NameDecl(n.Value.ToString(), Span(n.Value.Span)));
                NameDecl? scope = null;
                if (b.Within.HasValue)
                {
                    var rule = b.Within.Value.Name;
                    scope = new NameDecl(rule.ToString(), Span(rule.Span));
                }
```

`Alternatives`, name and precedence:

```csharp
            string? name = node.Named.HasValue ? node.Named.Value.Name.ToString() : null;
            int end = body.Span.End;
            int? precedence = null;
            GrammarAssociativity? associativity = null;
            if (node.Precedence.HasValue)
            {
                var group = node.Precedence.Value;
                end = group.Span.End;
                var number = group.Level;
                string digits = number.ToString();
                if (digits.Length > 9) throw new NgrMappingException("precedence is too large", Span(number.Span));
                precedence = int.Parse(digits, CultureInfo.InvariantCulture);
                if (group.Associativity.HasValue)
                    associativity = Text(group.Associativity.Value.Index) == "left" ? GrammarAssociativity.Left : GrammarAssociativity.Right;
            }
```

`Clauses`, Declares type:

```csharp
                if (d.Type.HasValue)
                {
                    var name = d.Type.Value.Name;
                    type = new NameDecl(name.ToString(), Span(name.Span));
                    end = name.Span.End;
                }
```

`Clauses`, LowersReference initializer:

```csharp
                    var property = l.Form.Child(2);
                    var source = Cast<LowersReferenceNode>(l.Form).Initializer;
                    Token? initializer = source.HasValue ? source.Value.Name : null;
```

The rest of that branch (`initializer?.Span.End`, `initializer is { } value`) is unchanged.

`Clauses`, References qualifier:

```csharp
                if (r.Within.HasValue)
                {
                    var qualifierKind = r.Within.Value.Name;
                    qualifier = new NameDecl(qualifierKind.ToString(), Span(qualifierKind.Span));
                    end = qualifierKind.Span.End;
                }
```

`Kinds`: `foreach (var k in Cast<KindGroupNode>(node).Kinds) kinds.Add(Name(k.Value));` The single-kind branch is unchanged because `KindList` is still an alias.

`Element`:

```csharp
        var label = node.Label.Value;
        return new LabeledExpr(label.Name.ToString(), inner, GrammarSpan.FromBounds(label.Span.Start, inner.Span.End));
```

`Primary`: `if (kind == NitrogenKinds.StringLiteral)` (the body, `Unquote(node.ToString())`, is unchanged because the node spans the token).

`CharClass`:

```csharp
            if (item.Last.HasValue)
            {
                var lastToken = item.Last.Value.Last;
                last = Unquote(lastToken.ToString())[0];
```

Unchanged on purpose, because child positions inside the new nodes match the old groups:
- `LowersValue` (`type.ChildCount == 2 && type.Child(0).ToString() == "type"` is still true of `ComputedType`; `FixedType` has one child)
- `LowersCall` (`ComputedOperation` keeps `"operation"`, `"?"`, `Identifier`)
- the `Check` `at` lookup (`tree.Child(c.At.Value.Index, 1)`)
- the plain-field argument (`argument.ToString()` on the `FieldArgument` node)
- `Parenthesized` (`Tail.Kind != SeparatorTail`)

- [ ] **Step 5: Build, then run the regression tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: 0 warnings, 0 errors.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests|FullyQualifiedName~NgrGrammarTests|FullyQualifiedName~RecoverySoundnessTests|FullyQualifiedName~LanguageService|FullyQualifiedName~GrammarLoopTests"`
Expected: PASS. If a `SelfHostingTests` row fails, the dump diff names the field whose span or value moved. Fix the mapper; do not change the bootstrap parser.

- [ ] **Step 6: Update the recovery snapshot**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~CommitSnapshotTests"`
Expected: `Nitrogen.commits.txt` FAILs because the rules changed. Regenerate with `NITROGEN_UPDATE_SNAPSHOTS=1`, review that `git diff` only renames or adds the reshaped rules, and rerun to PASS.

- [ ] **Step 7: Commit**

```bash
git add Nitrogen.Ngr Nitrogen.Tests/Grammar/Snapshots
git commit -m "Reshape the self-hosted grammar so every field can lower

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The `Grammar` semantic module

**Files:**
- Create: `Nitrogen.Ngr/GrammarSemantics.cs`
- Create: `Nitrogen.Tests/Grammar/GrammarSemanticsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Nitrogen.Ngr;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarSemanticsTests
{
    [Fact]
    public void The_grammar_module_composes_with_core()
    {
        var catalog = SemanticCatalog.Compose([GrammarSemantics.Module], out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.NotNull(catalog);
        Assert.Equal("Grammar", GrammarSemantics.Module.Name);
        Assert.Equal(["Core"], GrammarSemantics.Module.Imports);
        Assert.Equal(18, GrammarSemantics.Module.Types.Count);
        Assert.All(GrammarSemantics.Module.Operations, operation => Assert.StartsWith("Grammar.", operation.Id));
        Assert.Equal(GrammarSemantics.Types.File, catalog!.Operations["Grammar.File"].Result);
        Assert.Equal(SemanticTypes.SequenceOf(GrammarSemantics.Types.Module), catalog.Operations["Grammar.File"].Inputs[0]);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarSemanticsTests"`
Expected: build error `The name 'GrammarSemantics' does not exist`.

- [ ] **Step 3: Implement `Nitrogen.Ngr/GrammarSemantics.cs`**

```csharp
using Nitrogen.Semantic;

namespace Nitrogen.Ngr;

/// <summary>
/// What Nitrogen.ngr's <c>lowers</c> clauses lower a grammar to: one type per kind of construct, one
/// operation per concrete rule. Text is the raw source spelling; quoted literals keep their quotes and
/// C# keeps its spacing. Nothing executes these operations; they type and describe a grammar.
/// </summary>
public static class GrammarSemantics
{
    public static class Types
    {
        public static readonly SemanticType File = Named("File");
        public static readonly SemanticType Module = Named("Module");
        public static readonly SemanticType Member = Named("Member");
        public static readonly SemanticType Except = Named("Except");
        public static readonly SemanticType End = Named("End");
        public static readonly SemanticType Alternative = Named("Alternative");
        public static readonly SemanticType Precedence = Named("Precedence");
        public static readonly SemanticType Clause = Named("Clause");
        public static readonly SemanticType Kinds = Named("Kinds");
        public static readonly SemanticType LowersForm = Named("LowersForm");
        public static readonly SemanticType TypeSpec = Named("TypeSpec");
        public static readonly SemanticType OperationSpec = Named("OperationSpec");
        public static readonly SemanticType LoweringArgument = Named("LoweringArgument");
        public static readonly SemanticType Semantics = Named("Semantics");
        public static readonly SemanticType SemanticItem = Named("SemanticItem");
        public static readonly SemanticType Expression = Named("Expression");
        public static readonly SemanticType Tail = Named("Tail");
        public static readonly SemanticType ClassItem = Named("ClassItem");

        internal static readonly SemanticType[] All =
        [
            File, Module, Member, Except, End, Alternative, Precedence, Clause, Kinds, LowersForm, TypeSpec,
            OperationSpec, LoweringArgument, Semantics, SemanticItem, Expression, Tail, ClassItem,
        ];

        static SemanticType Named(string name) => SemanticType.Named("Grammar", name);
    }

    static readonly SemanticType Text = SemanticTypes.Text;

    static SemanticType Seq(SemanticType element) => SemanticTypes.SequenceOf(element);

    static SemanticType Opt(SemanticType element) => SemanticTypes.OptionalOf(element);

    static OperationSignature Op(string name, SemanticType result, params SemanticType[] inputs) =>
        new("Grammar." + name, result, inputs);

    static readonly OperationSignature[] s_operations =
    [
        Op("File", Types.File, Seq(Types.Module)),
        Op("Module", Types.Module, Text, Seq(Types.Member)),
        Op("Using", Types.Member, Text),
        Op("Symbols", Types.Member, Seq(Text)),
        Op("SymbolProperty", Types.Member, Text, Types.Kinds, Text, Text),
        Op("Builtin", Types.Member, Text, Opt(Text), Seq(Text)),
        Op("TokenRule", Types.Member, Text, Types.Expression, Opt(Types.Except)),
        Op("Except", Types.Except, Seq(Text)),
        Op("SyntaxRule", Types.Member, Text, Types.Expression, Seq(Types.Clause), Types.End),
        Op("Terminator", Types.End),
        Op("RuleSemantics", Types.End, Types.Semantics),
        Op("ExtensibleRule", Types.Member, Text, Seq(Types.SemanticItem), Seq(Types.Alternative)),
        Op("Extend", Types.Member, Text, Seq(Types.Alternative)),
        Op("Alternative", Types.Alternative, Opt(Text), Types.Expression, Opt(Types.Precedence), Seq(Types.Clause), Opt(Types.Semantics)),
        Op("Precedence", Types.Precedence, Text, Opt(Text)),
        Op("Declares", Types.Clause, Text, Text, Opt(Text), Opt(Text), Opt(Text), Opt(Text)),
        Op("References", Types.Clause, Opt(Text), Types.Kinds, Text, Opt(Text)),
        Op("KindGroup", Types.Kinds, Seq(Text)),
        Op("SingleKind", Types.Kinds, Text),
        Op("Scope", Types.Clause),
        Op("Dynamic", Types.Clause),
        Op("Lowers", Types.Clause, Types.LowersForm),
        Op("LowersTemplate", Types.LowersForm, Text, Text),
        Op("LowersExpand", Types.LowersForm, Text, Text),
        Op("LowersRepeat", Types.LowersForm, Text, Text, Text, Text),
        Op("LowersLiteral", Types.LowersForm, Text, Text),
        Op("LowersText", Types.LowersForm, Text, Text),
        Op("LowersSequence", Types.LowersForm, Text, Text),
        Op("LowersValue", Types.LowersForm, Types.TypeSpec, Text),
        Op("ComputedType", Types.TypeSpec, Text),
        Op("FixedType", Types.TypeSpec, Text),
        Op("LowersReference", Types.LowersForm, Text, Opt(Text)),
        Op("LowersCall", Types.LowersForm, Types.OperationSpec, Seq(Types.LoweringArgument)),
        Op("ComputedOperation", Types.OperationSpec, Opt(Text), Text),
        Op("FixedOperation", Types.OperationSpec, Text),
        Op("SequenceArgument", Types.LoweringArgument, Text, Text),
        Op("OptionalArgument", Types.LoweringArgument, Text, Text),
        Op("OptionalTextArgument", Types.LoweringArgument, Text),
        Op("TextArgument", Types.LoweringArgument, Text),
        Op("FieldArgument", Types.LoweringArgument, Text),
        Op("Semantics", Types.Semantics, Seq(Types.SemanticItem)),
        Op("PropertyDecl", Types.SemanticItem, Text, Seq(Text), Text, Text, Text),
        Op("Check", Types.SemanticItem, Opt(Text), Text, Text, Opt(Text)),
        Op("Assignment", Types.SemanticItem, Text, Text),
        Op("Choice", Types.Expression, Seq(Types.Expression)),
        Op("Sequence", Types.Expression, Seq(Types.Expression)),
        Op("Element", Types.Expression, Opt(Text), Types.Expression),
        Op("Unary", Types.Expression, Seq(Text), Types.Expression),
        Op("Postfix", Types.Expression, Types.Expression, Seq(Text)),
        Op("Literal", Types.Expression, Text),
        Op("Any", Types.Expression),
        Op("CharClass", Types.Expression, Opt(Text), Seq(Types.ClassItem)),
        Op("ClassItem", Types.ClassItem, Text, Opt(Text)),
        Op("Parenthesized", Types.Expression, Types.Expression, Types.Tail),
        Op("SeparatorTail", Types.Tail, Types.Expression, Text),
        Op("GroupClose", Types.Tail),
        Op("Reference", Types.Expression, Text),
    ];

    /// <summary>The <c>Grammar</c> semantic module: imports Core, exports the types and operations above.</summary>
    public static SemanticModule Module { get; } = new("Grammar", ["Core"], Types.All, s_operations);
}
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarSemanticsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Ngr/GrammarSemantics.cs Nitrogen.Tests/Grammar/GrammarSemanticsTests.cs
git commit -m "Add the Grammar semantic module for the self-hosted grammar

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `lowers` clauses, wiring, and coverage

**Files:**
- Modify: `Nitrogen.Ngr/Nitrogen.ngr`
- Modify: `Nitrogen.Ngr/NgrParser.cs:14`
- Create: `Nitrogen.Tests/Grammar/GrammarLoweringTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Binding;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Nitrogen.ngr's lowers clauses: every .ngr file lowers to one typed Grammar.File root.</summary>
public sealed class GrammarLoweringTests
{
    public static TheoryData<string> RepositoryGrammars()
    {
        string root = AppContext.BaseDirectory;
        var data = new TheoryData<string>();
        foreach (string path in Directory.GetFiles(Path.Combine(root, "Grammars"), "*.ngr").Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(root, path));
        data.Add(Path.Combine("GeometryLanguage", "Geometry.ngr"));
        data.Add(Path.Combine("DateCalcLanguage", "DateCalc.ngr"));
        return data;
    }

    [Theory]
    [MemberData(nameof(RepositoryGrammars))]
    public void Every_repository_grammar_lowers_to_one_grammar_file(string relative)
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relative));
        using var parsed = NgrParser.Language.Parse(text, NitrogenModule.File);
        Assert.True(parsed.Success, parsed.Success ? "" : parsed.FormatMessage(parsed.Diagnostics[0]));
        var project = new Project(NgrParser.Language);
        project.Set(relative, parsed.Tree);
        var file = new ProjectSemantics(project)[relative];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, NgrParser.Language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal("Grammar.File", root.Signature.Id);
        Assert.Equal(GrammarSemantics.Types.File, root.Type);
    }

    [Fact]
    public void A_grammar_lowers_its_structure_as_raw_text()
    {
        const string source = "syntax module M { token T = ['a'..'z' '_']+; syntax R = \"r\" Items:(T; \",\")* lowers M.Build(sequence M.Part Items); }";
        WithRoot(source, root =>
        {
            var module = Op(Assert.Single(Seq(root.Arguments[0])), "Module");
            Assert.Equal("M", Text(module.Arguments[0]));
            var members = Seq(module.Arguments[1]);
            Assert.Equal(2, members.Count);

            var token = Op(members[0], "TokenRule");
            Assert.Equal("T", Text(token.Arguments[0]));
            Assert.Null(Opt(token.Arguments[2]));
            var postfix = Postfix(token.Arguments[1]);
            Assert.Equal(["+"], Seq(postfix.Arguments[1]).Select(Text));
            var charClass = Op(postfix.Arguments[0], "CharClass");
            Assert.Null(Opt(charClass.Arguments[0]));
            var items = Seq(charClass.Arguments[1]).Select(item => Op(item, "ClassItem")).ToList();
            Assert.Equal(("'a'", "'z'"), (Text(items[0].Arguments[0]), Text(Opt(items[0].Arguments[1])!)));
            Assert.Equal("'_'", Text(items[1].Arguments[0]));
            Assert.Null(Opt(items[1].Arguments[1]));

            var rule = Op(members[1], "SyntaxRule");
            Assert.Equal("R", Text(rule.Arguments[0]));
            Op(rule.Arguments[3], "Terminator");
            var lowers = Op(Assert.Single(Seq(rule.Arguments[2])), "Lowers");
            var call = Op(lowers.Arguments[0], "LowersCall");
            Assert.Equal("M.Build", Text(Op(call.Arguments[0], "FixedOperation").Arguments[0]));
            var argument = Op(Assert.Single(Seq(call.Arguments[1])), "SequenceArgument");
            Assert.Equal(("M.Part", "Items"), (Text(argument.Arguments[0]), Text(argument.Arguments[1])));

            var elements = Seq(Op(Assert.Single(Seq(Op(rule.Arguments[1], "Choice").Arguments[0])), "Sequence").Arguments[0]);
            Assert.Equal(2, elements.Count);
            Assert.Equal("\"r\"", Text(Op(PostfixOf(elements[0]).Arguments[0], "Literal").Arguments[0]));
            var labeled = Op(elements[1], "Element");
            Assert.Equal("Items", Text(Opt(labeled.Arguments[0])!));
            var list = PostfixOf(elements[1]);
            Assert.Empty(Seq(list.Arguments[1]));
            var group = Op(list.Arguments[0], "Parenthesized");
            var tail = Op(group.Arguments[1], "SeparatorTail");
            Assert.Equal("*", Text(tail.Arguments[1]));
            Assert.Equal("\",\"", Text(Op(Postfix(tail.Arguments[0]).Arguments[0], "Literal").Arguments[0]));
        });
    }

    [Fact]
    public void Optional_keywords_and_semantics_lower_with_presence()
    {
        const string source = "syntax module M { symbols { v } token T = ['a'..'z']+; syntax R = Name:T declares v Name export { out O : int = 0; check O >= 0 : \"bad\" at Name; } }";
        WithRoot(source, root =>
        {
            var module = Op(Assert.Single(Seq(root.Arguments[0])), "Module");
            var rule = Op(Seq(module.Arguments[1])[2], "SyntaxRule");
            var declares = Op(Assert.Single(Seq(rule.Arguments[2])), "Declares");
            Assert.Equal(("v", "Name"), (Text(declares.Arguments[0]), Text(declares.Arguments[1])));
            Assert.Null(Opt(declares.Arguments[2]));
            Assert.Null(Opt(declares.Arguments[3]));
            Assert.Equal("export", Text(Opt(declares.Arguments[4])!));
            Assert.Null(Opt(declares.Arguments[5]));

            var semantics = Op(Op(rule.Arguments[3], "RuleSemantics").Arguments[0], "Semantics");
            var items = Seq(semantics.Arguments[0]);
            var property = Op(items[0], "PropertyDecl");
            Assert.Equal(("out", "O"), (Text(property.Arguments[0]), Text(property.Arguments[2])));
            Assert.Empty(Seq(property.Arguments[1]));
            var check = Op(items[1], "Check");
            Assert.Null(Opt(check.Arguments[0]));
            Assert.Equal("Name", Text(Opt(check.Arguments[3])!));
        });
    }

    static void WithRoot(string source, Action<HirOperation> inspect)
    {
        using var parsed = NgrParser.Language.Parse(source, NitrogenModule.File);
        Assert.True(parsed.Success);
        var project = new Project(NgrParser.Language);
        project.Set("m.ngr", parsed.Tree);
        var file = new ProjectSemantics(project)["m.ngr"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, NgrParser.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        inspect(Op(Assert.Single(lowered.Roots), "File"));
    }

    static HirOperation Op(HirNode node, string name)
    {
        var operation = Assert.IsType<HirOperation>(node);
        Assert.Equal("Grammar." + name, operation.Signature.Id);
        return operation;
    }

    static string Text(HirNode node) => Assert.IsType<HirText>(node).Value;

    static HirNode? Opt(HirNode node) => Assert.IsType<HirOptional>(node).Value;

    static IReadOnlyList<HirNode> Seq(HirNode node) => Assert.IsType<HirSequence>(node).Items;

    /// <summary>The postfix of an expression that is one choice of one element.</summary>
    static HirOperation Postfix(HirNode expression) =>
        PostfixOf(Assert.Single(Seq(Op(Assert.Single(Seq(Op(expression, "Choice").Arguments[0])), "Sequence").Arguments[0])));

    /// <summary>The postfix of an element: Element → Unary → Postfix.</summary>
    static HirOperation PostfixOf(HirNode element) =>
        Op(Op(Op(element, "Element").Arguments[1], "Unary").Arguments[1], "Postfix");
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarLoweringTests"`
Expected: FAIL. `NgrParser.Language` has no catalog, so `lowered.Roots` is empty.

- [ ] **Step 3: Add the clauses to `Nitrogen.ngr`**

Replace the rules (same span as Task 4) with:

```ngr
  syntax File           = Modules:Module*  lowers Grammar.File(sequence Grammar.Module Modules);
  syntax Module         = "syntax" "module" Name:QualifiedName "{" Members:Member* "}"  declares module Name scope
                          lowers Grammar.Module(text Name, sequence Grammar.Member Members);
  syntax Member         = Using / Symbols / SymbolProperty / Builtin / TokenRule / SyntaxRule / ExtensibleRule / Extend;
  syntax Symbols        = "symbols" "{" Kinds:Word+ "}"  lowers Grammar.Symbols(sequence Core.Text Kinds);
  syntax Word           = Value:Identifier  lowers text Core.Text Value;
  syntax Builtin        = "builtin" SymbolKind:Identifier Within:InKind? "{" Names:BuiltinName+ "}"
                          lowers Grammar.Builtin(text SymbolKind, optional Core.Text Within, sequence Core.Text Names);
  syntax BuiltinName    = Value:QualifiedName  lowers text Core.Text Value;
  syntax InKind         = "in" Name:Identifier  lowers text Core.Text Name;
  syntax Using          = "using" Module:QualifiedName ";"  references? module Module  lowers Grammar.Using(text Module);
  syntax TokenRule      = "token" Name:Identifier "=" Body:Expression Except:ExceptList? ";"  declares rule Name
                          lowers Grammar.TokenRule(text Name, Body, optional Grammar.Except Except);
  syntax ExceptList     = "except" Words:ExceptWord+  lowers Grammar.Except(sequence Core.Text Words);
  syntax ExceptWord     = Value:String  lowers text Core.Text Value;
  syntax SyntaxRule     = "syntax" Name:Identifier "=" Body:Expression Clauses:Clause* End:(Terminator / RuleSemantics)  declares rule Name scope
                          lowers Grammar.SyntaxRule(text Name, Body, sequence Grammar.Clause Clauses, End);
  syntax Terminator     = ";"  lowers Grammar.Terminator();
  syntax RuleSemantics  = Block:Semantics  lowers Grammar.RuleSemantics(Block);
  syntax ExtensibleRule = "extensible" "syntax" Name:Identifier "{" Properties:PropertyDecl* Alternatives:Alternative* "}"  declares rule Name scope
                          lowers Grammar.ExtensibleRule(text Name, sequence Grammar.SemanticItem Properties, sequence Grammar.Alternative Alternatives);
  syntax Extend         = "extend" "syntax" Target:QualifiedName "{" Alternatives:Alternative* "}"  references? rule Target
                          lowers Grammar.Extend(text Target, sequence Grammar.Alternative Alternatives);
  syntax Alternative    = "|" Named:AlternativeName? Body:Expression Precedence:PrecedenceSpec? Clauses:Clause* Semantics:Semantics?
                          lowers Grammar.Alternative(optional Core.Text Named, Body, optional Grammar.Precedence Precedence,
                            sequence Grammar.Clause Clauses, optional Grammar.Semantics Semantics);
  syntax AlternativeName = Name:Identifier "="  lowers text Core.Text Name;
  syntax PrecedenceSpec = "precedence" Level:Integer Associativity:Associativity?
                          lowers Grammar.Precedence(text Level, optional Core.Text Associativity);
  syntax Associativity  = "left" / "right"  lowers text Core.Text this;

  // Binding clauses (issue 237).
  syntax Clause         = Declares / References / ScopeClause / DynamicClause / Lowers;
  syntax Declares       = "declares" SymbolKind:Identifier Field:Identifier Sequential:"sequential"? FileScope:("in" "file")? Export:"export"? Type:DeclaredType?
                          lowers Grammar.Declares(text SymbolKind, text Field, optional text Sequential, optional text FileScope,
                            optional text Export, optional Core.Text Type);
  syntax DeclaredType   = "type" Name:QualifiedName  lowers text Core.Text Name;
  syntax References     = "references" Question:"?"? Kinds:KindList Field:Identifier Within:InKind?
                          lowers Grammar.References(optional text Question, Kinds, text Field, optional Core.Text Within);
  syntax KindList       = KindGroup / SingleKind;
  syntax KindGroup      = "(" Kinds:(Word; "|")+ ")"  lowers Grammar.KindGroup(sequence Core.Text Kinds);
  syntax SingleKind     = Name:Identifier  lowers Grammar.SingleKind(text Name);
  syntax ScopeClause    = "scope"  lowers Grammar.Scope();
  syntax DynamicClause  = "dynamic"  lowers Grammar.Dynamic();

  // Declarative typing and lowering (issue 251).
  syntax Lowers         = "lowers" Form:(LowersLiteral / LowersText / LowersSequence / LowersValue / LowersReference / LowersRepeat / LowersTemplate / LowersExpand / LowersCall)
                          lowers Grammar.Lowers(Form);
  syntax LowersTemplate = "template" Field:Identifier "(" Parameters:Identifier Close:")"  lowers Grammar.LowersTemplate(text Field, text Parameters);
  syntax LowersExpand   = "expand" Field:Identifier "(" Arguments:Identifier Close:")"  lowers Grammar.LowersExpand(text Field, text Arguments);
  syntax LowersRepeat   = "repeat" Type:QualifiedName Count:Identifier Iterator:Identifier Children:Identifier
                          lowers Grammar.LowersRepeat(text Type, text Count, text Iterator, text Children);
  syntax LowersLiteral  = "literal" Type:QualifiedName Field:Identifier  lowers Grammar.LowersLiteral(text Type, text Field);
  syntax LowersText     = "text" Type:QualifiedName Field:Identifier  lowers Grammar.LowersText(text Type, text Field);
  syntax LowersSequence = "sequence" Type:QualifiedName Field:Identifier  lowers Grammar.LowersSequence(text Type, text Field);
  syntax LowersValue    = "value" Type:(ComputedType / FixedType) Field:Identifier  lowers Grammar.LowersValue(Type, text Field);
  syntax ComputedType   = "type" Property:Identifier  lowers Grammar.ComputedType(text Property);
  syntax FixedType      = Name:QualifiedName  lowers Grammar.FixedType(text Name);
  syntax LowersReference = "reference" "type" Property:Identifier Initializer:InitializerSpec?
                          lowers Grammar.LowersReference(text Property, optional Core.Text Initializer);
  syntax InitializerSpec = "initializer" Name:Identifier  lowers text Core.Text Name;
  syntax LowersCall     = Operation:(ComputedOperation / FixedOperation) "(" Arguments:(LowersArgument; ",")* Close:")"
                          lowers Grammar.LowersCall(Operation, sequence Grammar.LoweringArgument Arguments);
  syntax ComputedOperation = "operation" Question:"?"? Property:Identifier  lowers Grammar.ComputedOperation(optional text Question, text Property);
  syntax FixedOperation = Name:QualifiedName  lowers Grammar.FixedOperation(text Name);
  syntax LowersArgument = LowersSequenceArgument / LowersOptionalTextArgument / LowersOptionalArgument / LowersTextArgument / FieldArgument;
  syntax LowersSequenceArgument = "sequence" Type:QualifiedName Field:Identifier  lowers Grammar.SequenceArgument(text Type, text Field);
  syntax LowersOptionalTextArgument = "optional" "text" Field:Identifier  lowers Grammar.OptionalTextArgument(text Field);
  syntax LowersOptionalArgument = "optional" Type:QualifiedName Field:Identifier  lowers Grammar.OptionalArgument(text Type, text Field);
  syntax LowersTextArgument = "text" Field:Identifier  lowers Grammar.TextArgument(text Field);
  syntax FieldArgument  = Field:Identifier  lowers Grammar.FieldArgument(text Field);

  // Semantics (issue 239).
  syntax SymbolProperty = "symbol" "property" Name:Identifier "for" Kinds:KindList ":" Type:CsType "=" Default:CsCode ";"  declares property Name
                          lowers Grammar.SymbolProperty(text Name, Kinds, text Type, text Default);
  syntax Semantics      = "{" Items:SemanticItem* "}"  lowers Grammar.Semantics(sequence Grammar.SemanticItem Items);
  syntax SemanticItem   = PropertyDecl / Check / Assignment;
  syntax PropertyDecl   = Direction:("out" / "in") Flags:PropertyFlag* Name:Identifier ":" Type:CsType "=" Default:CsCode ";"  declares property Name
                          lowers Grammar.PropertyDecl(text Direction, sequence Core.Text Flags, text Name, text Type, text Default);
  syntax PropertyFlag   = "hover" / "expected"  lowers text Core.Text this;
  syntax Check          = "check" Code:CheckCode? Condition:CsCond ":" Message:CsMessage At:CheckAt? ";"
                          lowers Grammar.Check(optional text Code, text Condition, text Message, optional Core.Text At);
  syntax CheckAt        = "at" Name:Identifier  lowers text Core.Text Name;
  syntax Assignment     = Target:QualifiedName "=" Value:CsCode ";"  references? property Target  lowers Grammar.Assignment(text Target, text Value);

  syntax Expression     = Choices:(Sequence; "/")+  lowers Grammar.Choice(sequence Grammar.Expression Choices);
  syntax Sequence       = Elements:Element+  lowers Grammar.Sequence(sequence Grammar.Expression Elements);
  syntax Element        = Label:LabelPrefix? Body:Unary  lowers Grammar.Element(optional Core.Text Label, Body);
  syntax LabelPrefix    = Name:Identifier ":"  lowers text Core.Text Name;
  syntax Unary          = Prefixes:PredicateOp* Body:Postfix  lowers Grammar.Unary(sequence Core.Text Prefixes, Body);
  syntax PredicateOp    = "!" / "&"  lowers text Core.Text this;
  syntax Postfix        = Body:Primary Operators:RepeatOp*  lowers Grammar.Postfix(Body, sequence Core.Text Operators);
  syntax RepeatOp       = "?" / "*" / "+"  lowers text Core.Text this;
  syntax Primary        = StringLiteral / CharClass / Any / Parenthesized / Reference;
  syntax StringLiteral  = Value:String  lowers Grammar.Literal(text Value);
  syntax Any            = "."  lowers Grammar.Any();
  syntax Parenthesized  = "(" Inner:Expression Tail:(SeparatorTail / GroupClose)  lowers Grammar.Parenthesized(Inner, Tail);
  syntax SeparatorTail  = ";" Separator:Expression ")" Op:("*" / "+")  lowers Grammar.SeparatorTail(Separator, text Op);
  syntax GroupClose     = ")"  lowers Grammar.GroupClose();
  syntax Reference      = !"precedence" !"except" !"declares" !"references" !"scope" !"dynamic" !"lowers" Name:QualifiedName  references? rule Name
                          lowers Grammar.Reference(text Name);
  syntax QualifiedName  = DottedName / Identifier;
  syntax CharClass      = "[" Negated:"^"? Items:ClassItem+ "]"  lowers Grammar.CharClass(optional text Negated, sequence Grammar.ClassItem Items);
  syntax ClassItem      = First:Char Last:RangeEnd?  lowers Grammar.ClassItem(text First, optional Core.Text Last);
  syntax RangeEnd       = ".." Last:Char  lowers text Core.Text Last;
}
```

- [ ] **Step 4: Wire the module**

`NgrParser.cs:14`:

```csharp
    static readonly Language s_language = new LanguageBuilder().Add(NitrogenModule.Instance).AddSemantic(GrammarSemantics.Module).Build();
```

Update the `Language` doc comment to: `/// <summary>The generated <c>.ngr</c> language with the <see cref="GrammarSemantics"/> catalog, for tests and tools.</summary>`.

- [ ] **Step 5: Build, then run the tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: 0 warnings, 0 errors. A composition error from `NgrParser`'s static initializer (`SemanticCompositionException`) shows up as a test failure in the next step; its `NM0008`–`NM0011` message names the rule and operation that disagree with `GrammarSemantics`.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~GrammarLoweringTests|FullyQualifiedName~SelfHostingTests|FullyQualifiedName~NgrGrammarTests|FullyQualifiedName~RecoverySoundnessTests"`
Expected: PASS. If a repository grammar fails with `NH0003`, read the underlying diagnostic. If it is a declarative clause error (`NT…`), fix the clause or the module. If it is a duplicate declaration deliberately present in that fixture, stop and report it rather than excluding the file.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Ngr Nitrogen.Tests/Grammar/GrammarLoweringTests.cs
git commit -m "Lower every .ngr file to typed Grammar HIR

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Keep `.ngr` colouring; check the editor

**Files:**
- Modify: `Nitrogen.LanguageService/Presentation.cs:81-89`
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs:110-114`
- Modify: `Nitrogen.Cli/LspCommand.cs:18-23`
- Create: `Nitrogen.Tests/LanguageService/NgrEditorLoweringTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The ngr language with the Grammar catalog: hover says what a node lowers to, colouring is unchanged.</summary>
public sealed class NgrEditorLoweringTests
{
    const string Uri = "file:///w/m.ngr";
    const string Source = "syntax module M { token T = ['a'..'z']+; syntax R = \"r\" Name:T; }";

    static DocumentPosition At(string text, int shift = 0) => new(0, Source.IndexOf(text, StringComparison.Ordinal) + shift);

    [Fact]
    public void Ngr_tokens_are_not_coloured_from_lowering()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, Source);

        var tokens = service.SemanticTokens(Uri);

        Assert.DoesNotContain(tokens, token => token.Type == TokenType.Function);
        Assert.DoesNotContain(tokens, token => token.Start == At("Name:T", "Name:".Length) && token.Type == TokenType.String);
    }

    [Fact]
    public void Hover_over_a_grammar_node_names_what_it_lowers_to()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, Source);

        var hover = service.Hover(Uri, At("token"));

        Assert.NotNull(hover);
        Assert.Contains("`Grammar.TokenRule`", hover!.Markdown, StringComparison.Ordinal);
        Assert.Empty(service.Diagnostics(Uri));
    }
}
```

- [ ] **Step 2: Run them and confirm the colouring test fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~NgrEditorLoweringTests"`
Expected: `Ngr_tokens_are_not_coloured_from_lowering` FAILs on function tokens (keywords such as `syntax` and `token` outside operation arguments). `Hover_over_a_grammar_node_names_what_it_lowers_to` passes already, from Task 6.

- [ ] **Step 3: Implement the opt-out**

`Presentation.cs`: replace the class declaration and add the property, and extend the doc comment with
`/// <paramref name="colorFromLowering"/> false keeps lowered HIR from colouring tokens (the .ngr grammars).`

```csharp
public sealed class Presentation(IReadOnlyDictionary<string, SymbolStyle> styles, IReadOnlyDictionary<string, TokenType>? types = null,
    bool colorFromLowering = true)
{
    public static Presentation Default { get; } = new(new Dictionary<string, SymbolStyle>());

    /// <summary>Whether what a document lowers to colours its tokens.</summary>
    public bool ColorFromLowering { get; } = colorFromLowering;
```

`NitrogenLanguageService.cs`, `SemanticTokens`:

```csharp
            ? SemanticTokenBuilder.Build(document, _projects[document.Language],
                document.Language.Presentation.ColorFromLowering ? LoweredRoots(document) : [],
                AssistOf(document) is { } assisted ? document.Language.Assist!.Tokens(assisted) : null)
```

`LspCommand.cs`:

```csharp
    static Presentation Styles(bool colorFromLowering, params (string Kind, TokenType Token, OutlineKind Outline)[] styles) =>
        new(styles.ToDictionary(s => s.Kind, s => new SymbolStyle(s.Token, s.Outline)), colorFromLowering: colorFromLowering);

    // What a grammar lowers to (GrammarSemantics) shows on hover; its colours stay those of the grammar's syntax.
    static readonly Presentation NgrStyles = Styles(false,
        ("module", TokenType.Namespace, OutlineKind.Module), ("rule", TokenType.Type, OutlineKind.Class),
        ("property", TokenType.Property, OutlineKind.Property));
```

- [ ] **Step 4: Run the editor tests and the whole LanguageService suite**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~NgrEditorLoweringTests|FullyQualifiedName~LanguageService|FullyQualifiedName~GrammarLoopTests|FullyQualifiedName~Cli"`
Expected: PASS. Hover over a name in a `.ngr` file now appends the lowering summary (`HoverInfo` = name text + `\n\n` + `` `type` · `Grammar.…` ``). If an existing test asserts an exact `.ngr` name hover, update its expected text to include the summary. That change is intended (spec section 4). Do not suppress the summary.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService Nitrogen.Cli/LspCommand.cs Nitrogen.Tests/LanguageService
git commit -m "Keep .ngr colouring while the grammar lowers to HIR

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Documentation and full verification

**Files:**
- Modify: `README.md` (near line 194, the operation-argument forms; near line 368, editor support)
- Modify: `docs/superpowers/specs/2026-10-06-self-hosted-grammar-lowering-design.md:3`

- [ ] **Step 1: Document `optional text`**

In `README.md`, after the paragraph on `sequence inferred Field` (line 194), add:

```markdown
`optional text Field` passes an optional field's spelling as `Core.Optional<Core.Text>`: present, its tokens
without trivia; absent, an empty optional. It suits bare optional keywords (`Export:"export"?`). After
`optional`, `text` is reserved; a type of that name must be qualified with its module.
```

- [ ] **Step 2: Document the self-hosted lowering**

In the `.ngr` bullet of "Editor support for lowered languages" (line 370), append:

```markdown
  `Nitrogen.ngr` itself lowers every grammar to typed HIR over the `Grammar` semantic module
  (`GrammarSemantics`): hover over a rule, expression or clause shows the `Grammar` operation it lowers
  to. Its colours stay those of the grammar's syntax (`Presentation.ColorFromLowering` is off for `.ngr`).
```

- [ ] **Step 3: Mark the spec**

Change the spec's status line to `Status: stage 1 implemented (2026-10-06); stage 2 not started.`

- [ ] **Step 4: Full verification**

Run: `dotnet build Nitrogen.slnx -warnaserror --no-incremental`
Expected: 0 warnings, 0 errors.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all tests pass, and the count is higher than the baseline by the tests added in Tasks 1–7. Record the pass count in the commit message body.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/superpowers/specs/2026-10-06-self-hosted-grammar-lowering-design.md
git commit -m "Document optional text and the self-hosted grammar lowering

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
