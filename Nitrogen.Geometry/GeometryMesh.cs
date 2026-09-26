using System.Numerics;

namespace Nitrogen.Geometry;

public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal);

public sealed class GeometryMesh
{
    public GeometryMesh(IEnumerable<MeshVertex> vertices, IEnumerable<ushort> indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        var copiedVertices = vertices.ToArray();
        var copiedIndices = indices.ToArray();
        if (copiedVertices.Length == 0 || copiedIndices.Length == 0 || copiedIndices.Length % 3 != 0)
            throw new ArgumentException("A mesh needs vertices and triangle indices.");
        if (copiedVertices.Any(vertex => !Finite(vertex.Position) || !Finite(vertex.Normal)))
            throw new ArgumentException("Mesh vertex coordinates and normals must be finite.", nameof(vertices));
        if (copiedIndices.Any(index => index >= copiedVertices.Length))
            throw new ArgumentException("A triangle index is outside the vertex list.", nameof(indices));
        Vertices = Array.AsReadOnly(copiedVertices);
        Indices = Array.AsReadOnly(copiedIndices);
    }

    public IReadOnlyList<MeshVertex> Vertices { get; }
    public IReadOnlyList<ushort> Indices { get; }

    static bool Finite(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
