using System.Globalization;
using Nitrogen.Semantic;

namespace DateCalc.Syntax;

/// <summary>
/// DateCalc's semantic module: its types and operations, and which operation an operator picks for
/// its operand types. The grammar names these; the language server finds <see cref="Semantics"/>.
/// </summary>
public static class DateCalcLanguage
{
    public static readonly SemanticType Number = SemanticTypes.Scalar;
    public static readonly SemanticType Text = SemanticTypes.Text;
    public static readonly SemanticType Date = SemanticType.Named("DateCalc", "Date");
    public static readonly SemanticType Duration = SemanticType.Named("DateCalc", "Duration");

    public static readonly OperationSignature ParseDate = new("DateCalc.Date", Date, Text);
    public static readonly OperationSignature Days = new("DateCalc.Days", Duration, Number);
    public static readonly OperationSignature Weeks = new("DateCalc.Weeks", Duration, Number);
    public static readonly OperationSignature InDays = new("DateCalc.InDays", Number, Duration);

    /// <summary>DateCalc's own functions, beside <see cref="MathModule.Functions"/>: overloads for dates and durations.</summary>
    public static readonly Function[] Functions =
    [
        new("weekday", new("DateCalc.Weekday", Text, Date), a => ((DateOnly)a[0]).DayOfWeek.ToString()),
        new("abs", new("DateCalc.AbsDuration", Duration, Duration), a => ((TimeSpan)a[0]).Duration()),
        new("min", new("DateCalc.EarlierDate", Date, Date, Date), a => (DateOnly)a[0] < (DateOnly)a[1] ? a[0] : a[1]),
        new("max", new("DateCalc.LaterDate", Date, Date, Date), a => (DateOnly)a[0] > (DateOnly)a[1] ? a[0] : a[1]),
        new("min", new("DateCalc.ShorterDuration", Duration, Duration, Duration), a => (TimeSpan)a[0] < (TimeSpan)a[1] ? a[0] : a[1]),
        new("max", new("DateCalc.LongerDuration", Duration, Duration, Duration), a => (TimeSpan)a[0] > (TimeSpan)a[1] ? a[0] : a[1]),
    ];

    /// <summary>Every function a call can name, from both modules.</summary>
    public static IEnumerable<Function> AllFunctions => MathModule.Functions.Concat(Functions);

    /// <summary>Every operator overload: the operator, then its signature.</summary>
    public static readonly (string Operator, OperationSignature Signature)[] Operators =
    [
        ("+", new("DateCalc.Add", Number, Number, Number)),
        ("-", new("DateCalc.Subtract", Number, Number, Number)),
        ("*", new("DateCalc.Multiply", Number, Number, Number)),
        ("/", new("DateCalc.Divide", Number, Number, Number)),
        ("+", new("DateCalc.Later", Date, Date, Duration)),
        ("-", new("DateCalc.Earlier", Date, Date, Duration)),
        ("-", new("DateCalc.Between", Duration, Date, Date)),
        ("+", new("DateCalc.AddDurations", Duration, Duration, Duration)),
        ("-", new("DateCalc.SubtractDurations", Duration, Duration, Duration)),
        ("*", new("DateCalc.Scale", Duration, Duration, Number)),
        ("*", new("DateCalc.Times", Duration, Number, Duration)),
        ("/", new("DateCalc.Ratio", Number, Duration, Duration)),
    ];

    public static readonly SemanticModule Semantics = new("DateCalc", ["Core"], [Date, Duration],
        [ParseDate, Days, Weeks, InDays, .. Functions.Select(f => f.Signature), .. Operators.Select(o => o.Signature)]);

    /// <summary>The overload of <paramref name="op"/> for these operand types; null when there is none.</summary>
    public static OperationSignature? Select(string op, SemanticType? left, SemanticType? right) =>
        Operators.FirstOrDefault(o => o.Operator == op && o.Signature.Inputs[0].Equals(left) && o.Signature.Inputs[1].Equals(right)).Signature;

    /// <summary>The overload of the function <paramref name="name"/> for these argument types; null when there is none.</summary>
    public static OperationSignature? Call(string name, params SemanticType?[] arguments) =>
        AllFunctions.FirstOrDefault(f => f.Name == name && f.Signature.Inputs.SequenceEqual(arguments.Select(a => a!))
            && arguments.All(a => a is not null))?.Signature;

    /// <summary>An overload was found, or an operand's type is unknown (its own error is reported there).</summary>
    public static bool Fits(OperationSignature? selected, params SemanticType?[] operands) =>
        selected is not null || operands.Any(o => o is null);

    public static string NoCall(string name, params SemanticType?[] arguments) =>
        $"'{name}' does not take ({string.Join(", ", arguments.Select(a => a?.ToString()))})";

    /// <summary>The type of a built-in value: the Math constants are numbers.</summary>
    public static SemanticType? Builtin(string name) =>
        MathModule.Constants.Any(c => c.Name == name) ? Number : null;

    public static string Mismatch(string op, SemanticType? left, SemanticType? right) =>
        $"'{op}' does not apply to {left} and {right}";

    public static bool IsDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
