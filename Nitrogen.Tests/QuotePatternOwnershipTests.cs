using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuotePatternOwnershipTests
{
    [Theory]
    [InlineData("disposed-pattern", "NQP0004")]
    [InlineData("disposed-input", "NQP0004")]
    [InlineData("out-of-range", "NQP0004")]
    [InlineData("other-snapshot", "NQP0005")]
    public void Invalid_storage_never_returns_partial_captures(string scenario, string code)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "$X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        using var source = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        using var foreign = TemplateListInstantiationTests.CreateLanguage()
            .ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        var candidate = scenario switch
        {
            "out-of-range" => new QuoteNode(source.Tree, source.Tree.NodeCount),
            "other-snapshot" => new QuoteNode(foreign.Tree, foreign.Tree.Root),
            _ => new QuoteNode(source.Tree, source.Tree.Root),
        };
        var matcher = pattern.Pattern!;
        if (scenario == "disposed-pattern") pattern.Dispose();
        if (scenario == "disposed-input") source.Dispose();
        var result = matcher.Match(candidate);
        Assert.False(result.Matched);
        Assert.Empty(result.Captures);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Captures_borrow_input_and_keep_explicit_origins_after_pattern_disposal()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        var pattern = language.CompilePattern("QuoteLists.Expr", "$X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        var source = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
        var origin = new SourceOrigin("caller.test", Guid.NewGuid(), source.Tree.Root, source.Tree.Span(source.Tree.Root));
        var result = pattern.Pattern!.Match(new(source.Tree, source.Tree.Root, origin));
        pattern.Dispose();
        var capture = Assert.IsType<QuoteArgument.Scalar>(result.Captures["X"]).Value;
        Assert.Equal(origin, capture.Origin);
        using var replacement = language.CompileQuote("QuoteLists.Expr", "$X * 2",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        using var built = replacement.Template!.Instantiate(result.Captures);
        source.Dispose();
        Assert.True(built.Success);
        Assert.Contains(origin, built.Origins.Values);
        Assert.Equal("Test.Mul(Test.Add(1:Core.Scalar,2:Core.Scalar),2:Core.Scalar)",
            TemplateListInstantiationTests.Shape(Assert.Single(TemplateListInstantiationTests.Lower(language, built.Tree!).Roots)));
        using var invalid = replacement.Template.Instantiate(result.Captures);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Code == "NQ0003");
    }

    [Fact]
    public void Absent_origins_are_not_invented_and_wrong_categories_are_rejected()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "$X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        using var source = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = pattern.Pattern!.Match(new(source.Tree, source.Tree.Root));
        Assert.Null(Assert.IsType<QuoteArgument.Scalar>(result.Captures["X"]).Value.Origin);
        using var program = language.ParseTemplate("1;", "QuoteLists.Program", new Dictionary<string, string>());
        Assert.Equal("NQP0005", Assert.Single(pattern.Pattern.Match(new(program.Tree, program.Tree.Root)).Diagnostics).Code);
    }

    [Fact]
    public void Recovered_and_uninstantiated_syntax_are_rejected()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Scopes.ScopesModule.Instance).Build();
        using var pattern = language.CompilePattern("Scopes.File", "$File",
            new Dictionary<string, QuoteHole> { ["File"] = QuoteHole.Node("Scopes.File") });
        using var recovered = language.ParseRecovering("unit a { let x = ; }", Nitrogen.Tests.Scopes.ScopesModule.File);
        using var template = language.ParseTemplate("$File", "Scopes.File",
            new Dictionary<string, QuoteHole> { ["File"] = QuoteHole.Node("Scopes.File") });
        var result = pattern.Pattern!.Match(new(recovered.Tree, recovered.Tree.Root));
        Assert.Equal("NQP0004", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Captures);
        var uninstantiated = pattern.Pattern.Match(new(template.Tree, template.Tree.Root));
        Assert.False(uninstantiated.Matched);
        Assert.Equal("NQP0004", Assert.Single(uninstantiated.Diagnostics).Code);
    }

    [Theory]
    [InlineData(NodeFlags.Missing)]
    [InlineData(NodeFlags.Skipped)]
    [InlineData(NodeFlags.Ambiguous)]
    [InlineData(NodeFlags.Error)]
    public void Invalid_node_flags_are_rejected_before_capture(NodeFlags flags)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var valid = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        var arena = new BuildArena();
        int node = arena.NewNode(valid.Tree.Kind(valid.Tree.Root), 0, 1, flags, 0, 0);
        using var invalid = TreeFreezer.Freeze(arena, node, "1", language);
        using var pattern = language.CompilePattern("QuoteLists.Expr", "$X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        var result = pattern.Pattern!.Match(new(invalid, invalid.Root));
        Assert.Equal("NQP0004", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Captures);
    }
}
