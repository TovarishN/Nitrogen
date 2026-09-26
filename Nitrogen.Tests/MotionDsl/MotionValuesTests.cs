using Nitrogen.MotionDsl;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Constant folding with the skill compiler's float arithmetic (issue 240).</summary>
public class MotionValuesTests
{
    [Fact]
    public void Numbers_fold_as_the_compiler_parses_them()
    {
        Assert.Equal(0.5f, MotionValues.Number("0.5", null));
        Assert.Equal(90f * (MathF.PI / 180f), MotionValues.Number("90", "deg"));
        Assert.Equal(2f, MotionValues.Number("2", "s"));
    }

    [Fact]
    public void Operators_fold_and_a_non_constant_operand_does_not()
    {
        Assert.Equal(5f, MotionValues.Arithmetic('+', 2f, 3f));
        Assert.Equal(-1f, MotionValues.Arithmetic('-', 2f, 3f));
        Assert.Equal(1.5f, MotionValues.Arithmetic('/', 3f, 2f));
        Assert.Null(MotionValues.Arithmetic('/', 3f, 0f));
        Assert.Null(MotionValues.Arithmetic('*', 3f, null));
        Assert.Equal(-2f, MotionValues.Negate(2f));
        Assert.Equal(1f, MotionValues.Not(0f));
        Assert.Equal(1f, MotionValues.Compare("<", 1f, 2f));
        Assert.Equal(0f, MotionValues.Compare("==", 1f, 2f));
        Assert.Equal(1f, MotionValues.Logic(and: false, 0f, 1f));
        Assert.Equal(3f, MotionValues.Ternary(1f, 3f, 4f));
        Assert.Null(MotionValues.Ternary(null, 3f, 4f));
    }

    [Fact]
    public void Functions_fold_with_the_compilers_formulas()
    {
        Assert.Equal(2f, MotionValues.Call("function", "abs", [-2f]));
        Assert.Equal(1f, MotionValues.Call("function", "clamp", [5f, 0f, 1f]));
        Assert.Equal(0.5f * 0.5f * (3f - 2f * 0.5f), MotionValues.Call("function", "smoothstep", [0.5f]));
        Assert.Null(MotionValues.Call("function", "abs", [1f, 2f]));
        Assert.Null(MotionValues.Call("function", "clamp", [5f, 1f, 0f]));
        Assert.Null(MotionValues.Call("reward", "alive", []));
    }

    [Fact]
    public void Predicates()
    {
        Assert.True(MotionValues.Finite(1f));
        Assert.False(MotionValues.Finite(null));
        Assert.False(MotionValues.Finite(float.PositiveInfinity));
        Assert.True(MotionValues.NonNegative(null));
        Assert.False(MotionValues.NonNegative(-0.1f));
        Assert.True(MotionValues.Checkable(SkillType.Time));
        Assert.False(MotionValues.Checkable(SkillType.Error));
        Assert.False(MotionValues.Checkable(SkillType.Bool));
    }
}
