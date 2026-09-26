using Nitrogen.Geometry.Syntax;
using Nitrogen.Semantic;

namespace Nitrogen.Geometry;

public static class GeometryHirLowerer
{
    public static LoweringRegistration Registration { get; } =
        new(GeometryKinds.Box, BoxMeshModule.BoxSignature.Id, Lower);

    static HirNode? Lower(LoweringContext context, int boxNode)
    {
        var box = new BoxNodeSemantics(context.File, boxNode);
        var parts = new[] { box.Width, box.Height, box.Depth };
        if (parts.Any(part => !GeometryValues.Positive(part.Amount))) return null;
        var tree = context.File.Tree;
        var constants = parts.Select(part =>
        {
            var node = Enumerable.Range(0, tree.NodeCount).First(candidate =>
                tree.Kind(candidate) == GeometryKinds.Num && tree.Span(candidate) == part.Span);
            return (HirNode)new HirConstant(part.Amount!.Value, SemanticTypes.Scalar, context.Origin(node));
        }).ToArray();
        return new HirOperation(BoxMeshModule.BoxSignature, constants, [context.Origin(boxNode)]);
    }
}
