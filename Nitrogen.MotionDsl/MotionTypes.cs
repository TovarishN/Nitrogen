namespace Nitrogen.MotionDsl;

public enum SkillTypeKind
{
    Float,
    Bool,
    Enum,
    Entity,
    Error,
}

/// <summary>
/// A float's unit dimension (issue 239).
/// <list type="bullet">
/// <item>A bare number is <see cref="Any"/> and adapts to its context.</item>
/// <item>Only <see cref="Time"/> and <see cref="Angle"/> clash.</item>
/// <item><see cref="Mixed"/> is a product with a dimensioned operand (<c>sin(time * 3)</c> is fine),
/// and is never checked.</item>
/// </list>
/// </summary>
public enum Dimension
{
    Any,
    Time,
    Angle,
    Mixed,
}

/// <summary>
/// A Motion value type (issue 239). Enums are nominal: <see cref="Enum"/> identifies the declaring
/// <c>enum(…)</c>. An entity (a part, axis or subject) is valid only as a reward-call argument.
/// </summary>
public sealed record SkillType(SkillTypeKind Kind, Dimension Dimension = Dimension.Any, string? Enum = null, string? Display = null)
{
    public static readonly SkillType Error = new(SkillTypeKind.Error);
    public static readonly SkillType Bool = new(SkillTypeKind.Bool);
    public static readonly SkillType Float = new(SkillTypeKind.Float);
    public static readonly SkillType Time = new(SkillTypeKind.Float, Dimension.Time);
    public static readonly SkillType Angle = new(SkillTypeKind.Float, Dimension.Angle);

    public static SkillType EnumOf(string declaration, string display) => new(SkillTypeKind.Enum, Enum: declaration, Display: display);

    public static SkillType EntityOf(string kind) => new(SkillTypeKind.Entity, Display: kind);

    public bool IsError => Kind == SkillTypeKind.Error;

    public override string ToString() => Kind switch
    {
        SkillTypeKind.Float => Dimension switch
        {
            Dimension.Time => "float time",
            Dimension.Angle => "float angle",
            _ => "float",
        },
        SkillTypeKind.Bool => "bool",
        SkillTypeKind.Enum => Display ?? "enum",
        SkillTypeKind.Entity => Display ?? "entity",
        _ => "error",
    };
}

/// <summary>
/// The rules Motion.ngr's semantics blocks call (issue 239). Checks treat <see cref="SkillType.Error"/>
/// as fitting anything, and an operator whose own check fails yields Error, so one mistake gives one
/// diagnostic.
/// </summary>
public static class MotionTypes
{
    /// <summary>A symbol's type when its declaration sets none: built-in values, rewards (numbers: <c>alive * 1.0</c>), entities.</summary>
    public static SkillType Builtin(string kind, string name) => kind switch
    {
        "value" => name switch
        {
            "time" => SkillType.Time,
            "both_feet_supported" => SkillType.Bool,
            _ => SkillType.Float,
        },
        "reward" => SkillType.Float,
        _ => SkillType.EntityOf(kind),
    };

    public static SkillType Number(string? unit) => unit switch
    {
        "deg" => SkillType.Angle,
        "s" => SkillType.Time,
        _ => SkillType.Float,
    };

    public static SkillType Channel(string channel) => channel is "target_angle" or "angle_offset" ? SkillType.Angle : SkillType.Float;

    public static string Requires(string what, SkillType? expected, SkillType actual) => $"{what} requires {expected}, got {actual}";

    // ---- checks ----

    public static bool KindFits(SkillType actual, SkillType? expected) =>
        expected is null || actual.IsError || expected.IsError || actual.Kind == expected.Kind;

    public static bool DimensionFits(SkillType actual, SkillType? expected) =>
        expected is null || actual.IsError || expected.IsError || !Clash(actual.Dimension, expected.Dimension);

    public static bool SameEnum(SkillType actual, SkillType? expected) =>
        expected is null || actual.Kind != SkillTypeKind.Enum || expected.Kind != SkillTypeKind.Enum || actual.Enum == expected.Enum;

    public static bool IsFloat(SkillType type) => type.IsError || type.Kind == SkillTypeKind.Float;

    public static bool IsBool(SkillType type) => type.IsError || type.Kind == SkillTypeKind.Bool;

    public static bool AllFloat(IEnumerable<SkillType> types) => types.All(IsFloat);

    // ---- operators ----

    public static SkillType Negate(SkillType operand) => operand.Kind == SkillTypeKind.Float ? operand : SkillType.Error;

