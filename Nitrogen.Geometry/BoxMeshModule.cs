using Nitrogen.Geometry.Syntax;
using Nitrogen.Semantic;

namespace Nitrogen.Geometry;

public static class BoxMeshModule
{
    public static readonly SemanticType MeshType = SemanticType.Named("Geometry", "Mesh");
    public static readonly OperationSignature BoxSignature = new("Geometry.BoxMesh", MeshType,
        SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    public static readonly ModuleDescriptor Descriptor = new("Geometry", GeometryModule.Instance,
        new SemanticModule("Geometry", [], [MeshType], [BoxSignature]),
        ["Document"], [BoxSignature]);
}
