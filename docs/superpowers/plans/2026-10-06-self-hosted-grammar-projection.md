# Self-hosted grammar projection (stage 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `NgrParser.Parse` builds `GrammarModel` from the lowered `Grammar` HIR alone, and `NgrMapper` is deleted, with results identical to the bootstrap parser's.

**Architecture:** A `LoweringAdmission.SyntaxOnly` option lets `HirLowering` lower a grammar whose binding has errors, such as duplicate declarations. `NgrProjector` turns the `Grammar.File` root into `GrammarFile`, reproducing the bootstrap span rules from HIR origins. During development an internal `NgrParser.ParseProjected` runs beside `Parse` under `SelfHostingTests`; once every row agrees, `Parse` switches over and the mapper goes.

**Tech Stack:** C# / .NET 10, xUnit, Nitrogen runtime HIR (`HirOperation`, `HirText`, `HirOptional`, `HirSequence`).

**Spec:** `docs/superpowers/specs/2026-10-06-self-hosted-grammar-projection-design.md`

**Conventions:**
- Build: `dotnet build Nitrogen.slnx -warnaserror`. Focused tests: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

**Note on the differential check:** the spec compares the projector with `NgrMapper`. This plan compares it with the bootstrap parser instead, inside `SelfHostingTests.AssertSameModel`, which already holds the mapper equal to the bootstrap parser on every row. The check is equivalent and reuses every existing row.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.Runtime/Semantic/HirLowering.cs` | `LoweringAdmission`, `LoweringContext.Admission`, gated checks | 1 |
| `Nitrogen.Tests/Semantic/LoweringAdmissionTests.cs` | admission tests | 1 |
| `Nitrogen.Tests/Grammar/SelfHostingTests.cs` | duplicate and wrapper rows; temporary projected checks | 1, 2, 3 |
| `Nitrogen.Ngr/NgrProjector.cs` | HIR → `GrammarModel`; `NgrMappingException` | 2 |
| `Nitrogen.Ngr/NgrParser.cs` | `ParseProjected`, then `Parse` | 2, 3 |
| `Nitrogen.Ngr/Nitrogen.Ngr.csproj` | `InternalsVisibleTo` Nitrogen.Tests | 2 |
| `Nitrogen.Ngr/NgrMapper.cs` | deleted | 3 |
| `docs/roadmap.md`, `README.md`, specs | docs | 4 |

---

### Task 1: Syntax-only lowering admission

**Files:**
- Modify: `Nitrogen.Runtime/Semantic/HirLowering.cs:11-23, 60-96, 136-170`
- Create: `Nitrogen.Tests/Semantic/LoweringAdmissionTests.cs`
- Modify: `Nitrogen.Tests/Grammar/SelfHostingTests.cs` (declaration-form rows)

- [ ] **Step 1: Pin the precondition: both parsers accept duplicate declarations today**

Add to `SelfHostingTests.Every_declaration_form_maps_to_the_bootstrap_model`, after the last row:

```csharp
    [InlineData("syntax module M { syntax R = \"a\"; syntax R = \"b\"; }")]
    [InlineData("syntax module M { } syntax module M { }")]
    [InlineData("syntax module M { extensible syntax E { out T : int = 0; out T : int = 1; | A = \"a\" } }")]
```

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests"`
Expected: PASS. If a row fails, stop and report: the spec's premise does not hold.

- [ ] **Step 2: Write the failing admission tests**

`Nitrogen.Tests/Semantic/LoweringAdmissionTests.cs`:

```csharp
using Nitrogen.Binding;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Semantic;

/// <summary>Lowering admission: Full blocks binding errors; SyntaxOnly blocks only recovered syntax.</summary>
public sealed class LoweringAdmissionTests
{
    const string Duplicate = "syntax module M { syntax R = \"a\"; syntax R = \"b\"; }";

    static LoweringResult Lower(string source, LoweringAdmission admission, Action<FileSemantics>? inspect = null)
    {
        using var parsed = NgrParser.Language.Parse(source, NitrogenModule.File);
        var project = new Project(NgrParser.Language);
        project.Set("m.ngr", parsed.Tree);
        var file = new ProjectSemantics(project)["m.ngr"];
        inspect?.Invoke(file);
        return HirLowering.Lower(file, NgrParser.Language.SemanticCatalog, Guid.NewGuid(), admission);
    }

    [Fact]
    public void Full_admission_blocks_a_binding_error()
    {
        var lowered = Lower(Duplicate, LoweringAdmission.Full,
            file => Assert.Contains(file.Binding.Diagnostics, diagnostic => diagnostic.Code == BindingCodes.Duplicate));

        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
    }

    [Fact]
    public void Syntax_only_admission_lowers_despite_a_binding_error()
    {
        var lowered = Lower(Duplicate, LoweringAdmission.SyntaxOnly);

        Assert.Empty(lowered.Diagnostics);
        Assert.Equal("Grammar.File", Assert.IsType<HirOperation>(Assert.Single(lowered.Roots)).Signature.Id);
    }

    [Theory]
    [InlineData(LoweringAdmission.Full)]
    [InlineData(LoweringAdmission.SyntaxOnly)]
    public void Recovered_syntax_is_blocked_under_every_admission(LoweringAdmission admission)
    {
        var lowered = Lower("syntax module M { syntax R = ; }", admission);

        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0001");
    }

    [Fact]
    public void A_context_admits_fully_by_default()
    {
        using var parsed = NgrParser.Language.Parse(Duplicate, NitrogenModule.File);
        var project = new Project(NgrParser.Language);
        project.Set("m.ngr", parsed.Tree);
        var file = new ProjectSemantics(project)["m.ngr"];

        Assert.Equal(LoweringAdmission.Full, new LoweringContext(file, Guid.NewGuid()).Admission);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~LoweringAdmissionTests"`
