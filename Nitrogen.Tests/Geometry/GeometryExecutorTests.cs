using System.Numerics;
using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.Geometry.Syntax;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryExecutorTests
{
    static readonly SourceOrigin Origin = new("sample.geom", Guid.NewGuid(), 0, new TextSpan(0, 10));

    static GeometryMesh Triangle() => new(
        [new MeshVertex(Vector3.Zero, Vector3.UnitZ), new MeshVertex(Vector3.UnitX, Vector3.UnitZ),
            new MeshVertex(Vector3.UnitY, Vector3.UnitZ)], [0, 1, 2]);

    static HirOperation Root(float width = 1f, float height = 2f, float depth = 3f) =>
        new(BoxMeshModule.BoxSignature,
            [new HirConstant(width, SemanticTypes.Scalar, Origin),
                new HirConstant(height, SemanticTypes.Scalar, Origin),
                new HirConstant(depth, SemanticTypes.Scalar, Origin)], [Origin]);

    static ModuleComposition Compose(Delegate handler) => ModuleComposer.Compose([BoxMeshModule.Descriptor],
        [new HostOperationBinding(BoxMeshModule.BoxSignature, handler)]);

    [Fact]
    public void Valid_box_invokes_typed_host_once_with_ordered_read_only_arguments()
    {
        var mesh = Triangle();
        var calls = 0;
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = values =>
        {
            calls++;
            Assert.Equal([1f, 2f, 3f], values.Select(value => value.Number));
            Assert.Throws<NotSupportedException>(() => ((IList<ExecutionValue>)values)[0] = values[0]);
            return mesh;
        };
        var root = Root();
        var result = GeometryExecutor.Execute(root, Compose(host));
        Assert.Empty(result.Diagnostics);
        Assert.Same(mesh, result.Mesh);
        Assert.Equal(root.Origins, result.Origins);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Nonfinite_child_prevents_host_call_and_reports_child_origin()
    {
        var calls = 0;
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = _ => { calls++; return Triangle(); };
        var root = Root(float.NaN);
        var result = GeometryExecutor.Execute(root, Compose(host));
        Assert.Null(result.Mesh);
        Assert.Equal("GX0003", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(root.Arguments[0].Origins[0], result.Diagnostics[0].Origin);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Every_nonfinite_dimension_is_reported_before_the_host_runs()
    {
        var calls = 0;
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = _ => { calls++; return Triangle(); };
        var depth = new SourceOrigin("sample.geom", Origin.SnapshotId, 3, new TextSpan(8, 1));
        var root = new HirOperation(BoxMeshModule.BoxSignature,
            [new HirConstant(float.PositiveInfinity, SemanticTypes.Scalar, Origin),
                new HirConstant(2f, SemanticTypes.Scalar, Origin),
                new HirConstant(float.NaN, SemanticTypes.Scalar, depth)], [Origin]);
        var result = GeometryExecutor.Execute(root, Compose(host));
        Assert.Null(result.Mesh);
        Assert.Equal([("GX0003", Origin), ("GX0003", depth)], result.Diagnostics.Select(d => (d.Code, d.Origin)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_dimension_without_a_value_is_reported_at_its_reference()
    {
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = _ => Triangle();
        var composition = Compose(host);
        using var parsed = composition.Language.Parse("def c(w: Scalar, h: Scalar, d: Scalar) = box w h d;",
            composition.StartRules[("Geometry", "Document")]);
        var width = FileBinding.Bind("sample.geom", parsed.Tree).Declarations.First(symbol => symbol.Kind == "parameter");
        var reference = new HirSymbolRef(SemanticSymbol.From(width, "Geometry", SemanticTypes.Scalar), Origin);
        var root = new HirOperation(BoxMeshModule.BoxSignature, [reference, Root().Arguments[1], Root().Arguments[2]], [Origin]);
        var diagnostic = Assert.Single(GeometryExecutor.Execute(root, composition).Diagnostics);
        Assert.Equal(("GX0003", Origin), (diagnostic.Code, diagnostic.Origin));
    }

    [Fact]
    public void Invalid_root_or_catalog_signature_is_rejected()
    {
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = _ => Triangle();
        var wrong = new HirOperation(new OperationSignature("Geometry.BoxMesh", SemanticTypes.Scalar,
            SemanticTypes.Scalar), [new HirConstant(1f, SemanticTypes.Scalar, Origin)], [Origin]);
        Assert.Equal("GX0001", Assert.Single(GeometryExecutor.Execute(wrong, Compose(host)).Diagnostics).Code);
        var other = new HirConstant(1f, SemanticTypes.Scalar, Origin);
        Assert.Equal("GX0001", Assert.Single(GeometryExecutor.Execute(other, Compose(host)).Diagnostics).Code);
    }

    [Fact]
    public void Missing_or_wrong_delegate_is_rejected_before_invocation()
    {
        var optional = new ModuleDescriptor("Geometry", GeometryModule.Instance,
            new SemanticModule("Geometry", [], [BoxMeshModule.MeshType], [BoxMeshModule.BoxSignature]),
            ["Document"], []);
        var noHost = ModuleComposer.Compose([optional], []);
        Assert.Equal("GX0002", Assert.Single(GeometryExecutor.Execute(Root(), noHost).Diagnostics).Code);
        var wrongHost = Compose((Func<object?>)(() => null));
        Assert.Equal("GX0002", Assert.Single(GeometryExecutor.Execute(Root(), wrongHost).Diagnostics).Code);
        var calls = 0;
        Func<object, GeometryMesh> contravariant = _ => { calls++; return Triangle(); };
        Assert.Equal("GX0002", Assert.Single(GeometryExecutor.Execute(Root(), Compose(contravariant)).Diagnostics).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Null_throw_and_invalid_mesh_from_host_are_source_diagnostics()
    {
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> empty = _ => null!;
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> thrown = _ => throw new InvalidOperationException();
        Func<IReadOnlyList<ExecutionValue>, GeometryMesh> invalid = _ => new GeometryMesh(
            [new MeshVertex(Vector3.Zero, Vector3.UnitZ)], [0, 0, 1]);
        foreach (var host in new[] { empty, thrown, invalid })
        {
            var result = GeometryExecutor.Execute(Root(), Compose(host));
            Assert.Null(result.Mesh);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("GX0004", diagnostic.Code);
            Assert.Equal(Origin, diagnostic.Origin);
        }
    }

    [Fact]
    public void Composition_rejects_changed_host_signature()
    {
        var changed = new OperationSignature("Geometry.BoxMesh", SemanticTypes.Scalar,
            SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
        Assert.False(ModuleComposer.TryCompose([BoxMeshModule.Descriptor],
            [new HostOperationBinding(changed, (Func<object?>)(() => null))], out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0005");
    }
}
