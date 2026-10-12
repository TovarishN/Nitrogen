using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteOwnershipTests
{
    [Fact]
    public void Instances_own_their_storage_after_sources_and_compilation_are_disposed()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        var holes = new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") };
        var compiled = language.CompileQuote("QuoteLists.Expr", "f(..$Args, ..$Args)", holes);
        holes.Clear();
        var source = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
        var items = new List<QuoteNode> { new(source.Tree, source.Tree.Root) };
        var args = new Dictionary<string, QuoteArgument> { ["Args"] = QuoteArgument.List(items) };
        using var first = compiled.Template!.Instantiate(args);
        using var second = compiled.Template.Instantiate(args);
        items.Clear();
        source.Dispose();
        var savedTemplate = compiled.Template;
        compiled.Dispose();
        using var invalid = savedTemplate.Instantiate(args);
        Assert.Equal("NQ0003", Assert.Single(invalid.Diagnostics).Code);
        Assert.Equal("f(1 + 2, 1 + 2)", first.Tree!.Text);
        Assert.Empty(TemplateListInstantiationTests.Lower(language, first.Tree).Diagnostics);
        first.Dispose();
        Assert.Empty(TemplateListInstantiationTests.Lower(language, second.Tree!).Diagnostics);
        for (int n = 0; n < second.Tree!.NodeCount; n++) Assert.NotNull(second.Tree.GetText(n).ToString());
    }
}