    public static SkillType Not(SkillType operand) => operand.Kind == SkillTypeKind.Bool ? SkillType.Bool : SkillType.Error;

    public static SkillType Logic(SkillType a, SkillType b) =>
        a.Kind == SkillTypeKind.Bool && b.Kind == SkillTypeKind.Bool ? SkillType.Bool : SkillType.Error;

    public static SkillType Compare(SkillType a, SkillType b) =>
        a.Kind == SkillTypeKind.Float && b.Kind == SkillTypeKind.Float ? SkillType.Bool : SkillType.Error;

    public static SkillType Equality(SkillType a, SkillType b) =>
        !a.IsError && !b.IsError && a.Kind == b.Kind ? SkillType.Bool : SkillType.Error;

    /// <summary><c>+</c> and <c>-</c>: the operands' agreed dimension (<see cref="Dimension.Mixed"/> when they clash, which MT0002 reports).</summary>
    public static SkillType Sum(SkillType a, SkillType b) =>
        a.Kind == SkillTypeKind.Float && b.Kind == SkillTypeKind.Float
            ? new SkillType(SkillTypeKind.Float, Unify(a.Dimension, b.Dimension))
            : SkillType.Error;

    /// <summary><c>*</c> and <c>/</c>: dimensionless when both operands are, otherwise <see cref="Dimension.Mixed"/>.</summary>
    public static SkillType Product(SkillType a, SkillType b) =>
        a.Kind == SkillTypeKind.Float && b.Kind == SkillTypeKind.Float
            ? new SkillType(SkillTypeKind.Float, a.Dimension == Dimension.Any && b.Dimension == Dimension.Any ? Dimension.Any : Dimension.Mixed)
            : SkillType.Error;

    public static SkillType Ternary(SkillType condition, SkillType then, SkillType otherwise)
    {
        if (condition.Kind != SkillTypeKind.Bool || then.IsError || otherwise.IsError || then.Kind != otherwise.Kind) return SkillType.Error;
        return then.Kind == SkillTypeKind.Float ? new SkillType(SkillTypeKind.Float, Unify(then.Dimension, otherwise.Dimension)) : then;
    }

    /// <summary><c>torso.height</c> in a behavior: a property of an entity is a number.</summary>
    public static SkillType Member(SkillType target) => target.Kind == SkillTypeKind.Entity ? SkillType.Float : SkillType.Error;

    // ---- functions (the skill compiler's signatures) ----

    public static int? Arity(string? kind, string name) => kind != "function"
        ? null
        : name switch
        {
            "abs" or "sin" or "cos" or "smoothstep" => 1,
            "min" or "max" => 2,
            "clamp" => 3,
            _ => null,
        };

    public static bool ArityFits(string? kind, string name, int count) => Arity(kind, name) is not int arity || arity == count;

    public static bool ArgumentsFit(string? kind, IReadOnlyList<SkillType> arguments) => kind != "function" || AllFloat(arguments);

    public static bool ArgumentDimensionsFit(string? kind, string name, IReadOnlyList<SkillType> arguments)
    {
        if (kind != "function" || name == "smoothstep") return true;
        if (name is "sin" or "cos") return arguments.All(a => a.IsError || !Clash(a.Dimension, Dimension.Angle));
        var dimension = Dimension.Any;
        foreach (var argument in arguments)
        {
            if (argument.IsError) continue;
            if (Clash(dimension, argument.Dimension)) return false;
            dimension = Unify(dimension, argument.Dimension);
        }
        return true;
    }

    /// <summary>A reward call is a number; a function call is float of its arguments' dimension (sin, cos, smoothstep: none); Error when it does not fit.</summary>
    public static SkillType Call(string? kind, string name, IReadOnlyList<SkillType> arguments)
    {
        if (kind == "reward") return SkillType.Float;
        if (kind != "function" || !ArityFits(kind, name, arguments.Count) || arguments.Any(a => a.Kind != SkillTypeKind.Float)) return SkillType.Error;
        if (name is "sin" or "cos" or "smoothstep") return SkillType.Float;
        var dimension = Dimension.Any;
        foreach (var argument in arguments) dimension = Unify(dimension, argument.Dimension);
        return new SkillType(SkillTypeKind.Float, dimension);
    }

    static bool Clash(Dimension a, Dimension b) => (a == Dimension.Time && b == Dimension.Angle) || (a == Dimension.Angle && b == Dimension.Time);

    static Dimension Unify(Dimension a, Dimension b) =>
        a == b ? a : a == Dimension.Any ? b : b == Dimension.Any ? a : Dimension.Mixed;
}
