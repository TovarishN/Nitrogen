using Nitrogen.Grammar;
using Nitrogen.Ngr.Syntax;
using Xunit;

namespace Nitrogen.Tests;

public class NgrGrammarTests
{
    static readonly Language Ngr = new LanguageBuilder().Add(NitrogenModule.Instance).Build();

    public static TheoryData<string> GrammarFiles() =>
        new() { "Calc.ngr", "Power.ngr", "Clash.ngr", "Mini.ngr", "Lexical.ngr", "Nitrogen.ngr", "Scopes.ngr", "Typed.ngr", "TypedExtra.ngr", "Lowered.ngr" };

    [Fact]
    public void Nitrogen_grammar_compiles_without_diagnostics()
    {
        var result = GrammarCompiler.Compile(new[]
        {
            new GrammarInput("Nitrogen.ngr", TestGrammarFileTests.ReadGrammar("Nitrogen.ngr"), "Nitrogen.Ngr.Syntax"),
        });
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(GrammarFiles))]
    public void Generated_parser_accepts_every_grammar(string file)
    {
        using var result = Ngr.Parse(TestGrammarFileTests.ReadGrammar(file), NitrogenModule.File);
        Assert.True(result.Success, result.Success ? "" : result.FormatMessage(result.Diagnostics[0]) + " at " + result.Diagnostics[0].Span);
    }

    [Theory]
    [InlineData("syntax module M { syntax R = ; }")]
    [InlineData("syntax module M { rule R = A; }")]
    [InlineData("syntax module M { syntax R = (A; \",\"); }")]
    public void Generated_parser_rejects_invalid_grammar(string text)
    {
        using var result = Ngr.Parse(text, NitrogenModule.File);
        Assert.False(result.Success);
    }
}
