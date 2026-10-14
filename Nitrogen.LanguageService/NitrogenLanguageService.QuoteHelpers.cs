using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    IReadOnlyList<DocumentLocation>? QuoteHelperRelated(IReadOnlyList<Nitrogen.Semantic.CompositionDiagnosticLocation> sources) => sources.Count == 0 ? null : sources
        .Where(source => !string.IsNullOrEmpty(source.Path) && ReadNavigationText(source.Path) is not null)
        .Select(source => new DocumentLocation(new Uri(Path.GetFullPath(source.Path)).AbsoluteUri,
            new LineMap(ReadNavigationText(source.Path)!).RangeOf(source.Span))).ToArray();

    QuoteHelperSourceIndex? QuoteHelperIndex(string uri, DocumentPosition position, bool completing = false)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || !TryDocument(uri, out var document)) return null;
        string path = parsed.LocalPath;
        if (!path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase) &&
            !DocumentSignatures.Read(TextOf(uri) ?? "").Signatures.Any(s => s.Mode == SignatureMode.Grammar)) return null;
        var configured = _grammarLanguages.FirstOrDefault(l => l.Workspace.Paths.Contains(path, StringComparer.Ordinal));
        var files = configured?.Files() ?? QuoteGrammarFiles(path) ?? NavigationScopeFor(path)?.Grammars.ToList() ?? [path];
        var connected = files.Append(path).ToHashSet(StringComparer.Ordinal);
        var compositions = _grammarLanguages.Select(l => (IEnumerable<string>)l.Files())
            .Concat(_signatureDefinitions.Values.Select(d => d.Grammars.Select(g => g.Path))).ToArray();
        bool grew;
        do
        {
            int count = connected.Count;
            foreach (var composition in compositions)
                if (composition.Any(connected.Contains)) connected.UnionWith(composition);
            grew = connected.Count != count;
        } while (grew);
        var texts = connected.Select(file => (Path: file, Text: ReadNavigationText(file)))
            .Where(input => input.Text is not null).ToDictionary(input => input.Path, input => input.Text!, StringComparer.Ordinal);
        return new(texts, completing ? path : null, completing ? document.Lines.OffsetOf(position) : 0, languageFactory: () => LanguageForQuote(uri));
    }

    IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>>? RenameQuoteHelper(string uri, DocumentPosition position, string newName)
    {
        var index = QuoteHelperIndex(uri, position);
        string path = new Uri(uri).LocalPath;
        if (index?.At(path, position) is { } target)
        {
            if (target.Kind == "helper" && NavigationScopeFor(path) is { } scope && scope.Sources.Any(source =>
                ReadNavigationText(source)?.Contains('"' + target.Identity["helper:".Length..] + '"', StringComparison.Ordinal) == true))
                throw new RenameRefusedException("A configured helper source uses this helper's name as a string; update that consumer explicitly.");
            return index.Rename(path, position, newName);
        }
        if (index is not null && GrammarRuleAt(uri, position) is { } rule)
            foreach (var definition in rule.Definitions)
                if (index.At(new Uri(definition.Uri).LocalPath, definition.Range.Start) is not null)
                    throw new RenameRefusedException("This rule has quote-helper category consumers; update its resolved uses together.");
        return null;
    }
}
