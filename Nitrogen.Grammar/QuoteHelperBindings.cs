namespace Nitrogen.Grammar;

/// <summary>Checks that source edits preserve every previously resolved helper call.</summary>
internal static class QuoteHelperBindings
{
    internal static bool Preserved(IEnumerable<ExtractionSource> before, IEnumerable<ExtractionSource> after,
        IEnumerable<ExtractionTextEdit> edits, IReadOnlyDictionary<string, string>? renamed = null)
    {
        var changed = edits.ToLookup(e => e.Uri, StringComparer.Ordinal);
        var targets = Calls(after).ToDictionary(call => (call.Uri, call.Start), call => call.Target);
        foreach (var call in Calls(before))
        {
            int start = call.Start + changed[call.Uri].Where(e => e.Span.End <= call.Start)
                .Sum(e => e.NewText.Length - e.Span.Length);
            string expected = renamed is not null && renamed.TryGetValue(call.Target, out var replacement) ? replacement : call.Target;
            if (!targets.TryGetValue((call.Uri, start), out string? actual) || actual != expected) return false;
        }
        return true;
    }

    static IEnumerable<(string Uri, int Start, string Target)> Calls(IEnumerable<ExtractionSource> sources)
    {
        var modules = sources.SelectMany(source => (GrammarParser.Parse(DocumentSignatures.Read(source.Text).MaskedText).File?.Modules ?? default)
            .Select(module => (source.Uri, Module: module))).ToArray();
        var analysis = new GrammarAnalysis(modules.Select(m => m.Module));
        foreach (var (uri, module) in modules)
        {
            var clauses = module.Rules.OfType<SyntaxRule>().SelectMany(r => r.Clauses)
                .Concat(module.Rules.OfType<ExtensibleRule>().SelectMany(r => r.Alternatives).SelectMany(a => a.Clauses))
                .Concat(module.Extends.SelectMany(e => e.Alternatives).SelectMany(a => a.Clauses));
            foreach (var call in clauses.Select(c => c.HelperCall).OfType<QuoteHelperCall>())
                if (analysis.Helpers.Resolve(call.Helper.Name, module) is { } resolved)
                    yield return (uri, call.Helper.Span.Start, resolved.Key);
        }
    }
}
