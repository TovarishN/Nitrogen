using Xunit;

namespace Nitrogen.Tests;

/// <summary>The recovery pass (issue 235) through a hand-written rule shaped like generated code.</summary>
public unsafe class RecoveryRuntimeTests
{
    static readonly Language Language = new LanguageBuilder().Build();
    static readonly Rule Field = new("Field", &ParseField);

    static int MatchName(ReadOnlySpan<char> text, int position)
    {
        int end = position;
        while (end < text.Length && char.IsAsciiLetterLower(text[end])) end++;
        return end > position ? end : -1;
    }

    static int MatchNumber(ReadOnlySpan<char> text, int position)
    {
        int end = position;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return end > position ? end : -1;
    }

    static LookaheadSet Literal(string text) => new([text], [], new delegate*<ReadOnlySpan<char>, int, int>[0], false);

    static readonly LookaheadSet Number = new([], [], new delegate*<ReadOnlySpan<char>, int, int>[] { &MatchNumber }, false);
    static readonly LookaheadSet End = new([], [], new delegate*<ReadOnlySpan<char>, int, int>[0], true);

    static readonly RepairSite EqualsSite = new(1, "'='", Number,
        new[] { new SyncPoint(1, Literal("=")), new SyncPoint(2, Number), new SyncPoint(3, Literal(";")) });
    static readonly RepairSite NumberSite = new(2, "Number", Literal(";"),
        new[] { new SyncPoint(2, Number), new SyncPoint(3, Literal(";")) });
    static readonly RepairSite SemicolonSite = new(3, "';'", End, new[] { new SyncPoint(3, Literal(";")) });

    static bool ParseField(ref ParserState s)
    {
        var start = s.Mark();
        s.Open(SyntaxKinds.Group);
        s.SkipTrivia();
        if (!s.MatchToken(SyntaxKinds.Literal, MatchName(s.Text, s.Position), "Name")) return Fail(ref s, start);
    e1:
        s.SkipTrivia();
        if (!s.MatchLiteral("="))
        {
            if (!s.Recovering) return Fail(ref s, start);
            switch (s.Recover(EqualsSite))
            {
                case 1:
                    goto e1;
                case 2:
                    s.PushMissing(SyntaxKinds.Literal);
                    goto e2;
                case 3:
                    s.PushMissing(SyntaxKinds.Literal);
                    s.PushMissing(SyntaxKinds.Literal);
                    goto e3;
                default:
                    s.PushMissing(SyntaxKinds.Literal);
                    s.PushMissing(SyntaxKinds.Literal);
                    s.PushMissing(SyntaxKinds.Literal);
                    goto done;
            }
        }
    e2:
        s.SkipTrivia();
        if (!s.MatchToken(SyntaxKinds.Literal, MatchNumber(s.Text, s.Position), "Number"))
        {
            if (!s.Recovering) return Fail(ref s, start);
            switch (s.Recover(NumberSite))
            {
                case 2:
                    goto e2;
                case 3:
                    s.PushMissing(SyntaxKinds.Literal);
                    goto e3;
                default:
                    s.PushMissing(SyntaxKinds.Literal);
                    s.PushMissing(SyntaxKinds.Literal);
                    goto done;
            }
        }
    e3:
        s.SkipTrivia();
        if (!s.MatchLiteral(";"))
        {
            if (!s.Recovering) return Fail(ref s, start);
            switch (s.Recover(SemicolonSite))
            {
                case 3:
                    goto e3;
                default:
                    s.PushMissing(SyntaxKinds.Literal);
                    goto done;
            }
        }
    done:
        s.Close();
        return true;
    }

    static bool Fail(ref ParserState s, scoped in ParseMark mark)
    {
        s.Reset(mark);
        return false;
    }

    static string[] Messages(ParseResult result) =>
        result.Diagnostics.ToArray().Select(d => result.FormatMessage(d) + " " + d.Span).ToArray();

    [Fact]
    public void Valid_input_takes_the_fast_pass_only()
    {
        using var result = Language.Parse("x = 1;", Field);
        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics.ToArray());
        Assert.Equal("(Group \"x\" \"=\" \"1\" \";\")", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void The_recovery_pass_alone_leaves_valid_input_untouched()
    {
        using var result = Language.ParseRecovering("x = 1;", Field);
        Assert.True(result.Success);
        Assert.Equal("(Group \"x\" \"=\" \"1\" \";\")", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void A_missing_element_is_inserted_when_what_follows_it_is_there()
    {
        using var result = Language.Parse("x = 1", Field);
        Assert.False(result.Success);
        Assert.Equal("(Group \"x\" \"=\" \"1\" !Literal)", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected ';' [5..5)" }, Messages(result));
        Assert.True(new SyntaxNode(result.Tree, result.Tree.Child(0, 3)).IsMissing);
    }

    [Fact]
    public void Skipped_input_becomes_skipped_trivia_and_the_element_is_retried()
    {
        using var result = Language.Parse("x = 1 ?? ;", Field);
        Assert.Equal("(Group~ \"x\" \"=\" \"1\" \";\")", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(6, 2) }, result.Tree.SkippedSpans.ToArray());
        Assert.Equal(new[] { new TextSpan(6, 2) }, result.Tree.SkippedTrivia(0).ToArray());
        Assert.True(new SyntaxNode(result.Tree, 0).IsSkipped);
        Assert.Equal(new[] { "expected ';' [6..6)" }, Messages(result));
    }

    [Fact]
    public void A_skip_may_resume_at_a_later_element()
    {
        using var result = Language.Parse("x ?? 1 ;", Field);
        Assert.Equal("(Group~ \"x\" !Literal \"1\" \";\")", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void A_closer_that_closes_no_open_bracket_is_skipped_like_other_input()
    {
        using var result = Language.Parse("x = 1 ?? )", Field);
        Assert.Equal("(Group~ \"x\" \"=\" \"1\" !Literal)", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(6, 4) }, result.Tree.SkippedSpans.ToArray());
        Assert.Equal(new[] { "expected ';' [6..6)" }, Messages(result));
    }

    [Fact]
    public void Errors_separated_by_a_token_are_reported_separately()
    {
        using var result = Language.Parse("x ?? 1 ?? ;", Field);
        Assert.Equal("(Group~ \"x\" !Literal \"1\" \";\")", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected '=' [2..2)", "unexpected input; expected ';' [7..9)" }, Messages(result));
    }

    [Fact]
    public void Without_a_committed_first_element_the_tree_is_a_single_error_root()
    {
        using var result = Language.Parse("?? x = 1;", Field);
        Assert.Equal("Error:\"?? x = 1;\"", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected Name [0..0)" }, Messages(result));
    }

    [Fact]
    public void The_fast_pass_alone_reports_its_failure_over_an_error_root()
    {
        using var result = Language.ParseFast("x = 1", Field);
        Assert.False(result.Success);
        Assert.Equal("Error:\"x = 1\"", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected ';' [5..5)" }, Messages(result));
    }
}
