using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public class LineMapTests
{
    [Fact]
    public void Positions_and_offsets_round_trip()
    {
        var map = new LineMap("ab\r\ncd\nef");
        Assert.Equal(new DocumentPosition(0, 0), map.PositionOf(0));
        Assert.Equal(new DocumentPosition(0, 2), map.PositionOf(2));
        Assert.Equal(new DocumentPosition(1, 0), map.PositionOf(4));
        Assert.Equal(new DocumentPosition(2, 1), map.PositionOf(8));
        Assert.Equal(new DocumentPosition(2, 2), map.PositionOf(9)); // the end of the text
        Assert.Equal(4, map.OffsetOf(new DocumentPosition(1, 0)));
        Assert.Equal(6, map.OffsetOf(new DocumentPosition(1, 9)));   // clamped to the line's end, before its newline
        Assert.Equal(9, map.OffsetOf(new DocumentPosition(7, 0)));   // past the last line: the end of the text
        Assert.Equal(new DocumentRange(new(1, 0), new(1, 2)), map.RangeOf(new TextSpan(4, 2)));
        Assert.Equal(new[] { 2, 2, 2 }, Enumerable.Range(0, 3).Select(map.LineLength));
    }
}
