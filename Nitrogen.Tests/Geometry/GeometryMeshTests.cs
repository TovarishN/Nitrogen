using System.Numerics;
using Nitrogen.Geometry;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryMeshTests
{
    static MeshVertex[] Triangle() =>
    [
        new(Vector3.Zero, Vector3.UnitZ),
        new(Vector3.UnitX, Vector3.UnitZ),
        new(Vector3.UnitY, Vector3.UnitZ)
    ];

    [Fact]
    public void Mesh_copies_inputs_and_exposes_read_only_lists()
    {
        var vertices = Triangle();
        ushort[] indices = [0, 1, 2];
        var mesh = new GeometryMesh(vertices, indices);
        vertices[0] = new MeshVertex(Vector3.One, Vector3.One);
        indices[0] = 2;
        Assert.Equal(Vector3.Zero, mesh.Vertices[0].Position);
        Assert.Equal((ushort)0, mesh.Indices[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<MeshVertex>)mesh.Vertices)[0] = vertices[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<ushort>)mesh.Indices)[0] = 2);
    }

    [Fact]
    public void Mesh_rejects_invalid_topology_and_nonfinite_vectors()
    {
        Assert.Throws<ArgumentNullException>(() => new GeometryMesh(null!, [0, 1, 2]));
        Assert.Throws<ArgumentNullException>(() => new GeometryMesh(Triangle(), null!));
        Assert.Throws<ArgumentException>(() => new GeometryMesh([], [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => new GeometryMesh(Triangle(), []));
        Assert.Throws<ArgumentException>(() => new GeometryMesh(Triangle(), [0, 1]));
        Assert.Throws<ArgumentException>(() => new GeometryMesh(Triangle(), [0, 1, 3]));
        var vertices = Triangle();
        vertices[0] = new MeshVertex(new Vector3(float.NaN, 0, 0), Vector3.UnitZ);
        Assert.Throws<ArgumentException>(() => new GeometryMesh(vertices, [0, 1, 2]));
        vertices[0] = new MeshVertex(Vector3.Zero, new Vector3(0, float.PositiveInfinity, 0));
        Assert.Throws<ArgumentException>(() => new GeometryMesh(vertices, [0, 1, 2]));
    }

    [Fact]
    public void Repeated_indices_and_zero_normals_are_valid_values()
    {
        var vertices = Triangle();
        vertices[0] = vertices[0] with { Normal = Vector3.Zero };
        var mesh = new GeometryMesh(vertices, [0, 0, 0]);
        Assert.Equal(Vector3.Zero, mesh.Vertices[0].Normal);
    }
}
