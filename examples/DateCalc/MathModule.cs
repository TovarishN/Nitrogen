using Nitrogen.Semantic;

namespace DateCalc.Syntax;

/// <summary>A function a call can select: its name in source, its typed operation, what it computes, and its parameters' names.</summary>
public sealed record Function(string Name, OperationSignature Signature, Func<object[], object> Run, IReadOnlyList<string> Parameters);

/// <summary>
/// The Math module: the usual number functions and constants. Each function is one line: the name a
/// call uses, the operation it lowers to, and its implementation. Calls pick an overload by name and
/// argument types, so <c>round(x)</c> and <c>round(x, 2)</c> are different operations.
/// </summary>
public static class MathModule
{
    static readonly SemanticType Number = SemanticTypes.Scalar;

    public static readonly Function[] Functions =
    [
        Unary("abs", MathF.Abs),
        Unary("sign", x => MathF.Sign(x)),
        Unary("sqrt", MathF.Sqrt),
        Unary("cbrt", MathF.Cbrt),
        Unary("exp", MathF.Exp),
        Unary("ln", MathF.Log),
        Unary("log10", MathF.Log10),
        Unary("log2", MathF.Log2),
        Unary("sin", MathF.Sin),
        Unary("cos", MathF.Cos),
        Unary("tan", MathF.Tan),
        Unary("asin", MathF.Asin),
        Unary("acos", MathF.Acos),
        Unary("atan", MathF.Atan),
        Unary("sinh", MathF.Sinh),
        Unary("cosh", MathF.Cosh),
        Unary("tanh", MathF.Tanh),
        Unary("floor", MathF.Floor),
        Unary("ceil", MathF.Ceiling),
        Unary("trunc", MathF.Truncate),
        Unary("round", x => MathF.Round(x, MidpointRounding.AwayFromZero)),
        Binary("round", "RoundTo", "x", "digits", (x, digits) => MathF.Round(x, (int)digits, MidpointRounding.AwayFromZero)),
        Binary("pow", "Pow", "base", "exponent", MathF.Pow),
        Binary("log", "LogBase", "x", "base", MathF.Log),
        Binary("atan2", "Atan2", "y", "x", MathF.Atan2),
        Binary("min", "Min", "a", "b", MathF.Min),
        Binary("max", "Max", "a", "b", MathF.Max),
        Binary("mod", "Mod", "x", "y", (x, y) => x - y * MathF.Floor(x / y)),
    ];

    public static readonly (string Name, float Value)[] Constants = [("pi", MathF.PI), ("e", MathF.E), ("tau", MathF.Tau)];

    public static readonly SemanticModule Semantics = new("Math", ["Core"], [], Functions.Select(f => f.Signature));

    static Function Unary(string name, Func<float, float> run) =>
        new(name, new("Math." + char.ToUpperInvariant(name[0]) + name[1..], Number, Number), a => run((float)a[0]), ["x"]);

    static Function Binary(string name, string id, string first, string second, Func<float, float, float> run) =>
        new(name, new("Math." + id, Number, Number, Number), a => run((float)a[0], (float)a[1]), [first, second]);
}
