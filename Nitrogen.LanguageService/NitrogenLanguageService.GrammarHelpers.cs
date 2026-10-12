namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record HelperIndex(Dictionary<string, string> Texts, string Namespace, string[] Usings, GrammarHelperSourceIndex Index);

    readonly Dictionary<string, HelperIndex> _helperIndexes = new(StringComparer.Ordinal);

    IReadOnlyList<DocumentLocation> GrammarHelperDefinition(string uri, DocumentPosition position)
    {
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile ||
            !parsed.LocalPath.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase)) return [];
        string path = Path.GetFullPath(parsed.LocalPath);
        if (NavigationScopeFor(path) is not { } scope) return [];
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in scope.Grammars.Concat(scope.Sources))
            if (ReadNavigationText(file) is { } text) texts[file] = text;
        string[] usings = scope.Usings?.ToArray() ?? [];
        if (!_helperIndexes.TryGetValue(scope.Key, out var cached) || cached.Namespace != scope.GeneratedNamespace ||
            !cached.Usings.SequenceEqual(usings) || cached.Texts.Count != texts.Count ||
            texts.Any(pair => !cached.Texts.TryGetValue(pair.Key, out string? text) || text != pair.Value))
        {
            cached = new HelperIndex(texts, scope.GeneratedNamespace, usings,
                new GrammarHelperSourceIndex(texts, scope.Grammars, scope.Sources, scope.GeneratedNamespace, usings));
            _helperIndexes[scope.Key] = cached;
        }
        return cached.Index.Definition(path, position);
    }
}
