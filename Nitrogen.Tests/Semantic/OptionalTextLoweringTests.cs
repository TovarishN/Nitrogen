using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.OptionalText;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class OptionalTextLoweringTests
{
    static readonly SemanticType Flag = SemanticType.Named("Test", "Flag");

    static LanguageBuilder Builder(SemanticType markInput) =>
        new LanguageBuilder().Add(OptionalTextModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [Flag],
                [new OperationSignature("Test.Flag", Flag, markInput, SemanticTypes.Text)]));

    [Theory]
    [InlineData("flag ! left", "!")]
    [InlineData("flag left", null)]
    public void Optional_text_lowers_presence_and_spelling(string source, string? mark)
    {
        var language = Builder(SemanticTypes.OptionalOf(SemanticTypes.Text)).Build();
        using var parsed = language.Parse(source, OptionalTextModule.Flagged);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("flag.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["flag.txt"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var optional = Assert.IsType<HirOptional>(root.Arguments[0]);
        Assert.Equal(SemanticTypes.OptionalOf(SemanticTypes.Text), optional.Type);
        if (mark is null)
        {
            Assert.Null(optional.Value);
        }
        else
        {
            var text = Assert.IsType<HirText>(optional.Value);
            Assert.Equal(mark, text.Value);
            Assert.Equal(source.IndexOf('!'), text.Origins[0].Span.Start);
        }
        Assert.Equal("left", Assert.IsType<HirText>(root.Arguments[1]).Value);
    }

    [Fact]
    public void Optional_text_requires_an_optional_text_input()
    {
        Assert.False(Builder(SemanticTypes.Text).TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0011" &&
            diagnostic.Message.Contains("passes optional text", StringComparison.Ordinal));
    }

    [Fact]
    public void Optional_text_input_is_not_an_expected_type()
    {
        var language = Builder(SemanticTypes.OptionalOf(SemanticTypes.Text)).Build();
        using var parsed = language.Parse("flag ! left", OptionalTextModule.Flagged);
        var project = new Project(language);
        project.Set("expected.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["expected.txt"];
        var mark = Enumerable.Range(0, file.Tree.NodeCount)
            .First(node => file.Tree.ChildCount(node) == 0 && file.Tree.GetText(node).ToString() == "!");

        Assert.Null(file.DeclarativeTypes.ExpectedTypeOf(mark));
    }
}
