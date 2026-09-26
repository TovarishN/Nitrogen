using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class CalcParsingTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();

    /// <summary>Parses <c>expr;</c> and dumps the expression under Program/List/ExprStatement.</summary>
    internal static string DumpExpr(Language language, string expr)
    {
        using var result = language.Parse(expr + ";", CalcModule.Program);
        Assert.True(result.Success, result.Success ? "" : result.FormatMessage(result.Diagnostics[0]));
        var tree = result.Tree;
        int statement = tree.Child(tree.Child(0, 0), 0);
        return SyntaxDumper.Dump(tree, tree.Child(statement, 0));
    }

    [Theory]
    [InlineData("1+2*3", "(Add (Num Number:\"1\") \"+\" (Mul (Num Number:\"2\") \"*\" (Num Number:\"3\")))")]
    [InlineData("1-2-3", "(Sub (Sub (Num Number:\"1\") \"-\" (Num Number:\"2\")) \"-\" (Num Number:\"3\"))")]
    [InlineData("-1*2", "(Mul (Neg \"-\" (Num Number:\"1\")) \"*\" (Num Number:\"2\"))")]
    [InlineData("(1+2)*3", "(Mul (Paren \"(\" (Add (Num Number:\"1\") \"+\" (Num Number:\"2\")) \")\") \"*\" (Num Number:\"3\"))")]
    [InlineData("f(1, x)", "(Call Identifier:\"f\" \"(\" (List (Num Number:\"1\") \",\" (Ref Identifier:\"x\")) \")\")")]
    [InlineData("f( 1 , x )", "(Call Identifier:\"f\" \"(\" (List (Num Number:\"1\") \",\" (Ref Identifier:\"x\")) \")\")")]
    [InlineData("f()", "(Call Identifier:\"f\" \"(\" (List) \")\")")]
    [InlineData("2.5", "(Num Number:\"2.5\")")]
    public void Precedence_associativity_and_longest_match(string expr, string expected)
    {
        Assert.Equal(expected, DumpExpr(Calc, expr));
    }

    [Fact]
    public void Longest_match_prefers_Call_over_the_earlier_registered_Ref()
    {
        Assert.StartsWith("(Call ", DumpExpr(Calc, "f(1)"));
        Assert.StartsWith("(Ref ", DumpExpr(Calc, "f"));
    }

    [Fact]
    public void Assignment_and_program()
    {
        using var result = Calc.Parse("x = 1; y;", CalcModule.Program);
        Assert.Equal(
            "(Program (List (Assign (Ref Identifier:\"x\") \"=\" (Num Number:\"1\") \";\") (ExprStatement (Ref Identifier:\"y\") \";\")))",
            SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Memoized_subtree_survives_the_rollback_of_Assign()
    {
        // Assign parses Expr at 0, fails at ';' and rolls back; ExprStatement reuses the memo.
        // Hits: Expr@0 in ExprStatement, and the failed Expr@4 at end of input.
        // Misses: Expr@0, Expr(min 10)@2, Expr@4.
        using var result = Calc.Parse("a+b;", CalcModule.Program);
        Assert.Equal(
            "(Program (List (ExprStatement (Add (Ref Identifier:\"a\") \"+\" (Ref Identifier:\"b\")) \";\")))",
            SyntaxDumper.Dump(result.Tree));
        Assert.Equal(2, result.Stats.MemoHits);
        Assert.Equal(3, result.Stats.MemoMisses);
    }

    [Fact]
    public void Missing_operand_names_the_extension_point()
    {
        using var result = Calc.Parse("1+;", CalcModule.Program);
        Assert.False(result.Success);
        Assert.Equal(new TextSpan(2, 0), result.Diagnostics[0].Span);
        Assert.Equal("expected Expr", result.FormatMessage(result.Diagnostics[0]));
    }

    [Fact]
    public void Unknown_operator_reports_what_could_follow()
    {
        using var result = Calc.Parse("2^3;", CalcModule.Program);
        Assert.False(result.Success);
        Assert.Equal(new TextSpan(1, 0), result.Diagnostics[0].Span);
        Assert.Equal("expected '=' or ';'", result.FormatMessage(result.Diagnostics[0]));
    }

    [Fact]
    public void Deep_nesting_parses()
    {
        string expr = new string('(', 200) + "1" + new string(')', 200);
        Assert.StartsWith("(Paren ", DumpExpr(Calc, expr));
    }
}
