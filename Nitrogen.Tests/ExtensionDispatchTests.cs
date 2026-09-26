using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class ExtensionDispatchTests
{
    static readonly ExtensionPoint Expr =
        new LanguageBuilder().Add(CalcModule.Instance).Build().GetExtensionPoint(CalcModule.Instance.Expr);

    static int[] Prefix(string text) => Expr.PrefixCandidates(ExtensionPoint.Bucket(text, 0)).ToArray();

    static int[] Postfix(string text) => Expr.PostfixCandidates(ExtensionPoint.Bucket(text, 0)).ToArray();

    [Fact]
    public void Prefix_candidates_follow_first_characters_in_registration_order()
    {
        // Calc registers Num, Ref, Call, Paren, Neg.
        Assert.Equal(new[] { 0 }, Prefix("7"));
        Assert.Equal(new[] { 1, 2 }, Prefix("f"));
        Assert.Equal(new[] { 3 }, Prefix("("));
        Assert.Equal(new[] { 4 }, Prefix("-"));
        Assert.Empty(Prefix("+"));
        Assert.Empty(Prefix("é"));
        Assert.Empty(Prefix(""));
    }

    [Fact]
    public void Postfix_candidates_follow_first_characters()
    {
        // Add, Sub, Mul.
        Assert.Equal(new[] { 0 }, Postfix("+"));
        Assert.Equal(new[] { 1 }, Postfix("-"));
        Assert.Equal(new[] { 2 }, Postfix("*"));
        Assert.Empty(Postfix("x"));
    }
}
