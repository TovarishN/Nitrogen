using Nitrogen.Semantic;
using Nitrogen.Tests.QuoteHelpers;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteHelperRuntimeTests
{
    static Language CreateLanguage() => new LanguageBuilder().Add(QuoteHelpersModule.Instance)
        .AddSemantic(new SemanticModule("Test", [], [],
        [
            new OperationSignature("Test.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar),
            new OperationSignature("Test.Mul", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar),
            new OperationSignature("Test.Call", SemanticTypes.Scalar, SemanticTypes.SequenceOf(SemanticTypes.Scalar)),
        ])).Build();

    [Theory]
    [InlineData("twice()", "f()")]
    [InlineData("twice(3; 4)", "f(3, 4, 3, 4)")]
    [InlineData("(1 + 2) times", "(1 + 2) * 2")]
    [InlineData("3 duplicate", "3 + 3")]
    public void Helper_expansion_matches_explicit_structural_lowering(string text, string expanded)
    {
        var language = CreateLanguage();
        using var actual = language.ParseTemplate(text, "QuoteHelpers.Expr", new Dictionary<string, string>());
        using var expected = language.ParseTemplate(expanded, "QuoteHelpers.Expr", new Dictionary<string, string>());
        Assert.True(actual.Success);
        Assert.True(expected.Success);
        var lowered = TemplateListInstantiationTests.Lower(language, actual.Tree);
        Assert.Empty(lowered.Diagnostics);
        var explicitLowered = TemplateListInstantiationTests.Lower(language, expected.Tree);
        Assert.Equal(TemplateListInstantiationTests.Shape(Assert.Single(explicitLowered.Roots)),
            TemplateListInstantiationTests.Shape(Assert.Single(lowered.Roots)));
    }

    [Fact]
    public void Constructed_helper_syntax_owns_storage_and_rejects_other_language_snapshots()
    {
        var language = CreateLanguage();
        var helper = QuoteHelpersModule.Instance.QuoteHelpers.Single(h => h.Name.EndsWith(".Multiply", StringComparison.Ordinal));
        var compiled = language.CompileQuote(helper.Category, helper.Text, helper.Parameters);
        var source = language.ParseTemplate("1 + 2", helper.Category, new Dictionary<string, string>());
        using var constructed = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new(source.Tree, source.Tree.Root))
        });
        Assert.True(constructed.Success);
        using var other = CreateLanguage().ParseTemplate("3", helper.Category, new Dictionary<string, string>());
        using var refused = compiled.Template.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new(other.Tree, other.Tree.Root))
        });
        Assert.Contains(refused.Diagnostics, d => d.Code == "NQ0004");
        compiled.Dispose();
        source.Dispose();
        Assert.Equal("Test.Mul(Test.Add(1:Core.Scalar,2:Core.Scalar),2:Core.Scalar)",
            TemplateListInstantiationTests.Shape(Assert.Single(TemplateListInstantiationTests.Lower(language, constructed.Tree!).Roots)));
    }

    [Theory]
    [InlineData("need()", "NH0010")]
    [InlineData("1 cycle", "NH0008")]
    public void Helper_expansion_preserves_required_list_and_cycle_failures(string text, string code)
    {
        var language = CreateLanguage();
        using var parsed = language.ParseTemplate(text, "QuoteHelpers.Expr", new Dictionary<string, string>());
        Assert.True(parsed.Success);
        Assert.Contains(TemplateListInstantiationTests.Lower(language, parsed.Tree).Diagnostics, d => d.Code == code);
    }
}
