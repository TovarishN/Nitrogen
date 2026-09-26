using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class LifetimeTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();

    [Fact]
    public void Tree_access_after_dispose_throws()
    {
        var result = Calc.Parse("f(1);", CalcModule.Program);
        var tree = result.Tree;
        var root = new SyntaxNode(tree, 0);
        _ = tree.Parent(1); // build the lazy parent table so it is returned too
        result.Dispose();

        Assert.True(tree.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => tree.Kind(0));
        Assert.Throws<ObjectDisposedException>(() => root.Kind);
        Assert.Throws<ObjectDisposedException>(() => tree.Trivia.Length);
        Assert.Equal(0, result.Diagnostics.Length);
        result.Dispose(); // idempotent
    }

    [Fact]
    public void A_parse_inside_a_parse_uses_its_own_arena()
    {
        using var outer = Calc.Parse("a+b;", CalcModule.Program);
        using var inner = Calc.Parse("c*d;", CalcModule.Program);
        Assert.Contains("Add", SyntaxDumper.Dump(outer.Tree));
        Assert.Contains("Mul", SyntaxDumper.Dump(inner.Tree));
    }
}
