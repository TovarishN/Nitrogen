namespace Nitrogen;

/// <summary>Snapshot-local compiled helper syntax. Validation-only callers dispose their temporary plan.</summary>
internal sealed class QuoteMatchPlan : IDisposable
{
    readonly List<IDisposable> _storage = [];
    internal QuoteTemplate? Fallback { get; private set; }
    internal IReadOnlyList<QuoteMatchPlanArm> Arms { get; private set; } = [];
    internal IReadOnlyList<QuoteHelperError> Errors { get; private set; } = [];

    internal static QuoteMatchPlan Compile(Language language, QuoteHelper helper)
    {
        var plan = new QuoteMatchPlan();
        try
        {
            var errors = new List<QuoteHelperError>();
            var arms = new List<QuoteMatchPlanArm>();
            var fallback = language.CompileQuote(helper.Category, helper.Text, helper.Parameters);
            plan._storage.Add(fallback);
            plan.Fallback = fallback.Template;
            Add(fallback.Diagnostics, helper.Source);
            if (helper.Match is { } match)
            {
                if (!helper.Parameters.TryGetValue(match.Selector, out var selector) || selector.IsList || selector.Category != match.SelectorCategory)
                    errors.Add(new(helper, new("NQP0002", default, "The match selector must name a scalar helper parameter.")));
                foreach (var arm in match.Arms)
                {
                    var scope = helper.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    foreach (var (name, contract) in arm.Captures)
                        if (!scope.TryAdd(name, contract))
                            errors.Add(new(helper, new("NQP0002", default, "A capture cannot shadow a helper parameter.", name)) { Source = arm.PatternSource });
                    var pattern = language.CompilePattern(match.SelectorCategory, arm.Pattern, arm.Captures);
                    plan._storage.Add(pattern);
                    Add(pattern.Diagnostics, arm.PatternSource);
                    var result = language.CompileQuote(helper.Category, arm.Result, scope);
                    plan._storage.Add(result);
                    Add(result.Diagnostics, arm.ResultSource);
                    if (pattern.Success && result.Success) arms.Add(new(pattern.Pattern!, result.Template!, arm.ResultSource));
                }
            }
            plan.Arms = Array.AsReadOnly(arms.ToArray());
            plan.Errors = Array.AsReadOnly(errors.ToArray());
            return plan;

            void Add(IReadOnlyList<QuoteDiagnostic> diagnostics, QuoteHelperSource source) =>
                errors.AddRange(diagnostics.Select(diagnostic => new QuoteHelperError(helper, diagnostic) { Source = source }));
        }
        catch { plan.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var storage in _storage) storage.Dispose();
        _storage.Clear();
    }
}

internal sealed record QuoteMatchPlanArm(QuotePattern Pattern, QuoteTemplate Result, QuoteHelperSource Source);
