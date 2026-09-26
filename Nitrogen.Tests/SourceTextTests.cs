using Xunit;

namespace Nitrogen.Tests;

public class SourceTextTests
{
    [Theory]
    [InlineData("ab\ncd", 0, 1, 1)]
    [InlineData("ab\ncd", 3, 2, 1)]
    [InlineData("ab\r\ncd", 4, 2, 1)]
    [InlineData("ab\ncd", 5, 2, 3)]
    public void GetLineColumn_is_one_based(string text, int position, int line, int column)
    {
        Assert.Equal((line, column), new SourceText(text).GetLineColumn(position));
    }

    [Fact]
    public void GetLineColumn_rejects_positions_outside_the_text()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceText("ab").GetLineColumn(3));
    }

    [Fact]
    public void TextSpan_end_is_start_plus_length()
    {
        Assert.Equal(7, new TextSpan(3, 4).End);
        Assert.Equal("[3..7)", new TextSpan(3, 4).ToString());
    }
}
