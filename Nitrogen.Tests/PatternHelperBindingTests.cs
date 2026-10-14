using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.PatternRefs;
using Xunit;

namespace Nitrogen.Tests;

public sealed class PatternHelperBindingTests
{
    static Language CreateLanguage() => new LanguageBuilder().Add(PatternRefsModule.Instance)
        .AddSemantic(new SemanticModule("Init", ["Core"], [],
        [
            new("Init.Emit", SemanticTypes.Scalar, SemanticTypes.Scalar),
            new("Init.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar),
            new("Init.Sum", SemanticTypes.Scalar, SemanticTypes.SequenceOf(SemanticTypes.Scalar)),
        ])).Build();

    static string Shape(HirNode node) => node switch
    {
        HirOperation op => op.Signature.Id + "(" + string.Join(",", op.Arguments.Select(Shape)) + ")",
        HirSequence list => "[" + string.Join(",", list.Items.Select(Shape)) + "]",
        HirSymbolRef reference => "ref:" + reference.Symbol.Binding.Name,
        HirConstant constant => constant.Value + ":" + constant.Type,
        _ => throw new InvalidOperationException(),
    };

    [Theory]
    [InlineData("input a; emit swap(a + 1)", "Init.Emit(Init.Add(1:Core.Scalar,ref:a))")]
    [InlineData("let a = 40; emit nested(a)", "Init.Emit(Init.Add(1:Core.Scalar,40:Core.Scalar))")]
    [InlineData("let a = 40; emit composite(a)", "Init.Emit(Init.Add(40:Core.Scalar,40:Core.Scalar))")]
    [InlineData("input a; emit middle(f(1, a, 2))", "Init.Emit(Init.Sum([ref:a]))")]
    public void Captures_preserve_caller_symbols_initializers_and_nested_proxy_context(string source, string expected)
    {
        var language = CreateLanguage();
        using var parsed = language.Parse(source, PatternRefsModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("caller.pattern", parsed.Tree);
        var file = new ProjectSemantics(project)["caller.pattern"];
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.Single(lowered.Roots);
        Assert.Equal(expected, Shape(root));
        Assert.All(HirTraversal.PreOrder(root).OfType<HirSymbolRef>(), reference => Assert.Equal("caller.pattern", reference.Symbol.Binding.Path));
        if (source.Contains("40"))
            Assert.Contains(HirTraversal.PreOrder(root).OfType<HirConstant>().SelectMany(node => node.Origins),
                origin => origin.Path == "caller.pattern" && origin.Span.Start == source.IndexOf("40", StringComparison.Ordinal));
    }

    [Fact]
    public void A_selected_result_error_points_to_that_arm_and_the_invocation()
    {
        var language = CreateLanguage();
        using var parsed = language.Parse("emit danger(1)", PatternRefsModule.File);
        var project = new Project(language);
        project.Set("caller.pattern", parsed.Tree);
        var file = new ProjectSemantics(project)["caller.pattern"];
        var diagnostic = Assert.Single(file.Diagnostics(), error => error.Related.Count > 0);
        Assert.Equal(new TextSpan(5, 9), diagnostic.Span);
        var related = Assert.Single(diagnostic.Related);
        var helper = PatternRefsModule.Instance.QuoteHelpers.Single(helper => helper.Name == "PatternRefs.Danger");
        Assert.Equal(helper.Match!.Arms[0].ResultSource.Start, related.Span.Start);
        Assert.NotEqual(helper.Source.Start, related.Span.Start);
        Assert.EndsWith("PatternRefs.ngr", related.Path);
    }

    [Fact]
    public void Unresolved_caller_names_are_not_captured_by_the_helper_module()
    {
        var language = CreateLanguage();
        using var parsed = language.Parse("emit swap(missing + 1)", PatternRefsModule.File);
        var project = new Project(language);
        project.Set("caller.pattern", parsed.Tree);
        var file = new ProjectSemantics(project)["caller.pattern"];
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0002" && diagnostic.Origin.Path == "caller.pattern");
    }
}
