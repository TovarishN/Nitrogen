using System.Globalization;
using Nitrogen;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace DateCalc.Syntax;

/// <summary>One shown value, or one diagnostic, at a source line (1-based).</summary>
public sealed record DateCalcLine(int Line, string Text, bool IsError = false);

/// <summary>Runs DateCalc source: parse, bind, check, lower to typed HIR, then project each root through the handlers.</summary>
public static class DateCalcEvaluator
{
    public static readonly Language Language = new LanguageBuilder().Add(DateCalcModule.Instance)
        .AddSemantic(MathModule.Semantics).AddSemantic(DateCalcLanguage.Semantics).Build();

    static readonly IReadOnlySet<int> Statements = new HashSet<int> { DateCalcKinds.Let, DateCalcKinds.Show };

    static readonly ProjectionRegistry Handlers = new(Language.SemanticCatalog,
    [
        Handler(DateCalcLanguage.ParseDate, a => DateOnly.ParseExact((string)a[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)),
        Handler(DateCalcLanguage.Days, a => TimeSpan.FromDays((float)a[0])),
        Handler(DateCalcLanguage.Weeks, a => TimeSpan.FromDays(7 * (float)a[0])),
        Handler(DateCalcLanguage.InDays, a => (float)((TimeSpan)a[0]).TotalDays),
        Handler(Op("DateCalc.Add"), a => (float)a[0] + (float)a[1]),
        Handler(Op("DateCalc.Subtract"), a => (float)a[0] - (float)a[1]),
        Handler(Op("DateCalc.Multiply"), a => (float)a[0] * (float)a[1]),
        Handler(Op("DateCalc.Divide"), a => (float)a[0] / (float)a[1]),
        Handler(Op("DateCalc.Later"), a => ((DateOnly)a[0]).AddDays((int)((TimeSpan)a[1]).TotalDays)),
        Handler(Op("DateCalc.Earlier"), a => ((DateOnly)a[0]).AddDays(-(int)((TimeSpan)a[1]).TotalDays)),
        Handler(Op("DateCalc.Between"), a => TimeSpan.FromDays(((DateOnly)a[0]).DayNumber - ((DateOnly)a[1]).DayNumber)),
        Handler(Op("DateCalc.AddDurations"), a => (TimeSpan)a[0] + (TimeSpan)a[1]),
        Handler(Op("DateCalc.SubtractDurations"), a => (TimeSpan)a[0] - (TimeSpan)a[1]),
        Handler(Op("DateCalc.Scale"), a => (TimeSpan)a[0] * (float)a[1]),
        Handler(Op("DateCalc.Times"), a => (float)a[0] * (TimeSpan)a[1]),
        Handler(Op("DateCalc.Ratio"), a => (float)((TimeSpan)a[0] / (TimeSpan)a[1])),
        .. DateCalcLanguage.AllFunctions.Select(f => Handler(f.Signature, f.Run)),
    ]);

    /// <summary>The value of each statement, in order, or the diagnostics that stop it from running.</summary>
    public static IReadOnlyList<DateCalcLine> Run(string source)
    {
        using var parsed = Language.Parse(source, DateCalcModule.Program);
        var project = new Project(Language);
        project.Set("input.datecalc", parsed.Tree);
        var file = new ProjectSemantics(project)["input.datecalc"];
        var errors = new List<DateCalcLine>();
        foreach (var diagnostic in parsed.Diagnostics) errors.Add(Error(source, diagnostic.Span, parsed.FormatMessage(diagnostic)));
        errors.AddRange(project.Diagnostics("input.datecalc").Select(d => Error(source, d.Span, d.Message)));
        errors.AddRange(file.Diagnostics().Select(d => Error(source, d.Span, $"{d.Code}: {d.Message}")));
        if (errors.Count > 0) return errors;

        var lowered = HirLowering.LowerSelected(file, Statements, Guid.NewGuid());
        if (lowered.Diagnostics.Count > 0)
            return lowered.Diagnostics.Select(d => Error(source, d.Origin.Span, $"{d.Code}: {d.Message}")).ToList();
        return lowered.Roots.Select(root =>
        {
            var result = HirProjector.Project(root, Handlers, Constants(root));
            return result.Value is { } value
                ? new DateCalcLine(LineOf(source, root.Origins[0].Span.Start), Show(value.Value))
                : Error(source, root.Origins[0].Span, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        }).ToList();
    }

    /// <summary>The values of the built-in constants the root refers to.</summary>
    static Dictionary<Symbol, ProjectedValue> Constants(HirNode root) => HirTraversal.PreOrder(root).OfType<HirSymbolRef>()
        .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
        .ToDictionary(s => s, s => new ProjectedValue(DateCalcLanguage.Number, MathModule.Constants.Single(c => c.Name == s.Name).Value));

    static string Show(object value) => value switch
    {
        float number => number.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
        TimeSpan span => $"{span.TotalDays.ToString(CultureInfo.InvariantCulture)} days",
        _ => value.ToString() ?? "",
    };

    static OperationSignature Op(string id) => Language.SemanticCatalog.Operations[id];

    static ProjectionHandler Handler(OperationSignature signature, Func<object[], object> run) =>
        new(signature, arguments => new ProjectedValue(signature.Result, run(arguments.Select(a => a.Value).ToArray())));

    static DateCalcLine Error(string source, TextSpan span, string message) => new(LineOf(source, span.Start), message, true);

    static int LineOf(string source, int offset) => source.AsSpan(0, Math.Min(offset, source.Length)).Count('\n') + 1;
}
