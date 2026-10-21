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
    [InlineData("pair(f(1, 2, 3))", "f(1, 2)")]
    [InlineData("pair(f(1))", "f(1)")]
    [InlineData("twice()", "f()") ]
    [InlineData("twice(3; 4)", "f(3, 4, 3, 4)")]
    [InlineData("(1 + 2) times", "(1 + 2) * 2")]
    [InlineData("3 duplicate", "3 + 3")]
    [InlineData("swap(1 + 2)", "2 + 1")]
    [InlineData("f(swap(1))", "f(9)")]
    [InlineData("f(swap(2))", "f(2)")]
    [InlineData("swap(swap(1 + 2))", "2 + 1")]
    [InlineData("nested(2)", "1 + 2")]
    [InlineData("composite(3)", "3 * 2")]
    [InlineData("middle(f(1, 3, 4, 2))", "f(3, 4)")]
    [InlineData("middle(f(1, 2))", "f()")]
    [InlineData("middle(f(3, 4))", "f(3, 4)")]
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
    public void Sequence_search_exhaustion_stops_before_fallback()
    {
        var language = CreateLanguage();
        using var input = language.ParseTemplate("limit(f(" + string.Join(",", Enumerable.Repeat("1", 45)) + "))",
            "QuoteHelpers.Expr", new Dictionary<string, string>());
        Assert.True(input.Success);
        var result = TemplateListInstantiationTests.Lower(language, input.Tree);
        Assert.Contains(result.Diagnostics, d => d.Code == "NH0009" && d.Message.Contains("work limit", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Roots, root => TemplateListInstantiationTests.Shape(root) == "9:Core.Scalar");
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

    [Theory]
    [InlineData("repeated(f(1, 2, 2))", "2:Core.Scalar")]
    [InlineData("swap(1)", "9:Core.Scalar")]
    [InlineData("swap(2)", "2:Core.Scalar")]
    public void Match_helpers_can_return_typed_non_operation_roots(string input, string expected)
    {
        var language = CreateLanguage();
        using var parsed = language.ParseTemplate(input, "QuoteHelpers.Expr", new Dictionary<string, string>());
        var lowered = TemplateListInstantiationTests.Lower(language, parsed.Tree);
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal(expected, TemplateListInstantiationTests.Shape(Assert.Single(lowered.Roots)));
    }

    [Fact]
    public void Selected_arm_construction_failures_keep_the_result_definition_as_related_source()
    {
        var language=CreateLanguage();
        using var parsed=language.ParseTemplate("requiredmiddle(f(1,2))","QuoteHelpers.Expr",new Dictionary<string,string>());
        var project=new Nitrogen.Binding.Project(language);
        project.Set("caller.quote",parsed.Tree);
        using var semantics=new Nitrogen.Semantics.ProjectSemantics(project);
        var lowered=HirLowering.Lower(semantics["caller.quote"],language.SemanticCatalog);
        var error=Assert.Single(lowered.Diagnostics);
        Assert.Equal("NH0010",error.Code);
        Assert.Equal("caller.quote",error.Origin.Path);
        var related=Assert.Single(error.Related);
        var expected=QuoteHelpersModule.Instance.QuoteHelpers.Single(helper=>helper.Name=="QuoteHelpers.RequireMiddle").Match!.Arms[0].ResultSource;
        Assert.Equal(expected.Path,related.Path);
        Assert.Equal(new TextSpan(expected.Start,expected.Length),related.Span);
    }

    [Fact]
    public void Editor_diagnostics_keep_the_selected_arm_for_failed_construction()
    {
        var language=CreateLanguage();
        var registry=new Nitrogen.LanguageService.LanguageRegistry();
        registry.Add(new("QuoteHelpers",language,new Dictionary<string,Rule> { [".quotehelpers"]=QuoteHelpersModule.Program }));
        using var service=new Nitrogen.LanguageService.NitrogenLanguageService(registry);
        const string uri="file:///tmp/selected-arm.quotehelpers";
        service.Open(uri,1,"requiredmiddle(f(1,2));");
        var error=Assert.Single(service.Diagnostics(uri),diagnostic=>diagnostic.Code=="NH0010");
        var related=Assert.Single(error.Related!);
        var expected=QuoteHelpersModule.Instance.QuoteHelpers.Single(helper=>helper.Name=="QuoteHelpers.RequireMiddle").Match!.Arms[0].ResultSource;
        Assert.Equal(new Uri(expected.Path).AbsoluteUri,related.Uri);
        Assert.Equal(new Nitrogen.LanguageService.LineMap(File.ReadAllText(expected.Path)).RangeOf(new(expected.Start,expected.Length)),related.Range);
    }
}
