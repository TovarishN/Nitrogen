using Xunit;

namespace Nitrogen.Tests;

public sealed class QuotePatternBudgetTests
{
    [Fact]
    public void Work_limit_covers_validation_and_structural_comparison()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompilePattern("QuoteLists.Expr", "1", new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        var root = new QuoteNode(input.Tree, input.Tree.Root);
        // Num and its Number child are each visited during validation and matching.
        var limited = compiled.Pattern!.Match(root, new QuoteMatchOptions { MaxWorkSteps = 3 });
        Assert.False(limited.Matched);
        Assert.Empty(limited.Captures);
        Assert.Equal("NQP0006", Assert.Single(limited.Diagnostics).Code);
        Assert.True(compiled.Pattern.Match(root, new QuoteMatchOptions { MaxWorkSteps = 4 }).Matched);
        Assert.Throws<ArgumentOutOfRangeException>(() => compiled.Pattern.Match(root, new QuoteMatchOptions { MaxWorkSteps = 0 }));
        Assert.Throws<ArgumentNullException>(() => compiled.Pattern.Match(root, null!));
    }

    [Fact]
    public void Cancellation_is_not_an_ordinary_mismatch()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompilePattern("QuoteLists.Expr", "$_", new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate("f(1, 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => compiled.Pattern!.Match(new(input.Tree, input.Tree.Root),
            new QuoteMatchOptions { CancellationToken = cancellation.Token }));
    }

    [Fact]
    public void Discarded_input_still_consumes_validation_budget()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompilePattern("QuoteLists.Expr", "$_", new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate("f(1, 2, 3)", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root), new QuoteMatchOptions { MaxWorkSteps = 2 });
        Assert.Equal("NQP0006", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Captures);
        Assert.True(compiled.Pattern.Match(new(input.Tree, input.Tree.Root)).Matched);
    }
}
