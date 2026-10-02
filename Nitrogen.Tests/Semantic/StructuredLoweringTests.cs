using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Structured;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class StructuredLoweringTests
{
    [Fact]
    public void Inline_text_argument_projects_a_token_with_its_source_span()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var language = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig],
            [
                new OperationSignature("Test.MakePart", part, SemanticTypes.Text),
                new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part)),
            ])).Build();
        const string source = "plain left";
        using var parsed = language.Parse(source, StructuredModule.PlainPart);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("part.inline", parsed.Tree);
        var file = new ProjectSemantics(project)["part.inline"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var name = Assert.IsType<HirText>(Assert.Single(root.Arguments));
        Assert.Equal("left", name.Value);
        Assert.Equal(source.IndexOf("left", StringComparison.Ordinal), name.Origins[0].Span.Start);
    }

    [Fact]
    public void Inline_text_argument_checks_the_operation_input_at_composition()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var builder = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig],
            [
                new OperationSignature("Test.MakePart", part, SemanticTypes.Scalar),
                new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part)),
            ]));

        Assert.False(builder.TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0011" &&
            diagnostic.Message.Contains("passes text", StringComparison.Ordinal));
    }

    [Fact]
    public void Inline_sequence_argument_checks_the_operation_input_at_composition()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var builder = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig],
            [
                new OperationSignature("Test.MakePart", part, SemanticTypes.Text),
                new OperationSignature("Test.MakeRig", rig, SemanticTypes.Text),
            ]));

        Assert.False(builder.TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0011" &&
            diagnostic.Message.Contains("sequence of Test.Part", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("inline", 0)]
    [InlineData("inline part left;, part right;", 2)]
    public void Inline_sequence_argument_projects_items_without_a_wrapper_rule(string source, int count)
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var language = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig],
            [
                new OperationSignature("Test.MakePart", part, SemanticTypes.Text),
                new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part)),
            ])).Build();
        using var parsed = language.Parse(source, StructuredModule.Inline);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("parts.inline", parsed.Tree);
        var file = new ProjectSemantics(project)["parts.inline"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var parts = Assert.IsType<HirSequence>(Assert.Single(root.Arguments));
        Assert.Equal(count, parts.Items.Count);
    }

    [Fact]
    public void Separated_sequence_projects_only_items_in_source_order()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var language = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig],
            [
                new OperationSignature("Test.MakePart", part, SemanticTypes.Text),
                new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part)),
            ])).Build();
        using var parsed = language.Parse("csv part left;, part right;", StructuredModule.Csv);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("parts.csv", parsed.Tree);
        var file = new ProjectSemantics(project)["parts.csv"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var parts = Assert.IsType<HirSequence>(Assert.Single(root.Arguments));
        Assert.Equal(2, parts.Items.Count);
    }

    [Fact]
    public void Grammar_lowers_names_and_ordered_parts_into_a_typed_rig_operation()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var makePart = new OperationSignature("Test.MakePart", part, SemanticTypes.Text);
        var makeRig = new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part));
        var module = new SemanticModule("Test", [], [part, rig], [makePart, makeRig]);
        var language = new LanguageBuilder().Add(StructuredModule.Instance).AddSemantic(module).Build();
        const string source = "rig part left; part right;";
        using var parsed = language.Parse(source, StructuredModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("rig.structured", parsed.Tree);
        var file = new ProjectSemantics(project)["rig.structured"];

        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal(makeRig, root.Signature);
        var parts = Assert.IsType<HirSequence>(Assert.Single(root.Arguments));
        Assert.Equal(["left", "right"], parts.Items.Select(item =>
            Assert.IsType<HirText>(Assert.Single(Assert.IsType<HirOperation>(item).Arguments)).Value));
        var names = parts.Items.Select(item => Assert.IsType<HirText>(
            Assert.Single(Assert.IsType<HirOperation>(item).Arguments))).ToArray();
        Assert.Equal(source.IndexOf("left", StringComparison.Ordinal), names[0].Origins[0].Span.Start);
        Assert.Equal(source.IndexOf("right", StringComparison.Ordinal), names[1].Origins[0].Span.Start);
        Assert.Equal("rig.structured", names[0].Origins[0].Path);
    }
}
