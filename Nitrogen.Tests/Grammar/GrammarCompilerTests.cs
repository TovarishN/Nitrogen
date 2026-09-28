using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class GrammarCompilerTests
{
    /// <summary>The test grammars with the namespaces the test project's generator uses.</summary>
    internal static GrammarInput[] TestInputs() => new[]
    {
        Input("Calc.ngr", "Nitrogen.Tests.Calc"),
        Input("Power.ngr", "Nitrogen.Tests.Calc"),
        Input("Clash.ngr", "Nitrogen.Tests.Calc"),
        Input("Mini.ngr", "Nitrogen.Tests.Mini"),
        Input("Lexical.ngr", "Nitrogen.Tests.Lexical"),
        Input("Scopes.ngr", "Nitrogen.Tests.Scopes"),
        Input("Lowered.ngr", "Nitrogen.Tests.Lowered"),
    };

    static GrammarInput Input(string file, string ns) => new(file, TestGrammarFileTests.ReadGrammar(file), ns);

    static CompiledDiagnostic SingleDiagnostic(params GrammarInput[] inputs)
    {
        var result = GrammarCompiler.Compile(inputs);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Sources);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void Test_grammars_compile_to_one_source_per_module()
    {
        var result = GrammarCompiler.Compile(TestInputs());
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
        Assert.Equal(
            new[] { "Calc.g.cs", "Calc.Power.g.cs", "Calc.Clash.g.cs", "Mini.g.cs", "Lexical.g.cs", "Scopes.g.cs", "Lowered.g.cs" },
            result.Sources.Select(s => s.HintName));
    }

    [Fact]
    public void Syntax_errors_carry_the_file_path()
    {
        var d = SingleDiagnostic(
            new GrammarInput("a.ngr", "syntax module A { }", "N"),
            new GrammarInput("b.ngr", "syntax module B { syntax R = ; }", "N"));
        Assert.Equal("b.ngr", d.Path);
        Assert.Equal(GrammarCodes.Syntax, d.Diagnostic.Code);
    }

    [Fact]
    public void Validation_errors_carry_the_declaring_file()
    {
        var d = SingleDiagnostic(
            new GrammarInput("a.ngr", "syntax module A { token T = \"t\"; }", "N"),
            new GrammarInput("b.ngr", "syntax module B { syntax R = A.U; }", "N"));
        Assert.Equal("b.ngr", d.Path);
        Assert.Equal(GrammarCodes.UndefinedRule, d.Diagnostic.Code);
    }

    [Fact]
    public void Two_modules_generating_the_same_class_collide()
    {
        var d = SingleDiagnostic(
            new GrammarInput("a.ngr", "syntax module A.X { token T = \"t\"; }", "N"),
            new GrammarInput("b.ngr", "syntax module B.X { token U = \"u\"; }", "N"));
        Assert.Equal(GrammarCodes.GeneratedNameCollision, d.Diagnostic.Code);
        Assert.Equal("modules 'A.X' and 'B.X' both generate 'XModule' in namespace 'N'", d.Diagnostic.Message);
        Assert.Equal("b.ngr", d.Path);

        Assert.Empty(GrammarCompiler.Compile(new[]
        {
            new GrammarInput("a.ngr", "syntax module A.X { token T = \"t\"; }", "N1"),
            new GrammarInput("b.ngr", "syntax module B.X { token U = \"u\"; }", "N2"),
        }).Diagnostics);
    }

    [Fact]
    public void Kind_names_must_be_unique_in_a_module()
    {
        var d = SingleDiagnostic(new GrammarInput("m.ngr",
            "syntax module M { token Number = ['0'..'9']+; extensible syntax E { | Number } }", "N"));
        Assert.Equal(GrammarCodes.KindNameCollision, d.Diagnostic.Code);
        Assert.Equal("'Number' names two syntax kinds in module 'M'; rename one of them", d.Diagnostic.Message);
    }

    [Fact]
    public void Generated_member_names_are_reserved()
    {
        var d = SingleDiagnostic(new GrammarInput("m.ngr", "syntax module M { syntax Instance = \"x\"; }", "N"));
        Assert.Equal(GrammarCodes.ReservedName, d.Diagnostic.Code);
        Assert.Equal("rule name 'Instance' is reserved by the generated class MModule", d.Diagnostic.Message);
    }

    [Fact]
    public void Warnings_do_not_stop_emission()
    {
        var result = GrammarCompiler.Compile(new[]
        {
            new GrammarInput("w.ngr", "syntax module W { token Id = ['a'..'z']+; syntax F = \"f\" E; extensible syntax E { out T : int = 0; | Num = Id } }", "W"),
        });
        Assert.False(result.HasErrors);
        Assert.Equal(GrammarCodes.UndefinedProperty, Assert.Single(result.Diagnostics).Diagnostic.Code);
        Assert.NotEmpty(result.Sources);
    }

    [Fact]
    public void Semantics_emit_keys_a_table_and_structs_mapped_to_the_grammar()
    {
        const string text = "syntax module W\n{\n  token Id = ['a'..'z']+;\n  syntax R = \"r\" Name:Id\n  {\n    out Size : int = 0;\n    Size = Name.Text.Length;\n  }\n}\n";
        var result = GrammarCompiler.Compile(new[] { new GrammarInput("w.ngr", text, "W") });
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        string code = Assert.Single(result.Sources).Code;
        Assert.Contains("P_R_Size =", code);
        Assert.Contains("public override global::Nitrogen.Semantics.SemanticsRule? GetSemantics(int localKind)", code);
        Assert.Contains("public readonly struct RNodeSemantics : global::Nitrogen.Semantics.ISemanticView<RNodeSemantics>", code);
        Assert.Contains("#line (7, 12) - (7, 28) 1 \"w.ngr\"\nName.Text.Length\n#line default", code);
        Assert.Contains("public override global::System.Collections.Generic.IReadOnlyList<global::Nitrogen.Semantics.Property> Properties => s_properties;", code);
    }

    [Fact]
    public void A_child_named_like_a_semantics_member_is_an_error()
    {
        var result = GrammarCompiler.Compile(new[]
        {
            new GrammarInput("w.ngr", "syntax module W { token Id = ['a'..'z']+; syntax R = \"r\" Parent:Id { out Size : int = 0; Size = 1; } }", "W"),
        });
        Assert.Equal(GrammarCodes.SemanticsNameCollision, Assert.Single(result.Diagnostics).Diagnostic.Code);
    }
}
