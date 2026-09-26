using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class CompositionTests
{
    static Language Build(params SyntaxModule[] modules)
    {
        var builder = new LanguageBuilder();
        foreach (var module in modules) builder.Add(module);
        return builder.Build();
    }

    [Theory]
    [InlineData("2^3^2", "(Pow (Num Number:\"2\") \"^\" (Pow (Num Number:\"3\") \"^\" (Num Number:\"2\")))")]
    [InlineData("2*3^2", "(Mul (Num Number:\"2\") \"*\" (Pow (Num Number:\"3\") \"^\" (Num Number:\"2\")))")]
    [InlineData("-2^2", "(Pow (Neg \"-\" (Num Number:\"2\")) \"^\" (Num Number:\"2\"))")]
    public void Another_module_extends_Expr(string expr, string expected)
    {
        Assert.Equal(expected, CalcParsingTests.DumpExpr(Build(CalcModule.Instance, PowerModule.Instance), expr));
    }

    [Theory]
    [InlineData("1+2^3*4-f(x^2)")]
    [InlineData("-(a^b)^c")]
    public void Module_order_does_not_change_the_tree(string expr)
    {
        Assert.Equal(
            CalcParsingTests.DumpExpr(Build(CalcModule.Instance, PowerModule.Instance), expr),
            CalcParsingTests.DumpExpr(Build(PowerModule.Instance, CalcModule.Instance), expr));
    }

    [Fact]
    public void Tied_alternatives_produce_an_ambiguous_node_and_diagnostic()
    {
        foreach (var language in new[] { Build(CalcModule.Instance, ClashModule.Instance), Build(ClashModule.Instance, CalcModule.Instance) })
        {
            using var result = language.Parse("f(1);", CalcModule.Program);
            Assert.True(result.Success);
            Assert.True(result.HasErrors);
            Assert.Equal(
                "(Program (List (ExprStatement (? (Call Identifier:\"f\" \"(\" (List (Num Number:\"1\")) \")\") (Invoke Identifier:\"f\" \"(\" (List (Num Number:\"1\")) \")\")) \";\")))",
                SyntaxDumper.Dump(result.Tree));
            var diagnostic = Assert.Single(result.Diagnostics.ToArray());
            Assert.Equal(DiagnosticCode.Ambiguous, diagnostic.Code);
            Assert.Equal(new TextSpan(0, 4), diagnostic.Span);
            Assert.Equal("ambiguous parse: Calc.Call or Calc.Clash.Invoke", result.FormatMessage(diagnostic));
        }
    }

    [Fact]
    public void Extending_a_point_whose_owner_is_missing_fails()
    {
        var error = Assert.Throws<LanguageCompositionException>(() => Build(PowerModule.Instance));
        Assert.Equal("Module 'Calc.Power' extends 'Calc.Expr', but module 'Calc' is not part of this language.", error.Message);
    }

    [Fact]
    public void Declaring_another_modules_point_fails()
    {
        var error = Assert.Throws<LanguageCompositionException>(() => Build(CalcModule.Instance, RogueDeclareModule.Instance));
        Assert.Equal("Module 'Rogue' cannot declare 'Calc.Expr'; only 'Calc' can.", error.Message);
    }

    [Fact]
    public void Duplicate_alternative_names_fail()
    {
        var error = Assert.Throws<LanguageCompositionException>(
            () => Build(CalcModule.Instance, PowerModule.Instance, PowerTwinModule.Instance));
        Assert.Equal("Extension point 'Calc.Expr' has two alternatives named 'Pow' from 'Calc.Power' and 'Calc.PowerTwin'.", error.Message);
    }

    [Fact]
    public void Postfix_precedence_must_be_positive()
    {
        var error = Assert.Throws<LanguageCompositionException>(() => Build(CalcModule.Instance, ZeroPrecedenceModule.Instance));
        Assert.Contains("must be 1..255", error.Message);
    }

    [Fact]
    public void Module_ids_are_unique_and_stable()
    {
        Assert.NotEqual(CalcModule.Instance.Id, PowerModule.Instance.Id);
        Assert.Equal(CalcModule.Instance.Id << 16, CalcModule.Instance.KindBase);
        Assert.Equal("Pow", Build(CalcModule.Instance, PowerModule.Instance).GetKindName(PowerKinds.Pow));
    }
}
