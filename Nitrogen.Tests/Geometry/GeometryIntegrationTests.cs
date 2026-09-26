using System.Numerics;
using Gravity.RagdollEditor;
using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryIntegrationTests
{
    const string Uri = "file:///w/box.geom";

    static (HirOperation Root, GeometryMesh Mesh) Execute(string source)
    {
        var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
        using var parsed = composition.Language.Parse(source, composition.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composition.Language);
        project.Set(Uri, parsed.Tree);
        var file = new ProjectSemantics(project)[Uri];
        Assert.Empty(file.Binding.Diagnostics);
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, composition.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var result = GeometryExecutor.Execute(root, composition);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(root.Origins, result.Origins);
        return (root, Assert.IsType<GeometryMesh>(result.Mesh));
    }

    [Fact]
    public void Box_pipeline_matches_existing_renderable_mesh_exactly()
    {
        var (root, mesh) = Execute("box 1 2 3;");
        var (expectedVertices, expectedIndices) = MeshGenerator.GenerateBox(new Vector3(0.5f, 1f, 1.5f));
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        Assert.Equal(expectedIndices, mesh.Indices);
        Assert.Equal(expectedVertices.Select(vertex => new MeshVertex(vertex.Position, vertex.Normal)), mesh.Vertices);
        Assert.Equal("box 1 2 3;", "box 1 2 3;".Substring(root.Origins[0].Span.Start, root.Origins[0].Span.Length));
        Assert.Equal(new Vector3(0.5f, 1f, 1.5f), mesh.Vertices[0].Position);
    }

    [Fact]
    public void Swapping_dimensions_swaps_x_and_z_extents()
    {
        var (_, first) = Execute("box 1 2 3;");
        var (_, swapped) = Execute("box 3 2 1;");
        Assert.Equal(new Vector3(0.5f, 1f, 1.5f), first.Vertices[0].Position);
        Assert.Equal(new Vector3(1.5f, 1f, 0.5f), swapped.Vertices[0].Position);
    }

    [Theory]
    [InlineData("box 0 2 3;")]
    [InlineData("box -1 2 3;")]
    [InlineData("box 1e999 2 3;")]
    public void Invalid_dimension_never_reaches_host(string source)
    {
        var calls = 0;
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = _ =>
        {
            calls++;
            throw new InvalidOperationException();
        };
        var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor],
            [new HostOperationBinding(BoxMeshModule.BoxSignature, host)]);
        using var parsed = composition.Language.Parse(source, composition.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composition.Language);
        project.Set(Uri, parsed.Tree);
        var file = new ProjectSemantics(project)[Uri];
        Assert.Contains(file.Diagnostics(), diagnostic => diagnostic.Code == "GE0001");
        var lowered = HirLowering.Lower(file, composition.Language.SemanticCatalog);
        Assert.Empty(lowered.Roots);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Language_service_inspects_same_box_then_invalidates_old_root()
    {
        const string valid = "box 1 2 3;";
        var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("geometry", composition.Language,
            new Dictionary<string, Rule> { [".geom"] = composition.StartRules[("Geometry", "Document")] }));
        using var service = new NitrogenLanguageService(registry);
        service.Open(Uri, 1, valid);
        var document = Assert.IsType<DocumentInspection>(service.InspectDocument(Uri));
        var root = Assert.IsType<HirOperation>(Assert.Single(document.Roots));
        Assert.Equal(BoxMeshModule.MeshType, root.Type);
        var child = Assert.IsType<SemanticInspection>(service.Inspect(Uri,
            new LineMap(valid).PositionOf(valid.IndexOf('2'))));
        Assert.IsType<HirConstant>(child.Node);
        Assert.Equal(SemanticTypes.Scalar, child.Type);
        Assert.Equal("2", valid.Substring(child.Node.Origins[0].Span.Start, child.Node.Origins[0].Span.Length));
        Assert.Same(root, child.Root);

        const string invalid = "box 0 2 3;";
        service.Change(Uri, 2, invalid);
        var current = service.InspectDocument(Uri)!;
        Assert.Empty(current.Roots);
        Assert.NotEqual(document.SnapshotId, current.SnapshotId);
        Assert.Null(service.Inspect(Uri, new LineMap(invalid).PositionOf(invalid.IndexOf('0'))));
        var error = Assert.Single(service.Diagnostics(Uri), diagnostic => diagnostic.Code == "GE0001");
        Assert.Equal(new LineMap(invalid).PositionOf(invalid.IndexOf('0')), error.Range.Start);
    }
}
