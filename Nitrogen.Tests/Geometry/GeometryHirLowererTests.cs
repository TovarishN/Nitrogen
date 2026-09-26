using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryHirLowererTests
{
    static LoweringResult Lower(string source, Guid snapshot)
    {
        var composed = ModuleComposer.Compose([BoxMeshModule.Descriptor],
            [new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))]);
        using var parsed = composed.Language.Parse(source, composed.StartRules[("Geometry", "Document")]);
        var project = new Project(composed.Language);
        project.Set("sample.geom", parsed.Tree);
        return HirLowering.Lower(new ProjectSemantics(project)["sample.geom"],
            composed.Language.SemanticCatalog, snapshot);
    }

    [Fact]
    public void Checked_box_lowers_ordered_scalar_constants_with_exact_origins()
    {
        const string source = "box 1 2 3;";
        var snapshot = Guid.NewGuid();
        var lowered = Lower(source, snapshot);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal(BoxMeshModule.BoxSignature, root.Signature);
        Assert.Equal(source, source.Substring(root.Origins[0].Span.Start, root.Origins[0].Span.Length));
        Assert.Equal(snapshot, root.Origins[0].SnapshotId);
        Assert.Equal([1f, 2f, 3f], root.Arguments.Select(node => Assert.IsType<HirConstant>(node).Value));
        Assert.Equal(["1", "2", "3"], root.Arguments.Select(node =>
            source.Substring(node.Origins[0].Span.Start, node.Origins[0].Span.Length)));
        Assert.All(root.Arguments, node =>
        {
            Assert.Equal(SemanticTypes.Scalar, node.Type);
            Assert.Equal(snapshot, node.Origins[0].SnapshotId);
        });
    }

    [Fact]
    public void Invalid_dimension_cannot_lower()
    {
        var lowered = Lower("box 0 2 3;", Guid.NewGuid());
        Assert.Empty(lowered.Roots);
        Assert.NotEmpty(lowered.Diagnostics);
    }

    [Fact]
    public void Malformed_document_cannot_lower()
    {
        var lowered = Lower("box 1 nope 3;", Guid.NewGuid());
        Assert.Empty(lowered.Roots);
    }
}
