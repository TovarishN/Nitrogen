using Xunit;

namespace Nitrogen.Tests;

public class AsciiSetTests
{
    [Fact]
    public void Default_matches_every_character()
    {
        var any = AsciiSet.Any;
        Assert.True(any.IsAny);
        Assert.True(any.Matches('a'));
        Assert.True(any.Matches('é'));
    }

    [Fact]
    public void Of_matches_only_listed_characters()
    {
        var ops = AsciiSet.Of("+-");
        Assert.False(ops.IsAny);
        Assert.True(ops.Matches('+'));
        Assert.True(ops.Matches('-'));
        Assert.False(ops.Matches('*'));
        Assert.False(ops.Matches('é'));
    }

    [Fact]
    public void Range_covers_both_halves_of_ascii()
    {
        var digits = AsciiSet.Range('0', '9');
        var lower = AsciiSet.Range('a', 'z');
        Assert.True(digits.Matches('0') && digits.Matches('9'));
        Assert.False(digits.Matches('a'));
        Assert.True(lower.Matches('a') && lower.Matches('z'));
        Assert.False(lower.Matches('A'));
    }

    [Fact]
    public void Union_and_non_ascii()
    {
        var set = AsciiSet.Of("_").Union(AsciiSet.Range('a', 'z')).WithNonAscii();
        Assert.True(set.Matches('_'));
        Assert.True(set.Matches('q'));
        Assert.True(set.Matches('é'));
        Assert.False(set.Matches('1'));
        Assert.True(AsciiSet.Any.Union(AsciiSet.Of("x")).IsAny);
    }

    [Fact]
    public void Of_rejects_non_ascii()
    {
        Assert.Throws<ArgumentException>(() => AsciiSet.Of("é"));
    }

    [Fact]
    public void Kinds_pack_module_and_local_parts()
    {
        int kind = (7 << 16) | 3;
        Assert.Equal(7, SyntaxKinds.ModuleOf(kind));
        Assert.Equal(3, SyntaxKinds.LocalOf(kind));
        Assert.Equal(0, SyntaxKinds.ModuleOf(SyntaxKinds.List));
        Assert.Equal("List", SyntaxKinds.GetBuiltinName(SyntaxKinds.List));
        Assert.Equal("#99", SyntaxKinds.GetBuiltinName(99));
    }

    [Fact]
    public void Group_is_a_builtin_kind()
    {
        Assert.Equal(6, SyntaxKinds.Group);
        Assert.Equal("Group", SyntaxKinds.GetBuiltinName(SyntaxKinds.Group));
    }
}
