using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class TestGrammarFileTests
{
    internal static string ReadGrammar(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grammars", name));

    static GrammarFile Parse(string name)
    {
        var result = GrammarParser.Parse(ReadGrammar(name));
        Assert.True(result.Success, result.Diagnostics.Count > 0 ? $"{name}: {result.Diagnostics[0]}" : "");
        return result.File!;
    }

    [Fact]
    public void All_test_grammars_parse_and_validate_together()
    {
        var modules = new[] { "Calc.ngr", "Power.ngr", "Clash.ngr", "Mini.ngr" }
            .SelectMany(name => Parse(name).Modules)
            .ToList();
        Assert.Equal(new[] { "Calc", "Calc.Power", "Calc.Clash", "Mini" }, modules.Select(m => m.Name));
        Assert.Empty(GrammarValidator.Validate(modules));
    }

    [Fact]
    public void Calc_model_matches_the_hand_written_module()
    {
        var calc = Parse("Calc.ngr").Modules[0];
        Assert.Equal(
            new[] { "Number", "Identifier", "Program", "Statement", "Assign", "ExprStatement", "Expr" },
            calc.Rules.Select(r => r.Name));

        var expr = (ExtensibleRule)calc.Rules[6];
        Assert.Equal(new[] { "Num", "Ref", "Call", "Paren", "Neg", "Add", "Sub", "Mul" }, expr.Alternatives.Select(a => a.Name));
        Assert.Equal(
            "(alt Call (seq (label Callee (ref Identifier)) (lit \"(\") (label Args (sep* (ref Expr) (lit \",\"))) (lit \")\")))",
            GrammarDumper.Dump(expr.Alternatives[2]));
        Assert.Equal("(alt Neg (seq (lit \"-\") (ref Expr)) 30)", GrammarDumper.Dump(expr.Alternatives[4]));
        Assert.Equal(
            "(token Number (seq (plus (class '0'..'9')) (opt (seq (lit \".\") (plus (class '0'..'9'))))))",
            GrammarDumper.Dump(calc.Rules[0]));
    }

    [Fact]
    public void Validation_catches_a_missing_extended_module()
    {
        var power = Parse("Power.ngr").Modules;
        var diagnostic = Assert.Single(GrammarValidator.Validate(power));
        Assert.Equal(GrammarCodes.UnknownModule, diagnostic.Code);
        Assert.Equal("Calc.Power", diagnostic.Module);
    }

    [Fact]
    public void Reparsing_gives_an_equal_model()
    {
        Assert.Equal(Parse("Calc.ngr"), Parse("Calc.ngr"));
        Assert.Equal(Parse("Calc.ngr").GetHashCode(), Parse("Calc.ngr").GetHashCode());
    }
}
