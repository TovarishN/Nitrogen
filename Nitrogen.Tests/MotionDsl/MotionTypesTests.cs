using Nitrogen.MotionDsl;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The type rules Motion.ngr's blocks call (issue 239).</summary>
public class MotionTypesTests
{
    [Fact]
    public void Bare_numbers_adapt_and_only_time_and_angle_clash()
    {
        Assert.Equal(SkillType.Time, MotionTypes.Sum(SkillType.Float, SkillType.Time));
        Assert.True(MotionTypes.DimensionFits(SkillType.Float, SkillType.Time));
        Assert.False(MotionTypes.DimensionFits(SkillType.Angle, SkillType.Time));
        Assert.Equal(Dimension.Mixed, MotionTypes.Sum(SkillType.Angle, SkillType.Time).Dimension);
        Assert.Equal(Dimension.Mixed, MotionTypes.Product(SkillType.Time, SkillType.Float).Dimension);
        Assert.Equal(Dimension.Any, MotionTypes.Product(SkillType.Float, SkillType.Float).Dimension);
        Assert.True(MotionTypes.DimensionFits(new SkillType(SkillTypeKind.Float, Dimension.Mixed), SkillType.Angle));
    }

    [Fact]
    public void An_operator_that_fails_yields_error_and_an_error_fits_anything()
    {
        Assert.Equal(SkillType.Error, MotionTypes.Not(SkillType.Float));
        Assert.Equal(SkillType.Error, MotionTypes.Sum(SkillType.Bool, SkillType.Float));
        Assert.Equal(SkillType.Error, MotionTypes.Call("function", "abs", new[] { SkillType.Float, SkillType.Float }));
        Assert.Equal(SkillType.Float, MotionTypes.Call("reward", "alive", Array.Empty<SkillType>()));
        Assert.True(MotionTypes.KindFits(SkillType.Error, SkillType.Bool));
        Assert.True(MotionTypes.KindFits(SkillType.Float, null));
        Assert.False(MotionTypes.KindFits(SkillType.Float, SkillType.Bool));
    }

    [Fact]
    public void Builtins_signatures_and_names()
    {
        Assert.Equal(SkillType.Time, MotionTypes.Builtin("value", "time"));
        Assert.Equal(SkillType.Bool, MotionTypes.Builtin("value", "both_feet_supported"));
        Assert.Equal(SkillType.Float, MotionTypes.Builtin("value", "com.offset_z"));
        Assert.Equal(SkillType.Float, MotionTypes.Builtin("reward", "alive"));
        Assert.Equal(SkillTypeKind.Entity, MotionTypes.Builtin("subject", "torso").Kind);
        Assert.Equal(1, MotionTypes.Arity("function", "smoothstep"));
        Assert.Equal(3, MotionTypes.Arity("function", "clamp"));
        Assert.Equal(SkillType.Angle, MotionTypes.Channel("angle_offset"));
        Assert.Equal("float time", SkillType.Time.ToString());
        Assert.Equal("enum(a, b)", SkillType.EnumOf("x#1", "enum(a, b)").ToString());
        Assert.False(MotionTypes.SameEnum(SkillType.EnumOf("x#1", "e"), SkillType.EnumOf("x#2", "e")));
        Assert.True(MotionTypes.SameEnum(SkillType.EnumOf("x#1", "e"), SkillType.EnumOf("x#1", "e")));
    }
}
