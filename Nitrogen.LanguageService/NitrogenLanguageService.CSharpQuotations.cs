namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record CSharpQuote(Host Host, CSharpQuotations.Quote Quote, Language Language, IReadOnlyList<QuoteRule> Rules);
    readonly Dictionary<string, (Dictionary<string, string> Sources, IReadOnlyList<CSharpQuotations.Quote> Quotes)> _csharpQuotes = new(StringComparer.Ordinal);

    CSharpQuote? CSharpQuoteAt(string uri, DocumentPosition position) => _hosts.TryGetValue(uri, out var host)
        ? CSharpQuoteContexts(host).FirstOrDefault(context => context.Quote.Text.ValueOffset(host.Lines.OffsetOf(position)) is not null)
        : null;

    IEnumerable<CSharpQuote> CSharpQuoteContexts(Host host)
    {
        if (!System.Uri.TryCreate(host.Uri, UriKind.Absolute, out var url) || !url.IsFile) yield break;
        string path = url.LocalPath;
        var scope = NavigationScopeFor(path);
        if (scope is null) yield break;
        var sources = scope.Sources.Where(p => ReadNavigationText(p) is not null)
            .ToDictionary(p => p, p => ReadNavigationText(p)!, StringComparer.Ordinal);
        sources[path] = host.Text;
        if (!_csharpQuotes.TryGetValue(host.Uri, out var cached) || cached.Sources.Count != sources.Count ||
            !sources.All(p => cached.Sources.TryGetValue(p.Key, out var text) && text == p.Value))
            _csharpQuotes[host.Uri] = cached = (sources, CSharpQuotations.Find(path, sources));
        if (cached.Quotes.Count == 0) yield break;
        var available = Registry.Entries.Select(entry => entry.Language)
            .Concat(scope.Grammars.Select(p => LanguageForQuote(new Uri(p).AbsoluteUri)).OfType<Language>()).Distinct().ToArray();
        var rules = scope.Grammars.SelectMany(p => QuoteRules(new Uri(p).AbsoluteUri).Rules).DistinctBy(r => (r.Uri, r.Id)).ToArray();
        foreach (var quote in cached.Quotes)
        {
            var languages = available.Where(language => language.Modules.Any(module => module.QuoteCategoryNames.Contains(quote.Category, StringComparer.Ordinal)))
                .Take(2).ToArray();
            if (languages.Length == 1) yield return new(host, quote, languages[0], rules);
        }
    }

    IEnumerable<ServiceDiagnostic> CSharpPatternDiagnostics(Host host)
    {
        foreach (var context in CSharpQuoteContexts(host).Where(context => context.Quote.IsPattern))
        {
            using var compilation = context.Language.CompilePattern(context.Quote.Category, context.Quote.Text.Value, Shapes(context));
            foreach (var diagnostic in compilation.Diagnostics)
            {
                TextSpan? span = diagnostic.Span.Length > 0 ? HostSpan(context, diagnostic.Span)
                    : diagnostic.Hole is { } name && context.Quote.Holes.TryGetValue(name, out var hole) ? hole.Span
                    : HostSpan(context, new(0, context.Quote.Text.Value.Length));
                if (span is { } source)
                    yield return new(host.Lines.RangeOf(source), ServiceSeverity.Error, diagnostic.Code, diagnostic.Message);
            }
        }
    }

    static Dictionary<string, QuoteHole> Shapes(CSharpQuote context) => context.Quote.Holes.ToDictionary(p => p.Key, p => p.Value.Shape, StringComparer.Ordinal);
    static TextSpan? HostSpan(CSharpQuote context, TextSpan span) => context.Quote.Text.SourceSpan(span);

    IEnumerable<QuoteName> CSharpQuoteNames(CSharpQuote context)
    {
        string text = context.Quote.Text.Value;
        int leading = text.Length - text.TrimStart().Length;
        ParseResult parsed;
        try { parsed = context.Language.ParseTemplate(text.Trim(), context.Quote.Category, Shapes(context)); }
        catch (ArgumentException) { yield break; }
        using (parsed)
        {
            if (!parsed.Success) yield break;
            var tree = parsed.Tree;
            for (int node = 0; node < tree.NodeCount; node++)
            {
                if (tree.ChildCount(node) != 0 || tree.Span(node).Length == 0) continue;
                var span = tree.Span(node);
                if (tree.Kind(node) is SyntaxKinds.Hole or SyntaxKinds.ListHole)
                {
                    int marker = tree.Kind(node) == SyntaxKinds.ListHole ? 3 : 1;
                    string name = tree.GetText(node)[marker..].ToString();
                    if (context.Quote.Holes.TryGetValue(name, out var hole) &&
                        HostSpan(context, new(span.Start + leading + marker, name.Length)) is { } hostSpan)
                        yield return new(hostSpan,
                            new(context.Host.Uri, context.Host.Lines.RangeOf(hole.Span)),
                            $"`{name}` — {(hole.Shape.IsList ? "list" : "scalar")} splice of `{hole.Shape.Category}`", true);
                    continue;
                }
                int owner = node;
                while (owner >= 0 && SyntaxKinds.ModuleOf(tree.Kind(owner)) == 0) owner = tree.Parent(owner);
                if (owner < 0) continue;
                int kind = tree.Kind(owner);
                string? module = context.Language.Modules.FirstOrDefault(m => m.Id == SyntaxKinds.ModuleOf(kind))?.Name;
                var rules = context.Rules.Where(r => r.Id == module + "." + context.Language.GetKindName(kind)).Take(2).ToArray();
                if (rules.Length == 1 && HostSpan(context, new(span.Start + leading, span.Length)) is { } hostToken)
                    yield return new(hostToken,
                        QuoteLocation(rules[0], rules[0].NameSpan), $"`{rules[0].Id}` — syntax rule");
            }
        }
    }

    QuoteName? CSharpNameAt(CSharpQuote context, DocumentPosition position)
    {
        int offset = context.Host.Lines.OffsetOf(position);
        return CSharpQuoteNames(context).FirstOrDefault(n => n.Span.Start <= offset && offset < n.Span.End);
    }

    IReadOnlyList<DocumentLocation> CSharpQuoteReferences(CSharpQuote context, DocumentPosition position, bool declaration)
    {
        if (CSharpNameAt(context, position) is not { } name) return [];
        return CSharpQuoteNames(context).Where(n => n.Target == name.Target)
            .Select(n => new DocumentLocation(context.Host.Uri, context.Host.Lines.RangeOf(n.Span)))
            .Concat(declaration ? new[] { name.Target } : []).Distinct().ToArray();
    }

    IReadOnlyList<CompletionItem> CSharpQuoteCompletion(CSharpQuote context, DocumentPosition position)
    {
        if (context.Quote.Text.ValueOffset(context.Host.Lines.OffsetOf(position)) is not { } offset) return [];
        string text = context.Quote.Text.Value;
        int start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        string prefix = text[start..offset];
        if (HostSpan(context, new(start, offset - start)) is not { } hostSpan) return [];
        var replace = context.Host.Lines.RangeOf(hostSpan);
        if (start > 0 && text[start - 1] == '$')
        {
            if (CSharpNameAt(context, position) is { IsSplice: false }) return [];
            bool list = start >= 3 && text[(start - 3)..start] == "..$";
            int marker = start - (list ? 3 : 1);
            if (!Nitrogen.Grammar.QuotationWriter.Holes(text.Insert(offset, "X")).Any(h => h.Start == marker)) return [];
            return context.Quote.Holes.Where(p => p.Value.Shape.IsList == list && p.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(p => new CompletionItem(p.Key, CompletionKind.Field, "quotation input", replace)).ToArray();
        }
        string before = text[..start].TrimStart();
        try
        {
            var (at, _) = context.Language.ExpectedTemplate(before + "\u0001", context.Quote.Category, Shapes(context));
            if (at < before.TrimEnd().Length) return [];
            return context.Rules.SelectMany(r => Literals(r.Body)).Distinct(StringComparer.Ordinal)
                .Where(word => SemanticTokenBuilder.IsWord(word) && word.StartsWith(prefix, StringComparison.Ordinal) &&
                    context.Language.ExpectedTemplate(before + word + "\u0001", context.Quote.Category, Shapes(context)).Position >= before.Length + word.Length)
                .Select(word => new CompletionItem(word, CompletionKind.Keyword, "template keyword", replace)).ToArray();
        }
        catch (ArgumentException) { return []; }
    }
}
