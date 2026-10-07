using System.Globalization;
using Nitrogen;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace DateCalc.Syntax;

/// <summary>One shown value, or one diagnostic, at a source line (1-based).</summary>
public sealed record DateCalcLine(int Line, string Text, bool IsError = false);

/// <summary>
/// Runs DateCalc source: parse, bind, check, lower to typed HIR, then project each statement through
/// <see cref="Profile"/>'s handlers. The language server finds <see cref="Profile"/> too and shows each
/// statement's value as an inlay hint.
/// </summary>
public static class DateCalcEvaluator
{
    static readonly Lazy<Language> s_language = new(() => new LanguageBuilder().Add(DateCalcModule.Instance)
        .AddSemantic(MathModule.Semantics).AddSemantic(DateCalcLanguage.Semantics).Build());

    /// <summary>The language <see cref="Run"/> parses with, built on first use: reading <see cref="Profile"/> does not build it.</summary>
    public static Language Language => s_language.Value;

    /// <summary>Lets and shown expressions, each projected to a number, date, duration or text; <c>today</c> is the context's date.</summary>
    public static readonly EvaluationProfile Profile = new(
        new HashSet<int> { DateCalcKinds.Let, DateCalcKinds.Show },
        Handlers,
        (symbol, context) => symbol.Name == "today"
            ? new ProjectedValue(DateCalcLanguage.Date, context.Today)
            : MathModule.Constants.Where(c => c.Name == symbol.Name)
                .Select(c => new ProjectedValue(DateCalcLanguage.Number, c.Value)).FirstOrDefault(),
        value => Show(value.Value),
        readsClock: true);

    static readonly Lazy<BoundEvaluation> s_bound = new(() => Profile.Bind(Language.SemanticCatalog));

    /// <summary>The value of each statement, in order, or the diagnostics that stop it from running; <c>today</c> is <paramref name="today"/>, else the local date.</summary>
    public static IReadOnlyList<DateCalcLine> Run(string source, DateOnly? today = null)
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

        var lowered = HirLowering.LowerSelected(file, Profile.StatementKinds, Guid.NewGuid());
        if (lowered.Diagnostics.Count > 0)
            return lowered.Diagnostics.Select(d => Error(source, d.Origin.Span, $"{d.Code}: {d.Message}")).ToList();
        var context = new EvaluationContext(today is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)) : DateTimeOffset.Now);
        var registry = s_bound.Value.Registry;
        return lowered.Roots.Select(root =>
        {
            var result = HirProjector.Project(root, registry, Builtins(root, context));
            return result.Value is { } value
                ? new DateCalcLine(LineOf(source, root.Origins[0].Span.Start), Profile.Format(value))
                : Error(source, root.Origins[0].Span, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        }).ToList();
    }

    static IEnumerable<ProjectionHandler> Handlers(SemanticCatalog catalog)
    {
        ProjectionHandler Handler(string id, Func<object[], object> run)
        {
            var signature = catalog.Operations[id];
            return new(signature, arguments => new ProjectedValue(signature.Result, run(arguments.Select(a => a.Value).ToArray())));
        }

        return
        [
            Handler(DateCalcLanguage.ParseDate.Id, a => DateOnly.ParseExact((string)a[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Handler(DateCalcLanguage.Days.Id, a => TimeSpan.FromDays((float)a[0])),
            Handler(DateCalcLanguage.Weeks.Id, a => TimeSpan.FromDays(7 * (float)a[0])),
            Handler(DateCalcLanguage.InDays.Id, a => (float)((TimeSpan)a[0]).TotalDays),
            Handler("DateCalc.Add", a => (float)a[0] + (float)a[1]),
            Handler("DateCalc.Subtract", a => (float)a[0] - (float)a[1]),
            Handler("DateCalc.Multiply", a => (float)a[0] * (float)a[1]),
            Handler("DateCalc.Divide", a => (float)a[0] / (float)a[1]),
            Handler("DateCalc.Later", a => ((DateOnly)a[0]).AddDays((int)((TimeSpan)a[1]).TotalDays)),
            Handler("DateCalc.Earlier", a => ((DateOnly)a[0]).AddDays(-(int)((TimeSpan)a[1]).TotalDays)),
            Handler("DateCalc.Between", a => TimeSpan.FromDays(((DateOnly)a[0]).DayNumber - ((DateOnly)a[1]).DayNumber)),
            Handler("DateCalc.AddDurations", a => (TimeSpan)a[0] + (TimeSpan)a[1]),
            Handler("DateCalc.SubtractDurations", a => (TimeSpan)a[0] - (TimeSpan)a[1]),
            Handler("DateCalc.Scale", a => (TimeSpan)a[0] * (float)a[1]),
            Handler("DateCalc.Times", a => (float)a[0] * (TimeSpan)a[1]),
            Handler("DateCalc.Ratio", a => (float)((TimeSpan)a[0] / (TimeSpan)a[1])),
            .. DateCalcLanguage.AllFunctions.Select(f => Handler(f.Signature.Id, f.Run)),
        ];
    }

    /// <summary>The values of the builtins the root refers to, in <paramref name="context"/>.</summary>
    static Dictionary<Symbol, ProjectedValue> Builtins(HirNode root, EvaluationContext context) => HirTraversal.PreOrder(root).OfType<HirSymbolRef>()
        .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
        .Select(s => (Symbol: s, Value: Profile.Builtin(s, context))).Where(p => p.Value is not null)
        .ToDictionary(p => p.Symbol, p => p.Value!);

    static string Show(object value) => value switch
    {
        float number => number.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
        TimeSpan span => $"{span.TotalDays.ToString(CultureInfo.InvariantCulture)} days",
        _ => value.ToString() ?? "",
    };

    static DateCalcLine Error(string source, TextSpan span, string message) => new(LineOf(source, span.Start), message, true);

    static int LineOf(string source, int offset) => source.AsSpan(0, Math.Min(offset, source.Length)).Count('\n') + 1;
}
