namespace Nitrogen;

public sealed unsafe partial class Language
{
    public IReadOnlyList<QuoteHelperError> ValidateQuoteHelpers()
    {
        var errors = new List<QuoteHelperError>();
        foreach (var helper in Modules.SelectMany(module => module.QuoteHelpers))
        {
            using var plan = QuoteMatchPlan.Compile(this, helper);
            errors.AddRange(plan.Errors);
        }
        return errors;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<QuoteMatchPlan>> _matchPlans = new(StringComparer.Ordinal);

    internal QuoteMatchPlan MatchPlan(string qualifiedHelperName) => _matchPlans.GetOrAdd(qualifiedHelperName, name =>
        new Lazy<QuoteMatchPlan>(() => QuoteMatchPlan.Compile(this,
            Modules.SelectMany(module => module.QuoteHelpers).Single(helper => helper.Name == name)))).Value;

    internal bool AcceptsQuoteNode(string category, int kind) => Modules
        .FirstOrDefault(m => m.Id == SyntaxKinds.ModuleOf(kind))?.GetQuoteCategories(SyntaxKinds.LocalOf(kind))
        .Contains(category, StringComparer.Ordinal) == true;

    /// <summary>Parses a quotation once. Its compilation owns the template; each instantiation owns fresh syntax.</summary>
    public QuoteCompilation CompileQuote(string category, string text, IReadOnlyDictionary<string, QuoteHole> holes)
        => CompileQuote(category, text, holes, null);

    public QuoteCompilation CompileQuote(string category, string text, IReadOnlyDictionary<string, QuoteHole> holes,
        QuoteOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(holes);
        var copied = holes.ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
        var diagnostics = new List<QuoteDiagnostic>();
        foreach (string name in copied.Values.Select(h => h.Category).Append(category).Distinct(StringComparer.Ordinal))
            if (!Modules.Any(m => m.QuoteCategoryNames.Contains(name, StringComparer.Ordinal)))
                diagnostics.Add(new("NQ0001", default, $"'{name}' has no generated quotation category metadata."));
        if (diagnostics.Count > 0) return new(null, diagnostics.ToArray(), copied);
        ParseResult parsed;
        try { parsed = ParseTemplate(text, category, copied); }
        catch (ArgumentException error) { return new(null, [new("NQ0001", default, error.Message)], copied); }
        if (!parsed.Success || parsed.HasErrors)
        {
            foreach (var diagnostic in parsed.Diagnostics)
                diagnostics.Add(new("NQ0001", diagnostic.Span, parsed.FormatMessage(diagnostic)));
            if (diagnostics.Count == 0) diagnostics.Add(new("NQ0001", default, "The quotation did not parse."));
        }
        else if (Enumerable.Range(0, parsed.Tree.NodeCount).Any(n => parsed.Tree.Kind(n) == SyntaxKinds.ListHole &&
            !parsed.Tree.TemplateLists.ContainsKey(parsed.Tree.Parent(n))))
            diagnostics.Add(new("NQ0001", parsed.Tree.Span(parsed.Tree.Root), "A list splice requires a direct syntax element and a literal separator."));
        else if (Nitrogen.Binding.FileBinding.Bind("nitrogen:quote", parsed.Tree).Declarations.Count > 0)
            diagnostics.Add(new("NQ0006", parsed.Tree.Span(parsed.Tree.Root), "A quotation template cannot declare names."));
        if (diagnostics.Count == 0) return new(parsed, [], copied, options);
        parsed.Dispose();
        return new(null, diagnostics.ToArray(), copied);
    }
}
