using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Lexical;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Incremental reparse reuses a rule's old result only when the edit lies outside what the parser
/// examined for it; these pin how far each primitive counts as examined (one past the last index it
/// read, where index <c>text.Length</c> means it saw the end of input).
/// </summary>
public unsafe class ExaminedTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();

    static ParserState Tracked(string text, BuildArena arena)
    {
        arena.Tracking = true;
        return new(text, arena, new MemoTable(), &StandardTrivia.WhitespaceAndComments);
    }

    [Fact]
    public void Literal_failure_examines_up_to_the_mismatch()
    {
        var s = Tracked("decx and a long tail", new BuildArena());

        Assert.False(s.MatchLiteral("decl"));

        Assert.Equal(4, s.Reads.End);
    }

    [Fact]
    public void Keyword_examines_the_following_character()
    {
        var s = Tracked("intx", new BuildArena());
        Assert.False(s.MatchKeyword("int"));
        Assert.Equal(4, s.Reads.End);

        s = Tracked("int x", new BuildArena());
        Assert.True(s.MatchKeyword("int"));
        Assert.Equal(4, s.Reads.End);
    }

    [Fact]
    public void Token_examines_the_character_that_stopped_it()
    {
        var reads = new FurthestRead();
        Assert.Equal(3, CalcModule.MatchNumber("123a", 0, ref reads));
        Assert.Equal(4, reads.End);

        reads = new FurthestRead();
        Assert.Equal(2, CalcModule.MatchNumber("12.x", 0, ref reads)); // tried ".", then 'x' is no digit
        Assert.Equal(4, reads.End);
    }

    [Fact]
    public void Failed_token_with_a_predicate_examines_to_the_end_of_input()
    {
        var reads = new FurthestRead();
        string text = "(* never closed";

        Assert.Equal(-1, LexicalModule.MatchComment(text, 0, ref reads));

        Assert.Equal(text.Length + 1, reads.End);
    }

    [Fact]
    public void Lookahead_past_the_node_is_examined()
    {
        // Expr at 0 tries Call (Identifier then "(" at ';'), settles on Ref "f", and its postfix loop
        // peeks at ';': the result ends at 1 but depends on index 1.
        var records = Calc.ParseTracked("f;", CalcModule.Program);

        var expr = Assert.Single(records, r => r.IsExtensible && r.Start == 0);
        Assert.Equal(1, expr.End);
        Assert.Equal(2, expr.ExaminedEnd);
    }

    [Fact]
    public void Trivia_scan_examines_up_to_the_next_token()
    {
        var s = Tracked("  // c\n  x", new BuildArena());

        s.SkipTrivia();

        Assert.Equal(9, s.Position);
        Assert.InRange(s.Reads.End, s.Position + 1, s.Position + 2);
    }

    [Fact]
    public void Examined_never_precedes_node_end()
    {
        string text = string.Concat(Enumerable.Range(0, 50).Select(i => $"x{i} = a*(b+1) - f(c, 2);\n"));

        var records = Calc.ParseTracked(text, CalcModule.Program);

        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.True(r.Start <= r.End && r.End <= r.ExaminedEnd, r.ToString()));
    }

    [Fact]
    public void Plain_parse_does_not_track()
    {
        var s = new ParserState("intx", new BuildArena(), new MemoTable(), &StandardTrivia.WhitespaceAndComments);

        Assert.False(s.MatchKeyword("int"));

        Assert.Equal(0, s.Reads.End);
    }
}
