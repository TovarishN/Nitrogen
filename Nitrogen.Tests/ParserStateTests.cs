using Xunit;

namespace Nitrogen.Tests;

public unsafe class ParserStateTests
{
    const int K = (99 << 16) | 1; // an arbitrary non-builtin kind

    static ParserState State(string text, BuildArena arena) =>
        new(text, arena, new MemoTable(), &StandardTrivia.WhitespaceAndComments);

    [Fact]
    public void MatchLiteral_pushes_a_literal_leaf_and_advances()
    {
        var a = new BuildArena();
        var s = State("ab", a);
        Assert.True(s.MatchLiteral("a"));
        Assert.Equal(1, s.Position);
        Assert.Equal(1, a.ChildStackCount);
        int leaf = a.ChildStack[0];
        Assert.Equal(SyntaxKinds.Literal, a.Kind[leaf]);
        Assert.Equal((0, 1), (a.Start[leaf], a.Length[leaf]));
    }

    [Fact]
    public void MatchLiteral_failure_records_expectation_without_pushing()
    {
        var a = new BuildArena();
        var s = State("ab", a);
        s.Position = 1;
        Assert.False(s.MatchLiteral("x"));
        Assert.Equal(1, s.Position);
        Assert.Equal(0, a.ChildStackCount);
        Assert.Equal(1, a.FurthestPosition);
        Assert.Equal(1, a.ExpectedCount);
        Assert.Equal("x", a.ExpectedNames[0]);
        Assert.True(a.ExpectedIsLiteral[0]);
    }

    [Fact]
    public void MatchKeyword_requires_an_identifier_boundary()
    {
        var a = new BuildArena();
        var s = State("iffy", a);
        Assert.False(s.MatchKeyword("if"));
        Assert.Equal(0, s.Position);

        var b = new BuildArena();
        var t = State("if(", b);
        Assert.True(t.MatchKeyword("if"));
        Assert.Equal(2, t.Position);
    }

    [Fact]
    public void MatchToken_uses_the_matcher_end()
    {
        var a = new BuildArena();
        var s = State("abc", a);
        Assert.False(s.MatchToken(K, -1, "Word"));
        Assert.Equal("Word", a.ExpectedNames[0]);
        Assert.False(a.ExpectedIsLiteral[0]);
        Assert.False(s.MatchToken(K, 0, "Word")); // zero-length tokens are failures
        Assert.True(s.MatchToken(K, 3, "Word"));
        Assert.Equal(3, s.Position);
        Assert.Equal(K, a.Kind[a.ChildStack[0]]);
    }

    [Fact]
    public void Close_spans_its_children_and_becomes_a_child()
    {
        var a = new BuildArena();
        var s = State("a b", a);
        s.Open(K);
        s.MatchLiteral("a");
        s.SkipTrivia();
        s.MatchLiteral("b");
        int n = s.Close();

        Assert.Equal(K, a.Kind[n]);
        Assert.Equal((0, 3), (a.Start[n], a.Length[n]));
        Assert.Equal(2, a.ChildCount[n]);
        Assert.Equal(SyntaxKinds.Literal, a.Kind[a.ChildLog[a.ChildStart[n]]]);
        Assert.Equal(1, a.ChildStackCount);
        Assert.Equal(n, a.ChildStack[0]);
        Assert.Equal(0, a.FrameCount);
    }

    [Fact]
    public void Close_without_children_is_empty_at_the_open_position()
    {
        var a = new BuildArena();
        var s = State("a b", a);
        s.Position = 2;
        s.Open(K);
        int n = s.Close();
        Assert.Equal((2, 0), (a.Start[n], a.Length[n]));
    }

    [Fact]
    public void PushEmpty_adds_a_zero_length_leaf_without_moving()
    {
        var a = new BuildArena();
        var s = State("ab", a);
        s.Position = 1;
        s.PushEmpty();
        int n = a.ChildStack[0];
        Assert.Equal(SyntaxKinds.Empty, a.Kind[n]);
        Assert.Equal((1, 0), (a.Start[n], a.Length[n]));
        Assert.Equal(1, s.Position);
    }

    [Fact]
    public void Reset_truncates_the_arena_when_no_memo_was_written()
    {
        var a = new BuildArena();
        var s = State("ab", a);
        var mark = s.Mark();
        s.Open(K);
        s.MatchLiteral("a");
        s.Reset(mark);
        Assert.Equal(0, s.Position);
        Assert.Equal(0, a.NodeCount);
        Assert.Equal(0, a.ChildStackCount);
        Assert.Equal(0, a.FrameCount);
    }

    [Fact]
    public void Reset_keeps_nodes_when_a_memo_was_written_after_the_mark()
    {
        var a = new BuildArena();
        var s = State("ab", a);
        var mark = s.Mark();
        s.MatchLiteral("a");
        a.MemoWrites++;
        s.Reset(mark);
        Assert.Equal(0, s.Position);
        Assert.Equal(1, a.NodeCount);
        Assert.Equal(0, a.ChildStackCount);
    }

    [Fact]
    public void Expectations_keep_only_the_furthest_position_without_duplicates()
    {
        var a = new BuildArena();
        var s = State("abc", a);
        s.MatchLiteral("x");            // at 0
        s.Position = 2;
        s.MatchLiteral("y");            // at 2: resets the set
        s.MatchLiteral("y");            // duplicate
        s.MatchLiteral("z");
        s.Position = 1;
        s.MatchLiteral("w");            // behind the furthest: ignored
        Assert.Equal(2, a.FurthestPosition);
        Assert.Equal(2, a.ExpectedCount);
        Assert.Equal(new[] { "y", "z" }, a.ExpectedNames[..2]);
    }

    [Fact]
    public void Arena_grows_past_its_initial_capacity()
    {
        var a = new BuildArena();
        string text = new('a', 5000);
        var s = State(text, a);
        s.Open(K);
        for (int i = 0; i < text.Length; i++) Assert.True(s.MatchLiteral("a"));
        int n = s.Close();
        Assert.Equal(5000, a.ChildCount[n]);
        Assert.Equal(5001, a.NodeCount);
    }

    [Fact]
    public void Close_ignores_zero_length_children_at_the_edges()
    {
        var a = new BuildArena();
        var s = State("a  ", a);
        s.Open(K);
        s.PushEmpty();          // at 0
        s.MatchLiteral("a");    // [0, 1)
        s.SkipTrivia();
        s.PushEmpty();          // at 3, after the trivia
        int n = s.Close();
        Assert.Equal((0, 1), (a.Start[n], a.Length[n]));
        Assert.Equal(3, a.ChildCount[n]);
    }

    [Fact]
    public void Close_with_only_zero_length_children_is_empty_at_the_last_one()
    {
        var a = new BuildArena();
        var s = State("  ", a);
        s.Open(K);
        s.SkipTrivia();
        s.PushEmpty();          // at 2
        int n = s.Close();
        Assert.Equal((2, 0), (a.Start[n], a.Length[n]));
    }
}
