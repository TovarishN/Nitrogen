using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Mini;
using Xunit;

namespace Nitrogen.Tests;

public class ViewTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
    static readonly Language Mini = new LanguageBuilder().Add(MiniModule.Instance).Build();

    static int CallNode(SyntaxTree tree) => tree.Child(tree.Child(tree.Child(0, 0), 0), 0);

    [Fact]
    public void Typed_views_navigate_labeled_children()
    {
        using var result = Calc.Parse("f(1, x+2);", CalcModule.Program);
        var tree = result.Tree;
        var call = SyntaxView.Cast<CallExpr>(tree, CallNode(tree));

        Assert.Equal("f", call.Callee.ToString());
        Assert.Equal(2, call.Args.Count);
        Assert.Equal(1, call.Args.SeparatorCount);
        Assert.Equal(",", call.Args.Separator(0).ToString());
        Assert.True(SyntaxView.TryCast<NumExpr>(tree, call.Args[0].Index, out var one));
        Assert.Equal("1", one.Number.ToString());

        var sum = call.Args[1].As<AddExpr>();
        Assert.Equal("+", new Token(tree, tree.Child(sum.Index, 1)).ToString());
        Assert.Equal("x", sum.Expr1.As<SyntaxNode>().ToString());
        Assert.Equal("2", sum.Expr2.As<NumExpr>().Number.ToString());
    }

    [Fact]
    public void Lists_enumerate_without_boxing()
    {
        using var result = Calc.Parse("f(1, x+2);", CalcModule.Program);
        var call = SyntaxView.Cast<CallExpr>(result.Tree, CallNode(result.Tree));
        var kinds = new List<string>();
        foreach (var arg in call.Args) kinds.Add(Calc.GetKindName(arg.Kind));
        Assert.Equal(new[] { "Num", "Add" }, kinds);

        var program = SyntaxView.Cast<ProgramNode>(result.Tree, 0);
        Assert.Equal(1, program.Statements.Count);
        int visited = 0;
        foreach (var child in new SyntaxNode(result.Tree, 0).Children) visited++;
        Assert.Equal(1, visited);
    }

    [Fact]
    public void Parent_navigation()
    {
        using var result = Calc.Parse("f(1, x+2);", CalcModule.Program);
        var tree = result.Tree;
        int callNode = CallNode(tree);
        var first = new SyntaxNode(tree, SyntaxView.Cast<CallExpr>(tree, callNode).Args[0].Index);
        Assert.Equal(SyntaxKinds.List, first.Parent.Kind);
        Assert.Equal(callNode, first.Parent.Parent.Index);
        Assert.True(new SyntaxNode(tree, 0).Parent.IsNull);
    }

    [Fact]
    public void Cast_to_the_wrong_view_throws()
    {
        using var result = Calc.Parse("f(1);", CalcModule.Program);
        int callNode = CallNode(result.Tree);
        Assert.False(SyntaxView.TryCast<AddExpr>(result.Tree, callNode, out _));
        var error = Assert.Throws<InvalidCastException>(() => SyntaxView.Cast<AddExpr>(result.Tree, callNode));
        Assert.Contains("Call", error.Message);
    }

    [Fact]
    public void Optional_view()
    {
        using var present = Mini.Parse("( x )", MiniModule.Maybe);
        // Unlabeled `Word?` gets no property (only direct references do), so build the view by hand.
        var word = new Optional<Token>(present.Tree, present.Tree.Child(0, 1));
        Assert.True(word.HasValue);
        Assert.Equal("x", word.Value.ToString());

        using var absent = Mini.Parse("()", MiniModule.Maybe);
        var none = new Optional<Token>(absent.Tree, absent.Tree.Child(0, 1));
        Assert.False(none.HasValue);
        Assert.Throws<InvalidOperationException>(() => none.Value);
    }

    [Fact]
    public void Token_view_rejects_interior_nodes()
    {
        using var result = Calc.Parse("f(1);", CalcModule.Program);
        Assert.False(Token.Is(result.Tree, 0));
        Assert.True(Token.Is(result.Tree, result.Tree.Child(CallNode(result.Tree), 0)));
    }
}
