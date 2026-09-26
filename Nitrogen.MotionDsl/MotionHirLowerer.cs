using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;

namespace Nitrogen.MotionDsl;

public static class MotionHirLowerer
{
    public static OperationSignature Signature { get; } = new("Motion.AngleSlot", SemanticTypes.Angle, SemanticTypes.Angle);
    public static LoweringRegistration Registration { get; } = new(MotionKinds.Track, Signature.Id, Lower);

    static HirNode? Lower(LoweringContext context, int node)
    {
        var track = new TrackNodeSemantics(context.File, node);
        if (MotionSemanticTypes.Map(track.Target.ChannelType) != SemanticTypes.Angle) return null;
        var value = track.To;
        var type = MotionSemanticTypes.Map(value.Type);
        if (type != SemanticTypes.Angle && type != SemanticTypes.Scalar) return null;
        var tree = context.File.Tree;
        var valueNode = Enumerable.Range(0, tree.NodeCount)
            .First(candidate => tree.Span(candidate) == value.Span &&
                context.File.Defines(candidate, MotionModule.P_Expr_Type));
        HirNode? expression;
        if (value.Constant is float radians)
        {
            if (!float.IsFinite(radians))
                return new HirConstant(0f, SemanticTypes.Error, context.Origin(valueNode));
            expression = new HirConstant(radians, SemanticTypes.Angle, context.Origin(valueNode));
        }
        else
        {
            expression = MotionExpressionLowerer.Lower(context, valueNode);
            if (expression is null) return null;
            if (expression.Type == SemanticTypes.Error) return expression;
            if (expression.Type == SemanticTypes.Scalar)
                expression = new HirOperation(MotionExpressionLowerer.ContextualAngle,
                    [expression], [context.Origin(valueNode)]);
        }
        return new HirOperation(Signature, [expression], [context.Origin(node)]);
    }
}
