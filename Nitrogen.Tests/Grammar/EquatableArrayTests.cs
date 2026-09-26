using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class EquatableArrayTests
{
    [Fact]
    public void Arrays_with_equal_items_are_equal()
    {
        EquatableArray<string> a = new[] { "x", "y" };
        EquatableArray<string> b = new[] { "x", "y" };
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_items_or_lengths_are_not_equal()
    {
        EquatableArray<string> a = new[] { "x", "y" };
        Assert.NotEqual(a, (EquatableArray<string>)new[] { "x", "z" });
        Assert.NotEqual(a, (EquatableArray<string>)new[] { "x" });
    }

    [Fact]
    public void Default_is_empty()
    {
        EquatableArray<string> empty = default;
        Assert.Empty(empty);
        Assert.Equal(empty, (EquatableArray<string>)new string[0]);
        Assert.Empty(empty);
        Assert.Equal("[]", empty.ToString());
    }

    [Fact]
    public void Indexing_enumeration_and_text()
    {
        EquatableArray<string> a = new[] { "x", "y" };
        Assert.Equal("y", a[1]);
        Assert.Equal(new[] { "x", "y" }, a.ToArray());
        Assert.Equal("[x, y]", a.ToString());
    }

    [Fact]
    public void GrammarSpan_bounds()
    {
        var span = GrammarSpan.FromBounds(3, 7);
        Assert.Equal(new GrammarSpan(3, 4), span);
        Assert.Equal(7, span.End);
        Assert.Equal("[3..7)", span.ToString());
    }
}
