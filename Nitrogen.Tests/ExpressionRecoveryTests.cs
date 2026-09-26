using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Recovery inside extension alternatives (issue 235, Plan 2b).</summary>
public class ExpressionRecoveryTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();

    static string[] Messages(ParseResult result) =>
        result.Diagnostics.ToArray().Select(d => result.FormatMessage(d) + " " + d.Span).ToArray();

    [Fact]
    public void First_character_sets_overlap_when_a_character_passes_both()
    {
        Assert.True(AsciiSet.Of("a").Overlaps(AsciiSet.Of("ab")));
        Assert.False(AsciiSet.Of("a").Overlaps(AsciiSet.Of("b")));
        Assert.True(AsciiSet.Any.Overlaps(AsciiSet.Of("b")));
        Assert.True(AsciiSet.Of("a").WithNonAscii().Overlaps(AsciiSet.Of("b").WithNonAscii()));
    }

    [Fact]
    public void Alternatives_commit_only_when_no_sibling_can_start_the_same_way()
    {
        var point = Calc.GetExtensionPoint(CalcModule.Instance.Expr);
        Assert.True(point.IsCommitEnabled(CalcKinds.Paren));
        Assert.True(point.IsCommitEnabled(CalcKinds.Add));
        Assert.False(point.IsCommitEnabled(CalcKinds.Call)); // an identifier also starts Ref
        Assert.False(point.IsCommitEnabled(CalcKinds.Ref));
    }

    [Fact]
    public void A_missing_right_operand_is_inserted()
    {
        using var result = Calc.Parse("1 + ;", CalcModule.Program);
        Assert.Equal("(Program (List (ExprStatement (Add (Num Number:\"1\") \"+\" !Error) \";\")))", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected Expr [4..4)" }, Messages(result));
    }

    [Fact]
    public void A_missing_closing_parenthesis_is_inserted()
    {
        using var result = Calc.Parse("(1 + 2;", CalcModule.Program);
        Assert.Equal(
            "(Program (List (ExprStatement (Paren \"(\" (Add (Num Number:\"1\") \"+\" (Num Number:\"2\")) !Literal) \";\")))",
            SyntaxDumper.Dump(result.Tree));
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void Motion_repairs_a_parenthesis_inside_an_attribute()
    {
        using var result = NitrogenMotionParser.Language.Parse("body b { part root box(1, 1, 1) mass = (1 + 2 }", MotionModule.File);
        string dump = SyntaxDumper.Dump(result.Tree);
        Assert.Contains("(Paren \"(\" (Add", dump);
        Assert.Contains("!Literal)", dump);
        Assert.Empty(result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }
}
