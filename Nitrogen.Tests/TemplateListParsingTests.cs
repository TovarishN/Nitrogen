using Nitrogen.Tests.QuoteLists;
using Xunit;

namespace Nitrogen.Tests;

public sealed class TemplateListParsingTests
{
    internal static readonly Language Language = new LanguageBuilder().Add(QuoteListsModule.Instance).Build();
    static readonly Dictionary<string, QuoteHole> Holes = new()
    {
        ["Args"] = QuoteHole.List("QuoteLists.Expr"),
        ["Value"] = QuoteHole.Node("QuoteLists.Expr"),
    };

    [Theory]
    [InlineData("f(..$Args, ..$Args)", 2)]
    [InlineData("  f(..$Args)", 1)]
    [InlineData("f(1, ..$Args, $Value)", 1)]
    [InlineData("required(..$Args)", 1)]
    [InlineData("space[..$Args ..$Args]", 2)]
    [InlineData("semi(..$Args; 1)", 1)]
    public void List_markers_match_only_at_item_sites(string text, int count)
    {
        using var parsed = Language.ParseTemplate(text, "QuoteLists.Expr", Holes);
        Assert.True(parsed.Success, text);
        Assert.Equal(count, Enumerable.Range(0, parsed.Tree.NodeCount)
            .Count(n => parsed.Tree.Kind(n) == SyntaxKinds.ListHole));
    }

    [Theory]
    [InlineData("..$Args + 1")]
    [InlineData("f($Args)")]
    [InlineData("f(..$Value)")]
    [InlineData("f(..$Unknown)")]
    public void Invalid_marker_shape_or_site_fails(string text)
    {
        using var parsed = Language.ParseTemplate(text, "QuoteLists.Expr", Holes);
        Assert.False(parsed.Success);
    }

    [Fact]
    public void Ordinary_input_cannot_use_markers()
    {
        using var parsed = Language.Parse("f(..$Args);", QuoteListsModule.Program);
        Assert.True(parsed.HasErrors);
    }
}
