using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Inferred;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class SelectedLoweringTests
{
    [Theory]
    [InlineData("items 1,1", "Core.Scalar")]
    [InlineData("items 2,2", "Units.Angle")]
    public void Selected_declarative_values_keep_order_types_and_snapshot_without_unrelated_roots(string source, string type)
    {
        var language = Language();
        using var parsed = language.Parse(source, InferredModule.File);
        var project = new Project(language);
        project.Set("selected.test", parsed.Tree);
        var file = new ProjectSemantics(project)["selected.test"];
        var snapshot = Guid.NewGuid();
        var result = HirLowering.LowerSelected(file, new HashSet<int> { InferredKinds.Item }, snapshot);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { type, type }, result.Roots.Select(root => root.Type.Id));
        Assert.All(result.Roots, root => Assert.Equal(snapshot, root.Origins[0].SnapshotId));
        Assert.Equal(new[] { 6, 8 }, result.Roots.Select(root => root.Origins[0].Span.Start));
    }

    [Fact]
    public void Invalid_selected_root_retains_its_source_diagnostic()
    {
        var language = Language();
        using var parsed = language.Parse("items 1,2", InferredModule.File);
        var project = new Project(language);
        project.Set("selected.test", parsed.Tree);
        var file = new ProjectSemantics(project)["selected.test"];
        var result = HirLowering.LowerSelected(file, new HashSet<int> { InferredKinds.File }, Guid.NewGuid());
        Assert.Empty(result.Roots);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("NH0003", error.Code);
        Assert.Equal(8, error.Origin.Span.Start);
    }

    [Fact]
    public void Selected_syntax_without_lowering_is_diagnosed_instead_of_disappearing()
    {
        var language = Language();
        using var parsed = language.Parse("items 1", InferredModule.File);
        var project = new Project(language);
        project.Set("selected.test", parsed.Tree);
        var result = HirLowering.LowerSelected(new ProjectSemantics(project)["selected.test"],
            new HashSet<int> { InferredKinds.Digits }, Guid.NewGuid());
        Assert.Empty(result.Roots);
        Assert.Equal("NH0005", Assert.Single(result.Diagnostics).Code);
    }

    static Language Language() => new LanguageBuilder().Add(InferredModule.Instance)
        .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
        .AddSemantic(new SemanticModule("Test", ["Units"], [],
            [InferredSignatures.Scalars, InferredSignatures.Angles, InferredSignatures.NotSequence])).Build();
}
