using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Counted;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class RepeatLoweringTests
{
    static Language Language() => new LanguageBuilder().Add(CountedModule.Instance)
        .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
        .AddSemantic(new SemanticModule("Counted", ["Units"], [], [])).Build();

    [Fact]
    public void Declarative_repeat_lowers_a_separated_template_and_projects_the_bound_indices()
    {
        var language = Language();
        using var parsed = language.Parse("repeat 3 as i { i, 10 }", CountedModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("counted.test", parsed.Tree);
        Assert.Empty(project.Diagnostics("counted.test"));
        var file = new ProjectSemantics(project)["counted.test"];
        Assert.Empty(file.Diagnostics());
        var context = new LoweringContext(file, Guid.NewGuid());
        var lowered = HirLowering.LowerNested(context, file.Tree.Root);
        Assert.Empty(context.Reported);
        var root = Assert.IsType<HirRepeat>(lowered);
        Assert.Equal(2, root.Template.Items.Count);
        Assert.Same(root.Iterator.Binding, Assert.IsType<HirSymbolRef>(root.Template.Items[0]).Symbol.Binding);
        Assert.Equal(root.Iterator.Id, Assert.IsType<HirSymbolRef>(root.Template.Items[0]).Symbol.Id);
        var projected = HirProjector.Project(root, new ProjectionRegistry(language.SemanticCatalog, []));
        Assert.Empty(projected.Diagnostics);
        var values = Assert.IsAssignableFrom<IReadOnlyList<ProjectedValue>>(projected.Value!.Value)
            .SelectMany(group => (IReadOnlyList<ProjectedValue>)group.Value).Select(value => (float)value.Value);
        Assert.Equal(new[] { 0f, 10f, 1f, 10f, 2f, 10f }, values);
    }

    [Theory]
    [InlineData("repeat angle 3 as i { i }", "angle 3")]
    [InlineData("repeat 3 as i { angle 10 }", "angle 10")]
    public void Count_and_template_types_are_checked_before_lowering(string source, string site)
    {
        var language = Language();
        using var parsed = language.Parse(source, CountedModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("invalid.counted", parsed.Tree);
        var file = new ProjectSemantics(project)["invalid.counted"];
        var error = Assert.Single(file.Diagnostics());
        Assert.Equal("NT0001", error.Code);
        Assert.Equal(source.IndexOf(site, StringComparison.Ordinal), error.Span.Start);
        Assert.Null(HirLowering.LowerNested(new LoweringContext(file, Guid.NewGuid()), file.Tree.Root));
    }
}
