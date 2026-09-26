using Xunit;

namespace Nitrogen.Tests;

public unsafe class TreeFreezerTests
{
    const int K1 = (99 << 16) | 1;
    const int K2 = (99 << 16) | 2;

    static ParserState State(string text, BuildArena arena) =>
        new(text, arena, new MemoTable(), &StandardTrivia.WhitespaceAndComments);

    [Fact]
    public void Freeze_renumbers_reachable_nodes_in_preorder()
    {
        var a = new BuildArena();
        var s = State("a  b", a);
        a.NewNode(K2, 0, 0, NodeFlags.None, 0, 0); // dead node: never pushed, must not survive
        s.Open(K1);
        s.MatchLiteral("a");
        s.SkipTrivia();
        s.Open(K2);
        s.MatchLiteral("b");
        s.Close();
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, "a  b", language: null);

        Assert.Equal(4, tree.NodeCount);
        Assert.Equal(new[] { K1, SyntaxKinds.Literal, K2, SyntaxKinds.Literal },
            Enumerable.Range(0, 4).Select(tree.Kind));
        Assert.Equal(new TextSpan(0, 4), tree.Span(0));
        Assert.Equal(1, tree.Child(0, 0));
        Assert.Equal(2, tree.Child(0, 1));
        Assert.Equal(3, tree.Child(2, 0));
        Assert.Equal(-1, tree.Parent(0));
        Assert.Equal(2, tree.Parent(3));
        Assert.Equal("b", tree.GetText(3).ToString());
        Assert.Equal(new[] { new TextSpan(1, 2) }, tree.Trivia.ToArray());
    }

    [Fact]
    public void Freeze_records_leading_and_trailing_trivia()
    {
        var a = new BuildArena();
        var s = State(" a ", a);
        s.Open(K1);
        s.SkipTrivia();
        s.MatchLiteral("a");
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, " a ", language: null);

        Assert.Equal(new[] { new TextSpan(0, 1), new TextSpan(2, 1) }, tree.Trivia.ToArray());
    }

    [Fact]
    public void Freeze_copies_a_shared_node_once_per_occurrence()
    {
        var a = new BuildArena();
        a.HasAmbiguity = true; // a hand-built shared subtree, as only ambiguity produces in a real parse
        var s = State("x", a);
        s.MatchLiteral("x");
        int shared = a.ChildStack[0];
        a.ChildStackCount = 0;
        s.Open(K1);
        a.PushChild(shared);
        a.PushChild(shared);
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, "x", language: null);

        Assert.Equal(3, tree.NodeCount);
        Assert.NotEqual(tree.Child(0, 0), tree.Child(0, 1));
        Assert.Equal(tree.Span(tree.Child(0, 0)), tree.Span(tree.Child(0, 1)));
    }

    [Fact]
    public void Child_index_out_of_range_throws()
    {
        var a = new BuildArena();
        var s = State("x", a);
        s.Open(K1);
        s.MatchLiteral("x");
        int root = s.Close();
        using var tree = TreeFreezer.Freeze(a, root, "x", language: null);

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Child(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Kind(2));
    }

    [Fact]
    public void ErrorTree_is_a_single_error_root()
    {
        using var tree = TreeFreezer.ErrorTree("oops", language: null);
        Assert.Equal(1, tree.NodeCount);
        Assert.Equal(SyntaxKinds.Error, tree.Kind(0));
        Assert.Equal(NodeFlags.Error, tree.Flags(0));
        Assert.Equal(new TextSpan(0, 4), tree.Span(0));
        Assert.Equal(0, tree.ChildCount(0));
        Assert.Equal(0, tree.Trivia.Length);
    }
}
