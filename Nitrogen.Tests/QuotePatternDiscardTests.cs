using Xunit;

namespace Nitrogen.Tests;

public sealed class QuotePatternDiscardTests
{
    [Theory]
    [InlineData("f(1)", true)]
    [InlineData("f(0, 1, 2)", true)]
    [InlineData("f(0, f(1), 2)", false)]
    [InlineData("f()", false)]
    [InlineData("f(0, 2)", false)]
    public void Anonymous_lists_match_independent_prefix_and_suffix(string source, bool expected)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(..$_, 1, ..$_)", new Dictionary<string, QuoteHole>());
        Assert.True(pattern.Success, string.Join("; ", pattern.Diagnostics));
        using var parsed = language.ParseTemplate(source, "QuoteLists.Expr", new Dictionary<string, string>());
        var match = pattern.Pattern!.Match(new(parsed.Tree, parsed.Tree.Root));
        Assert.Equal(expected, match.Matched);
        Assert.Empty(match.Captures);
        Assert.Empty(match.Diagnostics);
    }

    [Fact]
    public void Scalar_discards_are_independent_and_do_not_become_captures()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "$_ + $_", new Dictionary<string, QuoteHole>());
        Assert.True(pattern.Success, string.Join("; ", pattern.Diagnostics));
        using var parsed = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
        var match = pattern.Pattern!.Match(new(parsed.Tree, parsed.Tree.Root));
        Assert.True(match.Matched);
        Assert.Empty(match.Captures);
    }

    [Fact]
    public void Backtracking_restores_captures_and_pending_constraints()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(f(..$_, $X, ..$_), $X)",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        Assert.True(pattern.Success, string.Join("; ", pattern.Diagnostics));
        using var parsed = language.ParseTemplate("f(f(1, 2, 3), 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        var match = pattern.Pattern!.Match(new(parsed.Tree, parsed.Tree.Root));
        Assert.True(match.Matched);
        var capture = Assert.IsType<QuoteArgument.Scalar>(Assert.Single(match.Captures).Value).Value;
        Assert.Equal("2", capture.Tree.GetText(capture.Node).ToString());
        using var different = language.ParseTemplate("f(f(1, 2, 3), 9)", "QuoteLists.Expr", new Dictionary<string, string>());
        var mismatch = pattern.Pattern.Match(new(different.Tree, different.Tree.Root));
        Assert.False(mismatch.Matched);
        Assert.Empty(mismatch.Captures);
    }

    [Fact]
    public void Construction_quotes_cannot_construct_anonymous_discards()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var quote = language.CompileQuote("QuoteLists.Expr", "f(..$_)", new Dictionary<string, QuoteHole>());
        Assert.False(quote.Success);
    }
}
