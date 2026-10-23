using Xunit;

namespace Nitrogen.Tests;

public sealed class QuotePatternWildcardTests
{
    [Theory]
    [InlineData("$_", "1", true)]
    [InlineData("$_ + $_", "1 + 2", true)]
    [InlineData("f($_)", "f(1)", true)]
    [InlineData("f($_)", "f()", false)]
    [InlineData("f($_)", "f(1, 2)", false)]
    [InlineData("f(..$_)", "f()", true)]
    [InlineData("f(..$_)", "f(1, 2)", true)]
    [InlineData("f($_, ..$_)", "f(1, 2, 3)", true)]
    [InlineData("space[..$_]", "space[1 2]", true)]
    [InlineData("required(..$_)", "required(1)", true)]
    public void Discards_match_without_bindings(string pattern, string source, bool expected)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompilePattern("QuoteLists.Expr", pattern, new Dictionary<string, QuoteHole>());
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        using var input = language.ParseTemplate(source, "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.True(input.Success);
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.Equal(expected, result.Matched);
        Assert.Empty(result.Captures);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("$_", "x")]
    [InlineData("\"$_\" /* ..$_ */", "\"$_\"")]
    public void Opaque_and_overlapping_dollar_tokens_use_actual_parser_sites(string pattern, string input)
    {
        var workspace = new Nitrogen.Workspace.GrammarWorkspace { CompositionName = "W", StartRule = "W.Program" };
        workspace.SetGrammar("w.ngr", "syntax module W { token Dollar=\"$_\"; token String=\"\\\"\" [^ '\"']* \"\\\"\"; syntax Program=E; extensible syntax E { | X=\"x\" | Money=Dollar | Str=String } }");
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("; ", snapshot.Diagnostics));
        using var compiled = snapshot.Language!.CompilePattern("W.E", pattern, new Dictionary<string, QuoteHole>());
        using var source = snapshot.Language.ParseTemplate(input, "W.E", new Dictionary<string, string>());
        Assert.True(compiled.Success); Assert.True(source.Success);
        Assert.True(compiled.Pattern!.Match(new(source.Tree, source.Tree.Root)).Matched);
    }

    [Fact]
    public void Failed_parser_branch_cannot_leave_the_wrong_wildcard_category()
    {
        var workspace = new Nitrogen.Workspace.GrammarWorkspace { CompositionName = "W", StartRule = "W.Program" };
        workspace.SetGrammar("w.ngr", "syntax module W { syntax A=\"a\"; syntax B=\"b\"; syntax Program=(\"p\" A \"!\" / \"p\" B \"?\"); }");
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("; ", snapshot.Diagnostics));
        using var parsed = snapshot.Language!.ParsePattern("p $_ ?", "W.Program", new Dictionary<string, QuoteHole>());
        Assert.True(parsed.Success); Assert.False(parsed.HasErrors);
        Assert.Equal("W.B", Assert.Single(parsed.Tree.PatternHoleSites).Value.Category);
        using var copy = parsed.Tree.Copy();
        Assert.Equal("W.B", Assert.Single(copy.PatternHoleSites).Value.Category);
        using var compiled = snapshot.Language.CompilePattern("W.Program", "p $_ ?", new Dictionary<string, QuoteHole>());
        using var source = snapshot.Language.ParseTemplate("p b ?", "W.Program", new Dictionary<string, string>());
        Assert.True(compiled.Pattern!.Match(new(source.Tree, source.Tree.Root)).Matched);
    }

    [Fact]
    public void Incompatible_surviving_wildcard_interpretations_fail_compilation()
    {
        var workspace = new Nitrogen.Workspace.GrammarWorkspace { CompositionName = "W", StartRule = "W.Program" };
        workspace.SetGrammar("w.ngr", "syntax module W { syntax A=\"a\"; syntax B=\"b\"; syntax Program=E; extensible syntax E { | One=\"p\" A | Two=\"p\" B } }");
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("; ", snapshot.Diagnostics));
        using var compiled = snapshot.Language!.CompilePattern("W.Program", "p $_", new Dictionary<string, QuoteHole>());
        Assert.False(compiled.Success);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "NQP0001");
    }

    [Fact]
    public void Discards_in_different_categories_and_shapes_are_independent()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Shapes.ShapesModule.Instance).Build();
        using var compiled = language.CompilePattern("Shapes.Decl", "..$_ decl a : $_ { b }", new Dictionary<string, QuoteHole>());
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        using var input = language.ParseTemplate("public static decl a : int { b }", "Shapes.Decl", new Dictionary<string, string>());
        Assert.True(input.Success);
        Assert.True(compiled.Pattern!.Match(new(input.Tree, input.Tree.Root)).Matched);
        Assert.Empty(compiled.Pattern.Match(new(input.Tree, input.Tree.Root)).Captures);
    }

    [Fact]
    public void Underscore_is_reserved_only_in_pattern_contracts()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        var holes = new Dictionary<string, QuoteHole> { ["_"] = QuoteHole.Node("QuoteLists.Expr") };
        using var pattern = language.CompilePattern("QuoteLists.Expr", "$_", holes);
        Assert.Contains(pattern.Diagnostics, d => d.Code == "NQP0002");
        using var construction = language.CompileQuote("QuoteLists.Expr", "$_", holes);
        Assert.True(construction.Success);
        using var ordinaryTemplate = language.ParseTemplate("$_", "QuoteLists.Expr", new Dictionary<string, QuoteHole>());
        Assert.False(ordinaryTemplate.Success);
    }

    [Fact]
    public void Underscore_prefix_is_a_named_capture()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompilePattern("QuoteLists.Expr", "$_Name",
            new Dictionary<string, QuoteHole> { ["_Name"] = QuoteHole.Node("QuoteLists.Expr") });
        Assert.True(compiled.Success);
        using var input = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.Single(compiled.Pattern!.Match(new(input.Tree, input.Tree.Root)).Captures);
    }
}
