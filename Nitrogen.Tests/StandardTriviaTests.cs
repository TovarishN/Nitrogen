using Xunit;

namespace Nitrogen.Tests;

public class StandardTriviaTests
{
    [Theory]
    [InlineData("  x", 2)]
    [InlineData("// c\nx", 5)]
    [InlineData("/* c */x", 7)]
    [InlineData("/* open", 7)]
    [InlineData("x", 0)]
    [InlineData("/x", 0)]
    [InlineData(" \t\r\n /* a */ // b\n y", 19)]
    public void WhitespaceAndComments_skips_to_first_significant_char(string text, int expected)
    {
        Assert.Equal(expected, StandardTrivia.WhitespaceAndComments(text, 0));
    }

    [Fact]
    public void None_skips_nothing()
    {
        Assert.Equal(0, StandardTrivia.None("  x", 0));
    }

    [Fact]
    public void Start_set_covers_every_character_trivia_can_start_at()
    {
        var start = StandardTrivia.WhitespaceAndCommentsStart;
        for (int c = 0; c < 0x3100; c++)
        {
            string text = (char)c + "/*";
            if (StandardTrivia.WhitespaceAndComments(text, 0) > 0)
                Assert.True(start.Matches((char)c), $"U+{c:X4} starts trivia but is not in the start set");
        }
        Assert.False(start.Matches('a'));
        Assert.False(start.Matches('('));
    }
}
