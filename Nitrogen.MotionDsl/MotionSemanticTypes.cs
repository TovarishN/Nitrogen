using Nitrogen.Semantic;

namespace Nitrogen.MotionDsl;

public static class MotionSemanticTypes
{
    public static SemanticType Map(SkillType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Kind switch
        {
            SkillTypeKind.Float => type.Dimension switch
            {
                Dimension.Any => SemanticTypes.Scalar,
                Dimension.Time => SemanticType.Named("Units", "Time"),
                Dimension.Angle => SemanticTypes.Angle,
                Dimension.Mixed => throw new NotSupportedException("Mixed dimensions need a general dimensional algebra."),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            },
            SkillTypeKind.Bool => SemanticTypes.Bool,
            SkillTypeKind.Error => SemanticTypes.Error,
            SkillTypeKind.Enum => SemanticType.Named("Motion.Enum", type.Enum ??
                throw new ArgumentException("An enum requires a declaration identity.", nameof(type))),
            SkillTypeKind.Entity => SemanticType.Named("Motion.Entity", type.Display ??
                throw new ArgumentException("An entity requires a domain identity.", nameof(type))),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
