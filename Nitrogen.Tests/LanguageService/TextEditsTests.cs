using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Applying didChange content changes to a text (LSP positions: 0-based line, UTF-16 column).</summary>
public class TextEditsTests
{
    static DocumentRange R(int line, int character, int endLine, int endCharacter) =>
        new(new DocumentPosition(line, character), new DocumentPosition(endLine, endCharacter));

    [Fact]
    public void An_insert_a_delete_and_a_multi_line_replace()
    {
        Assert.Equal("let x = 41;", TextEdits.Apply("let x = 1;", [new TextChange(R(0, 8, 0, 8), "4")]));
        Assert.Equal("abc", TextEdits.Apply("abc def", [new TextChange(R(0, 3, 0, 7), "")]));
        Assert.Equal("oXree", TextEdits.Apply("one\ntwo\nthree", [new TextChange(R(0, 1, 2, 2), "X")]));
    }

    [Fact]
    public void Changes_apply_in_order_each_to_the_text_before_it_produced()
    {
        Assert.Equal("abcde", TextEdits.Apply("abc", [new TextChange(R(0, 3, 0, 3), "d"), new TextChange(R(0, 4, 0, 4), "e")]));
    }

    [Fact]
    public void A_change_without_a_range_replaces_the_whole_text()
    {
        Assert.Equal("new", TextEdits.Apply("old", [new TextChange(null, "new")]));
        Assert.Equal("abc", TextEdits.Apply("old", [new TextChange(null, "ab"), new TextChange(R(0, 2, 0, 2), "c")]));
    }

    [Fact]
    public void Columns_count_utf16_units()
    {
        Assert.Equal("a😀Xb", TextEdits.Apply("a😀b", [new TextChange(R(0, 3, 0, 3), "X")])); // 😀 is two UTF-16 units
    }

    [Fact]
    public void Positions_past_a_line_or_the_text_clamp()
    {
        Assert.Equal("ab", TextEdits.Apply("ab\ncd", [new TextChange(R(0, 5, 9, 0), "")]));
        Assert.Equal("aX\r\nb", TextEdits.Apply("a\r\nb", [new TextChange(R(0, 5, 0, 5), "X")]));
    }
}
