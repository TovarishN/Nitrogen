using Nitrogen.Semantic;

namespace Nitrogen.MotionDsl;

public static class UnitsSemanticModule
{
    public static SemanticModule Instance { get; } = new("Units", [],
        [SemanticTypes.Angle, SemanticType.Named("Units", "Time")], []);

    public static SemanticModule Motion { get; } = new("Motion", ["Units"], [],
        [MotionHirLowerer.Signature, MotionExpressionLowerer.AddScalars, MotionExpressionLowerer.AddScalarAngle,
         MotionExpressionLowerer.AddAngleScalar, MotionExpressionLowerer.AddAngles,
         MotionExpressionLowerer.GroupScalar, MotionExpressionLowerer.GroupAngle,
         MotionExpressionLowerer.ContextualAngle], [MotionHirLowerer.Registration]);

    public static SemanticModule Policy { get; } = new("Policy", ["Units"], [],
        [PolicyHirLowerer.Signature], [PolicyHirLowerer.Registration]);
}
