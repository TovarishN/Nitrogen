using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Inferred;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public static class InferredSignatures
{
    public static OperationSignature Scalars { get; } = new("Test.Scalars", SemanticTypes.Scalar, SemanticTypes.SequenceOf(SemanticTypes.Scalar));
    public static OperationSignature Angles { get; } = new("Test.Angles", SemanticTypes.Angle, SemanticTypes.SequenceOf(SemanticTypes.Angle));
    public static OperationSignature NotSequence { get; } = new("Test.NotSequence", SemanticTypes.Scalar, SemanticTypes.Scalar);
}

public sealed class InferredSequenceTests
{
    static LanguageBuilder Builder(OperationSignature? angles = null) => new LanguageBuilder().Add(InferredModule.Instance)
        .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
        .AddSemantic(new SemanticModule("Test", ["Units"], [], [InferredSignatures.Scalars, angles ?? InferredSignatures.Angles, InferredSignatures.NotSequence]));

    [Theory]
    [InlineData("items 1,1", "Core.Scalar", 2)]
    [InlineData("items 2,2", "Units.Angle", 2)]
    [InlineData("items", "Units.Angle", 0)]
    public void Selected_signature_defines_the_sequence_element_type(string source, string type, int count)
    {
        var language = Builder().Build();
        using var parsed = language.Parse(source, InferredModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("items.test", parsed.Tree);
        var file = new ProjectSemantics(project)["items.test"];
        Assert.Empty(file.Diagnostics());
        var root = Assert.IsType<HirOperation>(Assert.Single(HirLowering.Lower(file, language.SemanticCatalog).Roots));
        var sequence = Assert.IsType<HirSequence>(Assert.Single(root.Arguments));
        Assert.Equal(type, sequence.ElementType.Id);
        Assert.Equal(count, sequence.Items.Count);
        Assert.All(sequence.Items, item => Assert.Equal(sequence.ElementType, item.Type));
        var registry = new ProjectionRegistry(language.SemanticCatalog,
            new[] { InferredSignatures.Scalars, InferredSignatures.Angles }.Select(signature => new ProjectionHandler(signature,
                args => new ProjectedValue(signature.Result, ((IReadOnlyList<ProjectedValue>)args[0].Value).Sum(item => (float)item.Value)))));
        Assert.Empty(HirProjector.Project(root, registry).Diagnostics);
    }

    [Theory]
    [InlineData("items 1,2", 8)]
    [InlineData("items 2,1", 8)]
    public void A_wrong_element_type_reports_its_source(string source, int position)
    {
        var language = Builder().Build();
        using var parsed = language.Parse(source, InferredModule.File);
        var project = new Project(language);
        project.Set("items.test", parsed.Tree);
        var file = new ProjectSemantics(project)["items.test"];
        var error = Assert.Single(file.Diagnostics());
        Assert.Equal("NT0001", error.Code);
        Assert.Equal(new TextSpan(position, 1), error.Span);
        Assert.Empty(HirLowering.Lower(file, language.SemanticCatalog).Roots);
    }

    [Fact]
    public void A_fixed_nonsequence_input_is_a_composition_error()
    {
        var wrong = new OperationSignature("Test.Angles", SemanticTypes.Angle, SemanticTypes.Angle);
        Assert.False(Builder(wrong).TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0011");
    }

    [Fact]
    public void A_computed_nonsequence_input_cannot_lower_even_when_its_list_is_empty()
    {
        var language = Builder().Build();
        using var parsed = language.Parse("bad", InferredModule.Bad);
        var project = new Project(language);
        project.Set("bad.test", parsed.Tree);
        var file = new ProjectSemantics(project)["bad.test"];
        Assert.Equal("NT0001", Assert.Single(file.Diagnostics()).Code);
        Assert.Empty(HirLowering.Lower(file, language.SemanticCatalog).Roots);
    }
}
