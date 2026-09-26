using System.Numerics;
using Gravity.RagdollEditor;
using Nitrogen.Geometry;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryDefinitionIntegrationTests
{
    const string DefinitionsUri = "file:///w/definitions.geom";
    const string SceneUri = "file:///w/scene.geom";
    const string Definition = "def crate(width: Scalar, height: Scalar, depth: Scalar) = box width height depth;";
    const string Scene = "make crate(1,2,3); make crate(3,2,1);";

    static (ModuleComposition Composition, NitrogenLanguageService Service) Open(string definition = Definition,
        string scene = Scene)
    {
        var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("geometry", composition.Language,
            new Dictionary<string, Rule> { [".geom"] = composition.StartRules[("Geometry", "Document")] }));
        var service = new NitrogenLanguageService(registry);
        service.Open(DefinitionsUri, 1, definition);
        service.Open(SceneUri, 1, scene);
        return (composition, service);
    }

    [Fact]
    public void Two_file_calls_are_inspectable_navigable_and_match_existing_meshes()
    {
        var (composition, service) = Open();
        using (service)
        {
            var document = Assert.IsType<DocumentInspection>(service.InspectDocument(SceneUri));
            Assert.Empty(document.Diagnostics);
            Assert.Equal(2, document.Roots.Count);
            var child = Assert.IsType<SemanticInspection>(service.Inspect(SceneUri,
                new LineMap(Scene).PositionOf(Scene.IndexOf('2'))));
            Assert.IsType<HirConstant>(child.Node);
            Assert.Equal(SemanticTypes.Scalar, child.Type);
            Assert.Equal(2f, ((HirConstant)child.Node).Value);
            Assert.Same(document.Roots[0], child.Root);

            var declaration = Assert.Single(service.Definition(SceneUri,
                new LineMap(Scene).PositionOf(Scene.IndexOf("crate", StringComparison.Ordinal))));
            Assert.Equal(DefinitionsUri, declaration.Uri);
            Assert.Equal(new LineMap(Definition).PositionOf(Definition.IndexOf("crate", StringComparison.Ordinal)),
                declaration.Range.Start);

            var dimensions = new[] { new Vector3(0.5f, 1f, 1.5f), new Vector3(1.5f, 1f, 0.5f) };
            for (int i = 0; i < document.Roots.Count; i++)
            {
                var result = GeometryExecutor.Execute(document.Roots[i], composition);
                Assert.Empty(result.Diagnostics);
                var mesh = Assert.IsType<GeometryMesh>(result.Mesh);
                var (vertices, indices) = MeshGenerator.GenerateBox(dimensions[i]);
                Assert.Equal(vertices.Select(vertex => new MeshVertex(vertex.Position, vertex.Normal)), mesh.Vertices);
                Assert.Equal(indices, mesh.Indices);
            }
        }
    }

    [Fact]
    public void Changing_or_closing_definition_invalidates_existing_call_inspection()
    {
        var (_, service) = Open();
        using (service)
        {
            var before = service.InspectDocument(SceneUri)!;
            Assert.Equal(2, before.Roots.Count);
            const string invalid = "def crate(width: Mesh, height: Scalar, depth: Scalar) = box width height depth;";
            service.Change(DefinitionsUri, 2, invalid);
            var after = service.InspectDocument(SceneUri)!;
            Assert.NotEqual(before.SnapshotId, after.SnapshotId);
            Assert.Empty(after.Roots);
            Assert.Contains(service.Diagnostics(DefinitionsUri), item =>
                item.Code == "GD0001" && item.Range.Start ==
                new LineMap(invalid).PositionOf(invalid.IndexOf("Mesh", StringComparison.Ordinal)));

            service.Change(DefinitionsUri, 3, Definition);
            Assert.Equal(2, service.InspectDocument(SceneUri)!.Roots.Count);
            service.Close(DefinitionsUri);
            var closed = service.InspectDocument(SceneUri)!;
            Assert.Empty(closed.Roots);
            Assert.All(closed.Diagnostics, item => Assert.Equal("GD0002", item.Code));
            Assert.Equal(2, closed.Diagnostics.Count);
        }
    }

    [Fact]
    public void Recursive_definition_cannot_call_geometry_host()
    {
        var calls = 0;
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = _ =>
        {
            calls++;
            throw new InvalidOperationException();
        };
        var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor],
            [new HostOperationBinding(BoxMeshModule.BoxSignature, host)]);
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("geometry", composition.Language,
            new Dictionary<string, Rule> { [".geom"] = composition.StartRules[("Geometry", "Document")] }));
        using var service = new NitrogenLanguageService(registry);
        service.Open(DefinitionsUri, 1,
            "def loop(a: Scalar, b: Scalar, c: Scalar) = make loop(a,b,c);");
        service.Open(SceneUri, 1, "make loop(1,2,3);");
        var document = service.InspectDocument(SceneUri)!;
        Assert.Empty(document.Roots);
        Assert.Equal("GD0004", Assert.Single(document.Diagnostics).Code);
        Assert.Equal(0, calls);
    }
}
