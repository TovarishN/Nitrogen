using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;

namespace Nitrogen.MotionDsl;

/// <summary>The supported dynamic Motion expression subset for angle-track inspection.</summary>
public static class MotionExpressionLowerer
{
    public static OperationSignature AddScalars { get; } =
        new("Motion.AddScalars", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    public static OperationSignature AddScalarAngle { get; } =
        new("Motion.AddScalarAngle", SemanticTypes.Angle, SemanticTypes.Scalar, SemanticTypes.Angle);
    public static OperationSignature AddAngleScalar { get; } =
        new("Motion.AddAngleScalar", SemanticTypes.Angle, SemanticTypes.Angle, SemanticTypes.Scalar);
    public static OperationSignature AddAngles { get; } =
        new("Motion.AddAngles", SemanticTypes.Angle, SemanticTypes.Angle, SemanticTypes.Angle);
    public static OperationSignature GroupScalar { get; } =
        new("Motion.GroupScalar", SemanticTypes.Scalar, SemanticTypes.Scalar);
    public static OperationSignature GroupAngle { get; } =
        new("Motion.GroupAngle", SemanticTypes.Angle, SemanticTypes.Angle);
    public static OperationSignature ContextualAngle { get; } =
        new("Motion.ContextualAngle", SemanticTypes.Angle, SemanticTypes.Scalar);

    public static HirNode? Lower(LoweringContext context, int node)
    {
        var file = context.File;
        var tree = file.Tree;
        var kind = tree.Kind(node);
        if (kind == MotionKinds.Num)
        {
            var value = file.Get(node, MotionModule.P_Expr_Constant);
            var type = MotionSemanticTypes.Map(file.Get(node, MotionModule.P_Expr_Type));
            return value is float finite && float.IsFinite(finite) &&
                (type == SemanticTypes.Scalar || type == SemanticTypes.Angle)
                ? new HirConstant(finite, type, context.Origin(node))
                : new HirConstant(0f, SemanticTypes.Error, context.Origin(node));
        }
        if (kind == MotionKinds.Ref)
        {
            var binding = file.SymbolOf(node);
            var type = MotionSemanticTypes.Map(file.Get(node, MotionModule.P_Expr_Type));
            return binding is not null && (type == SemanticTypes.Scalar || type == SemanticTypes.Angle)
                ? new HirSymbolRef(SemanticSymbol.From(binding, "Motion", type), context.Origin(node)) : null;
        }
        if (kind == MotionKinds.Paren)
        {
            var inner = Lower(context, tree.Child(node, 1));
            if (inner is null) return null;
            if (inner.Type == SemanticTypes.Error) return inner;
            var signature = inner.Type == SemanticTypes.Scalar ? GroupScalar : GroupAngle;
            return new HirOperation(signature, [inner], [context.Origin(node)]);
        }
        if (kind == MotionKinds.Add)
        {
            var left = Lower(context, tree.Child(node, 0));
            var right = Lower(context, tree.Child(node, 2));
            if (left is null || right is null) return null;
            if (left.Type == SemanticTypes.Error) return left;
            if (right.Type == SemanticTypes.Error) return right;
            var signature = (left.Type, right.Type) switch
            {
                var (a, b) when a == SemanticTypes.Scalar && b == SemanticTypes.Scalar => AddScalars,
                var (a, b) when a == SemanticTypes.Scalar && b == SemanticTypes.Angle => AddScalarAngle,
                var (a, b) when a == SemanticTypes.Angle && b == SemanticTypes.Scalar => AddAngleScalar,
                var (a, b) when a == SemanticTypes.Angle && b == SemanticTypes.Angle => AddAngles,
                _ => null,
            };
            return signature is null ? null : new HirOperation(signature, [left, right], [context.Origin(node)]);
        }
        return null;
    }
}
