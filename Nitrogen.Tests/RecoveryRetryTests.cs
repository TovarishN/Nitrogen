using Xunit;

namespace Nitrogen.Tests;

public unsafe class RecoveryRetryTests
{
    static int _calls;
    static readonly Language Language = new LanguageBuilder().Build();
    static readonly Rule Start = new("Start", &Parse);
    static readonly RepairSite Semicolon = new(1, "';'", new([], [], [], true), []);

    static bool Parse(ref ParserState s)
    {
        _calls++;
        var mark = s.Mark();
        s.Open(SyntaxKinds.Group);
        if (!s.MatchLiteral("x")) { s.Reset(mark); return false; }
        if (!s.MatchLiteral(";"))
        {
            if (!s.Recovering) { s.Reset(mark); return false; }
            s.Recover(Semicolon);
            s.PushMissing(SyntaxKinds.Literal);
        }
        s.Close();
        return true;
    }

    [Fact]
    public void Failed_reparse_without_grafts_does_not_repeat_the_strict_pass()
    {
        using var previous = Language.ParseIncremental("x;", Start);
        _calls = 0;
        using var reparsed = Language.Reparse(previous, "x", [new TextChange(1, 1, 0)]);
        Assert.Equal(2, _calls); // One failed strict pass and one recovery pass.
        using var full = Language.Parse("x", Start);
        TreeAssert.Same(full, reparsed);
    }
}
