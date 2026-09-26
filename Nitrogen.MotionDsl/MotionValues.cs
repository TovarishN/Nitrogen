using System.Globalization;
using Nitrogen.Semantics;

namespace Nitrogen.MotionDsl;

/// <summary>
/// The value rules Motion.ngr's blocks call (issue 240). Constants fold in <c>float</c>, as
/// <c>SkillExpressionCompiler</c> folds them, so values and tolerances agree bit for bit. A null
/// operand is not constant, and neither is the result.
/// </summary>
public static partial class MotionValues
{
    public static float? Number(string text, string? unit)
    {
        float value = float.Parse(text, CultureInfo.InvariantCulture);
        if (unit == "deg") value *= MathF.PI / 180f;
        return value;
    }

    public static float? Negate(float? operand) => operand is float x ? -x : null;

    public static float? Not(float? operand) => operand is float x ? (x == 0f ? 1f : 0f) : null;

    /// <summary><c>+ - * /</c>; a division by zero does not fold (MV0001 reports it).</summary>
    public static float? Arithmetic(char op, float? a, float? b)
    {
        if (a is not float x || b is not float y) return null;
        return op switch
        {
            '+' => x + y,
            '-' => x - y,
            '*' => x * y,
            _ => y == 0f ? null : x / y,
        };
    }

    public static float? Compare(string op, float? a, float? b)
    {
        if (a is not float x || b is not float y) return null;
        bool result = op switch
        {
            "<" => x < y,
            ">" => x > y,
            "<=" => x <= y,
            ">=" => x >= y,
            "==" => x == y,
            _ => x != y,
        };
        return result ? 1f : 0f;
    }

    public static float? Logic(bool and, float? a, float? b)
    {
        if (a is not float x || b is not float y) return null;
        bool result = and ? x != 0f && y != 0f : x != 0f || y != 0f;
        return result ? 1f : 0f;
    }

    public static float? Ternary(float? condition, float? then, float? otherwise) =>
        condition is float c && then is float t && otherwise is float e ? (c != 0f ? t : e) : null;

    /// <summary>A function call folds when every argument does, with <c>EvaluateFunction</c>'s formulas; a clamp with inverted bounds does not.</summary>
    public static float? Call(string? kind, string name, IReadOnlyList<float?> arguments)
    {
        if (kind != "function" || !MotionTypes.ArityFits(kind, name, arguments.Count) || arguments.Any(a => a is null)) return null;
        float[] a = arguments.Select(v => v!.Value).ToArray();
        return name switch
        {
            "abs" => MathF.Abs(a[0]),
            "min" => MathF.Min(a[0], a[1]),
            "max" => MathF.Max(a[0], a[1]),
            "clamp" => a[1] <= a[2] ? Math.Clamp(a[0], a[1], a[2]) : null,
            "sin" => MathF.Sin(a[0]),
            "cos" => MathF.Cos(a[0]),
            "smoothstep" => a[0] * a[0] * (3f - 2f * a[0]),
            _ => null,
        };
    }

    public static bool Finite(float? value) => value is float v && float.IsFinite(v);

    public static bool NonNegative(float? value) => value is not float v || v >= 0f;

    /// <summary>A value check applies only to a float whose type checked: an error or another kind is reported by typing.</summary>
    public static bool Checkable(SkillType type) => type.Kind == SkillTypeKind.Float;

    /// <summary>Whether a node lies inside a node of <paramref name="kind"/>: division by zero is a skill rule.</summary>
    public static bool Inside(FileSemantics semantics, int node, int kind)
    {
        for (int p = semantics.Tree.Parent(node); p >= 0; p = semantics.Tree.Parent(p))
            if (semantics.Tree.Kind(p) == kind) return true;
        return false;
    }

    public static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
}
