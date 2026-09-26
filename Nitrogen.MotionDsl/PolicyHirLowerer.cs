using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.Semantic;

namespace Nitrogen.MotionDsl;

public static class PolicyHirLowerer
{
    public static OperationSignature Signature { get; } = new("Policy.Clamp", SemanticTypes.Angle, SemanticTypes.Angle);
    public static LoweringRegistration Registration { get; } = new(PolicyKinds.Clamp, Signature.Id, Lower);

    static HirNode? Lower(LoweringContext context, int node)
    {
        var clamp = new ClampNodeSemantics(context.File, node);
        var value = clamp.Radians;
        if (clamp.AngleRadians is not float radians) return null;
        var tree = context.File.Tree;
        var valueNode = Enumerable.Range(0, tree.NodeCount)
            .First(candidate => tree.Kind(candidate) == PolicyKinds.Num && tree.Span(candidate) == value.Span);
        if (!float.IsFinite(radians) || radians <= 0f)
            return new HirConstant(0f, SemanticTypes.Error, context.Origin(valueNode));
        var constant = new HirConstant(radians, SemanticTypes.Angle, context.Origin(valueNode));
        return new HirOperation(Signature, [constant], [context.Origin(node)]);
    }
}