Expected: build error `The name 'LoweringAdmission' does not exist`.

- [ ] **Step 4: Implement**

`HirLowering.cs`, after `SemanticCheckScope`:

```csharp
/// <summary>What blocks lowering a node besides its own lowering rules.</summary>
public enum LoweringAdmission
{
    /// <summary>Recovered syntax, unresolved names, and binding or semantic errors in the subtree block it.</summary>
    Full,
    /// <summary>Only recovered syntax blocks it; for callers that validate the lowered result themselves.</summary>
    SyntaxOnly,
}
```

`LoweringContext`: add the parameter, pass it on to template contexts, and expose it:

```csharp
public sealed class LoweringContext(FileSemantics file, Guid snapshotId, SemanticCheckScope checkScope = SemanticCheckScope.WholeFile,
    LoweringAdmission admission = LoweringAdmission.Full)
```

```csharp
        : this(file, caller.SnapshotId, caller.CheckScope, caller.Admission)
```

```csharp
    public LoweringAdmission Admission { get; } = admission;
```

(next to `CheckScope`).

`Admit`, directly after the `HasRecovery` block's `return false; }`:

```csharp
        if (context.Admission == LoweringAdmission.SyntaxOnly) return true;
```

`Lower(FileSemantics, SemanticCatalog, Guid)`: add `LoweringAdmission admission = LoweringAdmission.Full` as the last parameter, build the context with `new LoweringContext(file, snapshotId, admission: admission)`, and wrap the unresolved-reference and binding/semantic checks (from `var unresolved = …` through the `NH0003` block's `continue; }`) in

```csharp
                if (admission == LoweringAdmission.Full)
                {
                    // the existing unresolved-reference and binding/semantic checks, unchanged
                }
```

Update the method's XML doc to: `/// <summary>Lower every registered root of the file; <paramref name="admission"/> chooses what blocks a root.</summary>`.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~LoweringAdmissionTests|FullyQualifiedName~HirLoweringTests|FullyQualifiedName~TemplateLoweringTests|FullyQualifiedName~SelectedLoweringTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Runtime/Semantic/HirLowering.cs Nitrogen.Tests/Semantic/LoweringAdmissionTests.cs Nitrogen.Tests/Grammar/SelfHostingTests.cs
git commit -m "Let lowering admit subtrees whose only errors are binding errors

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `NgrProjector`, checked beside the mapper

**Files:**
- Create: `Nitrogen.Ngr/NgrProjector.cs`
- Modify: `Nitrogen.Ngr/NgrMapper.cs:7-11` (move `NgrMappingException` out)
- Modify: `Nitrogen.Ngr/NgrParser.cs`
- Modify: `Nitrogen.Ngr/Nitrogen.Ngr.csproj`
- Modify: `Nitrogen.Tests/Grammar/SelfHostingTests.cs`

- [ ] **Step 1: Write the failing checks**

`Nitrogen.Ngr.csproj`, a new `ItemGroup`:

```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>Nitrogen.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
```

`SelfHostingTests.AssertSameModel`, before the final `Assert.Equal(expected, actual);`:

```csharp
        // Stage-2 cutover check (removed when Parse projects): the HIR projection gives the same model.
        var projected = NgrParser.ParseProjected(text);
        Assert.True(projected.Success, projected.Diagnostics.Count > 0 ? "projected: " + projected.Diagnostics[0] : "");
        Assert.Equal(GrammarDumper.Dump(expected), GrammarDumper.Dump(projected.File!));
        Assert.Equal(expected, projected.File);
```

`SelfHostingTests.Invalid_input_fails_in_both_parsers`, at the end:

```csharp
        var projectedFailure = NgrParser.ParseProjected(text); // stage-2 cutover check
        Assert.False(projectedFailure.Success);
        Assert.Equal(GrammarCodes.Syntax, Assert.Single(projectedFailure.Diagnostics).Code);
```

Add rows so every wrapper in the spec's table appears with its name span checked:

```csharp
    [InlineData("syntax module M { symbols { v s } builtin v in s { a b.c } }")]
    [InlineData("syntax module M { syntax R = N:A { check N != null : \"m\" at N; } }")]
    [InlineData("syntax module M { syntax R = \"r\" { check AB0001 1 > 0 : $\"x\"; } }")]
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests"`
Expected: build error `'NgrParser' does not contain a definition for 'ParseProjected'`.

- [ ] **Step 3: Write `Nitrogen.Ngr/NgrProjector.cs`**

```csharp
using System.Globalization;
using System.Text;
using Nitrogen.Grammar;
using Nitrogen.Semantic;

namespace Nitrogen.Ngr;

internal sealed class NgrMappingException(string message, GrammarSpan span) : Exception(message)
{
    public GrammarSpan Span { get; } = span;
}

/// <summary>
/// Projects the typed HIR that Nitrogen.ngr's lowers clauses produce (<see cref="GrammarSemantics"/>) to the
/// GrammarModel, following the bootstrap parser's span rules exactly. It reads operations, text, optionals,
/// sequences and their origins, never the syntax tree. An operation's origin is its rule node; an inline
/// text argument's is its token; a <c>lowers text</c> wrapper's is the wrapper node.
/// </summary>
internal static class NgrProjector
{
    public static GrammarFile File(HirNode root)
    {
        var modules = new List<ModuleDecl>();
        foreach (var module in Items(Expect(root, "File").Arguments[0])) modules.Add(Module(module));
        return new GrammarFile(modules.ToArray());
    }

    static ModuleDecl Module(HirNode node)
    {
        var module = Expect(node, "Module");
        var usings = new List<UsingDecl>();
        var rules = new List<RuleDecl>();
        var extends = new List<ExtendDecl>();
        var symbols = new List<SymbolsDecl>();
        var builtins = new List<BuiltinDecl>();
        var symbolProperties = new List<SymbolPropertyDecl>();
        foreach (var item in Items(module.Arguments[1]))
        {
            var member = (HirOperation)item;
            var a = member.Arguments;
            switch (Kind(member))
            {
                case "Using":
                    usings.Add(new UsingDecl(Text(a[0]), Span(member)));
                    break;
                case "TokenRule":
                {
                    var except = new List<LiteralExpr>();
                    if (Value(a[2]) is { } list)
                        foreach (var word in Items(Expect(list, "Except").Arguments[0]))
                        {
                            string value = Unquote(Text(word));
                            if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(word));
                            except.Add(new LiteralExpr(value, Span(word)));
                        }
                    rules.Add(new TokenRule(Text(a[0]), Expression(a[1]), Span(member), except.ToArray()));
                    break;
                }
                case "SyntaxRule":
                {
                    var end = (HirOperation)a[3];
                    SemanticsBlock? semantics = Kind(end) == "RuleSemantics" ? Semantics(end.Arguments[0]) : null;
                    rules.Add(new SyntaxRule(Text(a[0]), Expression(a[1]), Span(member), Clauses(a[2]), semantics));
                    break;
                }
                case "ExtensibleRule":
                    rules.Add(new ExtensibleRule(Text(a[0]), Alternatives(a[2]), Span(member), Items(a[1]).Select(Property).ToArray()));
                    break;
                case "Symbols":
                    symbols.Add(new SymbolsDecl(Items(a[0]).Select(Name).ToArray(), Span(member)));
                    break;
                case "Builtin":
                    builtins.Add(new BuiltinDecl(Name(a[0]), Items(a[2]).Select(Name).ToArray(), Span(member),
                        Value(a[1]) is { } within ? AtEnd(within) : null));
                    break;
                case "SymbolProperty":
                    symbolProperties.Add(new SymbolPropertyDecl(Name(a[0]), Kinds(a[1]), Code(a[2]), Code(a[3]), Span(member)));
                    break;
                case "Extend":
                    extends.Add(new ExtendDecl(Text(a[0]), Alternatives(a[1]), Span(member)));
                    break;
                default:
                    throw Unexpected(member);
            }
        }
        return new ModuleDecl(Text(module.Arguments[0]), usings.ToArray(), rules.ToArray(), extends.ToArray(), Span(module),
            symbols.ToArray(), builtins.ToArray(), symbolProperties.ToArray());
    }

    static EquatableArray<Alternative> Alternatives(HirNode list)
    {
        var alternatives = new List<Alternative>();
        foreach (var item in Items(list))
        {
            var node = Expect(item, "Alternative");
            var a = node.Arguments;
            var body = Expression(a[1]);
            string? name = Value(a[0]) is { } named ? Text(named) : null;
            int end = body.Span.End;
            int? precedence = null;
            GrammarAssociativity? associativity = null;
            if (Value(a[2]) is { } spec)
            {
                var group = Expect(spec, "Precedence");
                end = Span(group).End;
                var number = group.Arguments[0];
                string digits = Text(number);
                if (digits.Length > 9) throw new NgrMappingException("precedence is too large", Span(number));
                precedence = int.Parse(digits, CultureInfo.InvariantCulture);
                if (Value(group.Arguments[1]) is { } word)
                    associativity = Text(word) == "left" ? GrammarAssociativity.Left : GrammarAssociativity.Right;
            }
            var clauses = Clauses(a[3]);
            if (clauses.Count > 0) end = clauses[clauses.Count - 1].Span.End;
            SemanticsBlock? semantics = null;
            if (Value(a[4]) is { } block)
            {
                semantics = Semantics(block);
                end = semantics.Span.End;
            }
            bool isImplicit = false;
            if (name is null)
            {
                if (body is ReferenceExpr reference)
                {
                    int dot = reference.Name.LastIndexOf('.');
                    name = dot < 0 ? reference.Name : reference.Name.Substring(dot + 1);
                    isImplicit = true;
                }
                else
                {
                    name = "";
                }
            }
            alternatives.Add(new Alternative(name, isImplicit, body, precedence, associativity,
                GrammarSpan.FromBounds(Span(node).Start, end), clauses, semantics));
        }
        return alternatives.ToArray();
    }

    /// <summary>Clause spans end at their last token, as the bootstrap parser's do (an absent optional adds nothing).</summary>
    static EquatableArray<BindingClause> Clauses(HirNode list)
    {
        var clauses = new List<BindingClause>();
        foreach (var item in Items(list))
        {
            var node = (HirOperation)item;
            var a = node.Arguments;
            switch (Kind(node))
            {
                case "Declares":
                {
                    var field = a[1];
                    var sequential = Value(a[2]);
                    var fileScope = Value(a[3]);
                    var export = Value(a[4]);
                    int end = export is not null ? Span(export).End : Span(field).End;
                    if (export is null && sequential is not null) end = Span(sequential).End;
                    if (export is null && fileScope is not null) end = Span(fileScope).End;
                    NameDecl? type = null;
                    if (Value(a[5]) is { } declared)
                    {
                        type = AtEnd(declared);
                        end = type.Span.End;
                    }
                    clauses.Add(new BindingClause(BindingClauseKind.Declares, new[] { Name(a[0]) }, Text(field), Span(field), false,
                        export is not null, GrammarSpan.FromBounds(Span(node).Start, end), Target: type,
                        FileScope: fileScope is not null, Sequential: sequential is not null));
                    break;
                }
                case "References":
                {
                    var field = a[2];
                    NameDecl? qualifier = Value(a[3]) is { } within ? AtEnd(within) : null;
                    int end = qualifier?.Span.End ?? Span(field).End;
                    clauses.Add(new BindingClause(BindingClauseKind.References, Kinds(a[1]), Text(field), Span(field),
                        Value(a[0]) is not null, false, GrammarSpan.FromBounds(Span(node).Start, end), qualifier));
                    break;
                }
                case "Scope":
                    clauses.Add(new BindingClause(BindingClauseKind.Scope, default, "", default, false, false, Span(node)));
                    break;
                case "Dynamic":
                    clauses.Add(new BindingClause(BindingClauseKind.Dynamic, default, "", default, false, false, Span(node)));
                    break;
                case "Lowers":
                    clauses.Add(Lowers(node));
                    break;
                default:
                    throw Unexpected(node);
            }
        }
        return clauses.ToArray();
    }

    static BindingClause Lowers(HirOperation clause)
    {
        int start = Span(clause).Start;
        var form = (HirOperation)clause.Arguments[0];
        var a = form.Arguments;
        switch (Kind(form))
        {
            case "LowersRepeat":
            {
                var type = Name(a[0]);
                return new BindingClause(BindingClauseKind.LowersRepeat, default, "", default, false, false,
                    GrammarSpan.FromBounds(start, Span(a[3]).End), Target: type, Arguments: new[]
                    {
                        new LoweringArgument(Text(a[1]), Span(a[1])),
                        new LoweringArgument(Text(a[2]), Span(a[2])),
                        new LoweringArgument(Text(a[3]), Span(a[3]), SequenceElementType: type),
                    });
            }
            case "LowersLiteral":
            case "LowersText":
            case "LowersSequence":
            {
                var kind = Kind(form) switch
                {
                    "LowersText" => BindingClauseKind.LowersText,
                    "LowersSequence" => BindingClauseKind.LowersSequence,
                    _ => BindingClauseKind.LowersLiteral,
                };
                return new BindingClause(kind, default, Text(a[1]), Span(a[1]), false, false,
                    GrammarSpan.FromBounds(start, Span(a[1]).End), Target: Name(a[0]));
            }
            case "LowersValue":
            {
                var spec = (HirOperation)a[0];
                bool computed = Kind(spec) == "ComputedType";
                var type = Name(spec.Arguments[0]);
                return new BindingClause(BindingClauseKind.LowersValue, default, Text(a[1]), Span(a[1]), false, false,
                    GrammarSpan.FromBounds(start, Span(a[1]).End), Target: computed ? null : type, TypeProperty: computed ? type : null);
            }
            case "LowersTemplate":
            case "LowersExpand":
                return new BindingClause(Kind(form) == "LowersTemplate" ? BindingClauseKind.LowersTemplate : BindingClauseKind.LowersExpand,
                    default, Text(a[0]), Span(a[0]), false, false, GrammarSpan.FromBounds(start, Span(form).End),
                    Arguments: new[] { new LoweringArgument(Text(a[1]), Span(a[1])) });
            case "LowersReference":
            {
                var property = Name(a[0]);
                NameDecl? initializer = Value(a[1]) is { } spec ? AtEnd(spec) : null;
                return new BindingClause(BindingClauseKind.LowersReference, default, "", default, false, false,
                    GrammarSpan.FromBounds(start, initializer?.Span.End ?? property.Span.End),
                    TypeProperty: property, InitializerProperty: initializer);
            }
            case "LowersCall":
            {
                var operation = (HirOperation)a[0];
                bool computed = Kind(operation) == "ComputedOperation";
                bool optional = computed && Value(operation.Arguments[0]) is not null;
                var name = Name(operation.Arguments[computed ? 1 : 0]);
                return new BindingClause(BindingClauseKind.Lowers, default, "", default, optional, false,
                    GrammarSpan.FromBounds(start, Span(form).End), Target: computed ? null : name,
                    Arguments: Items(a[1]).Select(Argument).ToArray(), OperationProperty: computed ? name : null);
            }
            default:
                throw Unexpected(form);
        }
    }

    static LoweringArgument Argument(HirNode node)
    {
        var argument = (HirOperation)node;
        var a = argument.Arguments;
        return Kind(argument) switch
        {
            "SequenceArgument" => Text(a[0]) == "inferred"
                ? new LoweringArgument(Text(a[1]), Span(a[1]), InferSequence: true)
                : new LoweringArgument(Text(a[1]), Span(a[1]), Name(a[0])),
            "OptionalArgument" => new LoweringArgument(Text(a[1]), Span(a[1]), OptionalElementType: Name(a[0])),
            "OptionalTextArgument" => new LoweringArgument(Text(a[0]), Span(a[0]), OptionalText: true),
            "TextArgument" => new LoweringArgument(Text(a[0]), Span(a[0]), AsText: true),
            "FieldArgument" => new LoweringArgument(Text(a[0]), Span(a[0])),
            _ => throw Unexpected(argument),
        };
    }

    static EquatableArray<NameDecl> Kinds(HirNode node)
    {
        var kinds = (HirOperation)node;
        return Kind(kinds) == "KindGroup" ? Items(kinds.Arguments[0]).Select(Name).ToArray() : new[] { Name(kinds.Arguments[0]) };
    }

    /// <summary>C# text as the bootstrap parser captures it: trimmed at the end (issue 239).</summary>
    static CodeText Code(HirNode node)
    {
        string text = Text(node).TrimEnd();
        return new CodeText(text, new GrammarSpan(node.Origins[0].Span.Start, text.Length));
    }

    static PropertyDecl Property(HirNode node)
    {
        var property = Expect(node, "PropertyDecl");
        var a = property.Arguments;
        bool hover = false, expected = false;
        foreach (var flag in Items(a[1]))
        {
            if (Text(flag) == "hover") hover = true;
            else expected = true;
        }
        var direction = Text(a[0]) == "out" ? PropertyDirection.Out : PropertyDirection.In;
        return new PropertyDecl(direction, Name(a[2]), Code(a[3]), Code(a[4]), hover, expected, Span(property));
    }

    static SemanticsBlock Semantics(HirNode node)
    {
        var block = Expect(node, "Semantics");
        var properties = new List<PropertyDecl>();
        var statements = new List<SemanticStatement>();
        foreach (var item in Items(block.Arguments[0]))
        {
            var statement = (HirOperation)item;
            var a = statement.Arguments;
            switch (Kind(statement))
            {
                case "PropertyDecl":
                    properties.Add(Property(statement));
                    break;
                case "Check":
                    statements.Add(new CheckStatement(Value(a[0]) is { } code ? Name(code) : null, Code(a[1]), Code(a[2]),
                        Span(statement), Value(a[3]) is { } at ? AtEnd(at) : null));
                    break;
                case "Assignment":
                    statements.Add(Assign(statement));
                    break;
                default:
                    throw Unexpected(statement);
            }
        }
        return new SemanticsBlock(properties.ToArray(), statements.ToArray(), Span(block));
    }

    static AssignStatement Assign(HirOperation node)
    {
        var target = node.Arguments[0];
        string text = Text(target);
        var targetSpan = target.Origins[0].Span;
        var span = Span(node);
        var value = Code(node.Arguments[1]);
        string[] parts = text.Split('.');
        if (parts.Length == 1) return new AssignStatement(AssignTarget.Self, null, new NameDecl(text, Span(target)), value, span);
        if (parts.Length > 2)
            throw new NgrMappingException("an assignment target is Property, Child.Property or symbol.Property", Span(target));
        var property = new NameDecl(parts[1], GrammarSpan.FromBounds(targetSpan.Start + parts[0].Length + 1, targetSpan.End));
        if (parts[0] == "symbol") return new AssignStatement(AssignTarget.Symbol, null, property, value, span);
        return new AssignStatement(AssignTarget.Child, new NameDecl(parts[0], new GrammarSpan(targetSpan.Start, parts[0].Length)), property, value, span);
    }

    /// <summary>A choice of one sequence is that sequence.</summary>
    static Expr Expression(HirNode node)
    {
        var alternatives = Items(Expect(node, "Choice").Arguments[0]).Select(Sequence).ToList();
        return alternatives.Count == 1
            ? alternatives[0]
            : new ChoiceExpr(alternatives.ToArray(), Bounds(alternatives[0], alternatives[^1]));
    }

    /// <summary>A sequence of one element is that element.</summary>
    static Expr Sequence(HirNode node)
    {
        var items = Items(Expect(node, "Sequence").Arguments[0]).Select(Element).ToList();
        return items.Count == 1 ? items[0] : new SequenceExpr(items.ToArray(), Bounds(items[0], items[^1]));
    }

    /// <summary>A label spans from its name (the LabelPrefix node's start) to the end of what it labels.</summary>
    static Expr Element(HirNode node)
    {
        var element = Expect(node, "Element");
        var inner = Unary(element.Arguments[1]);
        if (Value(element.Arguments[0]) is not { } label) return inner;
        return new LabeledExpr(Text(label), inner, GrammarSpan.FromBounds(label.Origins[0].Span.Start, inner.Span.End));
    }

    static Expr Unary(HirNode node)
    {
        var unary = Expect(node, "Unary");
        var inner = Postfix(unary.Arguments[1]);
        var prefixes = Items(unary.Arguments[0]);
        for (int i = prefixes.Count - 1; i >= 0; i--)
        {
            var op = prefixes[i];
            var kind = Text(op) == "!" ? PredicateKind.Not : PredicateKind.And;
            inner = new PredicateExpr(kind, inner, GrammarSpan.FromBounds(op.Origins[0].Span.Start, inner.Span.End));
        }
        return inner;
    }

    static Expr Postfix(HirNode node)
    {
        var postfix = Expect(node, "Postfix");
        var expr = Primary(postfix.Arguments[0]);
        foreach (var op in Items(postfix.Arguments[1]))
        {
            var kind = Text(op) switch
            {
                "?" => RepeatKind.Optional,
                "*" => RepeatKind.ZeroOrMore,
                _ => RepeatKind.OneOrMore,
            };
            expr = new RepeatExpr(kind, expr, GrammarSpan.FromBounds(expr.Span.Start, op.Origins[0].Span.End));
        }
        return expr;
    }

    static Expr Primary(HirNode node)
    {
        var primary = (HirOperation)node;
        switch (Kind(primary))
        {
            case "Literal":
            {
                var quoted = primary.Arguments[0];
                string value = Unquote(Text(quoted));
                if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(quoted));
                return new LiteralExpr(value, Span(quoted));
            }
            case "Any":
                return new AnyCharExpr(Span(primary));
            case "CharClass":
                return CharClass(primary);
            case "Parenthesized":
            {
                var inner = Expression(primary.Arguments[0]);
                var tail = (HirOperation)primary.Arguments[1];
                if (Kind(tail) != "SeparatorTail") return inner; // a plain group keeps the inner span
                return new SeparatedListExpr(inner, Expression(tail.Arguments[0]), Text(tail.Arguments[1]) == "+", Span(primary));
            }
            case "Reference":
                return new ReferenceExpr(Text(primary.Arguments[0]), Span(primary.Arguments[0]));
            default:
                throw Unexpected(primary);
        }
    }

    static Expr CharClass(HirOperation node)
    {
        var ranges = new List<CharRange>();
        foreach (var entry in Items(node.Arguments[1]))
        {
            var item = Expect(entry, "ClassItem");
            var firstText = item.Arguments[0];
            char first = Unquote(Text(firstText))[0];
            char last = first;
            if (Value(item.Arguments[1]) is { } end)
            {
                var lastToken = AtEnd(end);
                last = Unquote(lastToken.Name)[0];
                if (last < first)
                    throw new NgrMappingException("empty character range",
                        GrammarSpan.FromBounds(firstText.Origins[0].Span.Start, lastToken.Span.End));
            }
            ranges.Add(new CharRange(first, last));
        }
        return new CharClassExpr(ranges.ToArray(), Value(node.Arguments[0]) is not null, Span(node));
    }

    /// <summary>Decodes a quoted literal the String or Char token has already validated.</summary>
    static string Unquote(string quoted)
    {
        var builder = new StringBuilder();
        for (int i = 1; i < quoted.Length - 1; i++)
        {
            char c = quoted[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }
            char escape = quoted[++i];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case '0': builder.Append('\0'); break;
                case 'u':
                    builder.Append((char)int.Parse(quoted.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: builder.Append(escape); break; // \\ \" \'
            }
        }
        return builder.ToString();
    }

    static HirOperation Expect(HirNode node, string name) =>
        node is HirOperation operation && operation.Signature.Id == "Grammar." + name ? operation : throw Unexpected(node);

    static string Kind(HirOperation operation) =>
        operation.Signature.Id.StartsWith("Grammar.", StringComparison.Ordinal)
            ? operation.Signature.Id["Grammar.".Length..] : throw Unexpected(operation);

    static InvalidOperationException Unexpected(HirNode node) =>
        new($"unexpected {(node as HirOperation)?.Signature.Id ?? node.GetType().Name} in grammar HIR");

    static IReadOnlyList<HirNode> Items(HirNode node) => ((HirSequence)node).Items;

    static HirNode? Value(HirNode node) => ((HirOptional)node).Value;

    static string Text(HirNode node) => ((HirText)node).Value;

    static GrammarSpan Span(HirNode node)
    {
        var span = node.Origins[0].Span;
        return new GrammarSpan(span.Start, span.Length);
    }

    /// <summary>A name whose text spans its origin: an inline text argument, or a wrapper that is only its token.</summary>
    static NameDecl Name(HirNode text) => new(Text(text), Span(text));

    /// <summary>The name a wrapper ends with (<c>in Name</c>, <c>type Name</c>, <c>.. Char</c>, <c>initializer Name</c>, <c>at Name</c>).</summary>
    static NameDecl AtEnd(HirNode text)
    {
        var span = text.Origins[0].Span;
        string value = Text(text);
        return new NameDecl(value, GrammarSpan.FromBounds(span.End - value.Length, span.End));
    }

    static GrammarSpan Bounds(Expr first, Expr last) => GrammarSpan.FromBounds(first.Span.Start, last.Span.End);
}
```

`AlternativeName` and `LabelPrefix` start with their name, but the model needs only the alternative's name text and the label's start, which is the wrapper node's start. So no start-anchored helper is needed.

- [ ] **Step 4: Remove `NgrMappingException` from `NgrMapper.cs`**

Delete lines 8-11 (`internal sealed class NgrMappingException … }`) and the blank line after them. The class now lives in `NgrProjector.cs`.

- [ ] **Step 5: Add `ParseProjected` to `NgrParser.cs`**

Add the usings `using Nitrogen.Binding;`, `using Nitrogen.Semantic;`, `using Nitrogen.Semantics;`, and these members:

```csharp
    /// <summary>Stage-2 cutover: <see cref="Parse"/> through lowered HIR and <see cref="NgrProjector"/>.</summary>
    internal static GrammarParseResult ParseProjected(string text)
    {
        using var result = s_language.Parse(text, NitrogenModule.File);
        if (!result.Success)
        {
            var diagnostic = result.Diagnostics[0];
            return Failure(result.FormatMessage(diagnostic), new GrammarSpan(diagnostic.Span.Start, diagnostic.Span.Length));
        }
        try
        {
            return new GrammarParseResult(NgrProjector.File(LowerFile(result.Tree)), default);
        }
        catch (NgrMappingException error)
        {
            return Failure(error.Message, error.Span);
        }
    }

    const string DocumentPath = "grammar.ngr";

    /// <summary>
    /// The parsed file's one <c>Grammar.File</c> root. Binding errors (duplicates) do not block it:
    /// <see cref="GrammarValidator"/> reports those. Any other outcome on a successful parse is a bug.
    /// </summary>
    static HirOperation LowerFile(SyntaxTree tree)
    {
        var project = new Project(s_language);
        project.Set(DocumentPath, tree);
        var file = new ProjectSemantics(project)[DocumentPath];
        var lowered = HirLowering.Lower(file, s_language.SemanticCatalog, Guid.NewGuid(), LoweringAdmission.SyntaxOnly);
        if (lowered.Roots is [HirOperation { Signature.Id: "Grammar.File" } root]) return root;
        var first = lowered.Diagnostics.FirstOrDefault();
        throw new InvalidOperationException("a parsed grammar did not lower to one Grammar.File: " +
            (first is null ? $"{lowered.Roots.Count} roots" : $"{first.Code} {first.Message}"));
    }
```

- [ ] **Step 6: Build and run the self-hosting tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: 0 warnings, 0 errors.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SelfHostingTests"`
Expected: PASS on every row. A dump mismatch names the declaration and field; fix the projector against `NgrMapper`'s corresponding code, never the bootstrap parser. An `InvalidOperationException` from `LowerFile` means a successful parse did not lower: read its `NH` code.

- [ ] **Step 7: Commit**

```bash
git add Nitrogen.Ngr Nitrogen.Tests/Grammar/SelfHostingTests.cs
git commit -m "Project lowered grammar HIR to GrammarModel beside the mapper

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Cut over and delete `NgrMapper`

**Files:**
- Modify: `Nitrogen.Ngr/NgrParser.cs`
- Delete: `Nitrogen.Ngr/NgrMapper.cs`
- Modify: `Nitrogen.Tests/Grammar/SelfHostingTests.cs`

- [ ] **Step 1: Make `Parse` project**

In `NgrParser.cs`, replace the body of `Parse` with the body of `ParseProjected`, delete `ParseProjected`, and change the class summary to:

```csharp
/// <summary>
/// The self-hosted <c>.ngr</c> parser: generated from <c>Nitrogen.ngr</c>, lowered to typed
/// <see cref="GrammarSemantics"/> HIR and projected by <see cref="NgrProjector"/> to the same
/// <see cref="GrammarFile"/> model as the bootstrap <see cref="GrammarParser"/>. Error messages
/// and positions may differ from the bootstrap parser's; successful results are identical.
/// </summary>
```

- [ ] **Step 2: Delete the mapper and the temporary checks**

```bash
git rm Nitrogen.Ngr/NgrMapper.cs
```

In `SelfHostingTests`, remove the `// Stage-2 cutover check` block from `AssertSameModel` and the `projectedFailure` lines from `Invalid_input_fails_in_both_parsers`. `NgrParser.Parse` is now the projector, so the remaining assertions cover it.

- [ ] **Step 3: Confirm nothing references the mapper**

Run: `grep -rn "NgrMapper\|ParseProjected" --include='*.cs' . | grep -v "/obj/\|/bin/\|worktrees"`
Expected: no output.

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: 0 warnings, 0 errors.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A Nitrogen.Ngr Nitrogen.Tests/Grammar/SelfHostingTests.cs
git commit -m "Parse .ngr through lowered HIR and delete NgrMapper

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Documentation and full verification

**Files:**
- Modify: `docs/roadmap.md:26`
- Modify: `README.md` (the `.ngr` bullet added in stage 1)
- Modify: `docs/superpowers/specs/2026-10-06-self-hosted-grammar-lowering-design.md:3`
- Modify: `docs/superpowers/specs/2026-10-06-self-hosted-grammar-projection-design.md:3`

- [ ] **Step 1: Update the docs**

`docs/roadmap.md:26`: replace `` `NgrParser` maps its generated syntax to `Nitrogen.Grammar.GrammarModel`. `` with `` `NgrParser` lowers its generated syntax to typed `Grammar` HIR and projects that (`NgrProjector`) to `Nitrogen.Grammar.GrammarModel`. ``

`README.md`: in the stage-1 sentence beginning `` `Nitrogen.ngr` itself lowers every grammar to typed HIR``, append:

```markdown
  `NgrParser.Parse` builds its `GrammarModel` from that HIR (`NgrProjector`), with no separate syntax walk.
```

Stage-1 spec status line: `Status: implemented (2026-10-06); stage 2 implemented in [the projection design](2026-10-06-self-hosted-grammar-projection-design.md).`

Stage-2 spec status line: `Status: implemented (2026-10-06), stage 2 of 2. Builds on`

- [ ] **Step 2: Full verification**

Run: `dotnet build Nitrogen.slnx -warnaserror --no-incremental`
Expected: 0 warnings, 0 errors.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass. The count is 1012 from stage 1, plus 5 admission tests and 6 new `SelfHostingTests` rows (1023). Record the count in the commit body.

- [ ] **Step 3: Commit**

```bash
git add README.md docs
git commit -m "Document the HIR-projected self-hosted parser

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
