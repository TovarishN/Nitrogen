using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.QuoteLists;
using Xunit;

namespace Nitrogen.Tests;

public sealed class TemplateListInstantiationTests
{
    internal static Language CreateLanguage() => new LanguageBuilder().Add(QuoteListsModule.Instance)
        .AddSemantic(new SemanticModule("Test", [], [],
        [
            new OperationSignature("Test.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar),
            new OperationSignature("Test.Mul", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar),
            new OperationSignature("Test.Call", SemanticTypes.Scalar, SemanticTypes.SequenceOf(SemanticTypes.Scalar)),
        ])).Build();

    internal static string Shape(HirNode node) => node switch
    {
        HirOperation op => op.Signature.Id + "(" + string.Join(",", op.Arguments.Select(Shape)) + ")",
        HirSequence list => "[" + string.Join(",", list.Items.Select(Shape)) + "]",
        HirConstant value => value.Value + ":" + value.Type,
        _ => throw new InvalidOperationException(node.GetType().Name),
    };

    internal static LoweringResult Lower(Language language, SyntaxTree tree)
    {
        var project = new Project(language);
        project.Set("quote.test", tree);
        Assert.Empty(project.Diagnostics("quote.test"));
        var file = new ProjectSemantics(project)["quote.test"];
        Assert.Empty(file.Diagnostics());
        return HirLowering.Lower(file, language.SemanticCatalog);
    }

    [Theory]
    [InlineData("forward()", "f(1, 2)")]
    [InlineData("forward(3)", "f(1, 3, 2)")]
    [InlineData("forward(3; 4)", "f(1, 3, 4, 2)")]
    [InlineData("twice()", "f()")]
    [InlineData("twice(3; 4)", "f(3, 4, 3, 4)")]
    [InlineData("ends()", "f(1)")]
    [InlineData("ends(3)", "f(3, 1, 3)")]
    [InlineData("need(3)", "required(3)")]
    [InlineData("forward(3 + 4)", "f(1, 3 + 4, 2)")]
    public void Rewrite_matches_explicit_program(string source, string expected)
    {
        var language = CreateLanguage();
        using var actualParse = language.ParseTemplate(source, "QuoteLists.Expr", new Dictionary<string, string>());
        using var expectedParse = language.ParseTemplate(expected, "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.True(actualParse.Success);
        Assert.True(expectedParse.Success);
        var actual = Lower(language, actualParse.Tree);
        var explicitResult = Lower(language, expectedParse.Tree);
        Assert.Empty(actual.Diagnostics);
        Assert.Empty(explicitResult.Diagnostics);
        Assert.Equal(Shape(Assert.Single(explicitResult.Roots)), Shape(Assert.Single(actual.Roots)));
    }

    [Fact]
    public void Empty_required_expansion_reports_cardinality()
    {
        var language = CreateLanguage();
        using var parsed = language.ParseTemplate("need()", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = Lower(language, parsed.Tree);
        Assert.Contains(result.Diagnostics, d => d.Code == "NH0010");
    }
}
