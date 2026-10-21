using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

public sealed class QuotePatternPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(300)]
    public void Representative_searches_fit_the_default_budget(int count)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var input = language.ParseTemplate("f(" + string.Join(",", Enumerable.Repeat("1", count)) + ")",
            "QuoteLists.Expr", new Dictionary<string, string>());
        var root = new QuoteNode(input.Tree, input.Tree.Root);
        foreach (string pattern in new[] { "f(..$A, 1)", "f(..$_, $X, ..$_, $Y, ..$_)", "f(..$_, $X, ..$_, $Y, ..$_, $Z, ..$_)" })
        {
            var contracts = new Dictionary<string, QuoteHole>();
            foreach (string name in new[] { "X", "Y", "Z" }.Where(n => pattern.Contains("$" + n))) contracts[name] = QuoteHole.Node("QuoteLists.Expr");
            if (pattern.Contains("$A")) contracts["A"] = QuoteHole.List("QuoteLists.Expr");
            using var compiled = language.CompilePattern("QuoteLists.Expr", pattern, contracts);
            Assert.True(compiled.Success);
            Assert.True(compiled.Pattern!.Match(root).Matched); // warm-up
            int low = 1, high = 1_000_000;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                var match = compiled.Pattern.Match(root, new QuoteMatchOptions { MaxWorkSteps = middle });
                if (match.Diagnostics.Any(d => d.Code == "NQP0006")) low = middle + 1;
                else high = middle;
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) Assert.True(compiled.Pattern.Match(root).Matched);
            watch.Stop();
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            output.WriteLine($"{count} items; {pattern}; {low} steps; {watch.Elapsed.TotalMilliseconds / 10:F3} ms/match; {bytes / 10} bytes/match");
        }
    }

    [Fact]
    public void Adversarial_search_terminates_at_the_default_limit()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompilePattern("QuoteLists.Expr", "f(..$_, $_, ..$_, $_, ..$_, $_, ..$_, $_, ..$_, 999)", new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate("f(" + string.Join(",", Enumerable.Repeat("1", 45)) + ")", "QuoteLists.Expr", new Dictionary<string, string>());
        var watch = Stopwatch.StartNew();
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        watch.Stop();
        Assert.Equal("NQP0006", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Captures);
        output.WriteLine($"45-item adversarial failure; 1,000,000-step limit; {watch.Elapsed.TotalMilliseconds:F3} ms");
    }
}
