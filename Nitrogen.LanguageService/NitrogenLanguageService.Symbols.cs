using Nitrogen.Binding;

namespace Nitrogen.LanguageService;

/// <summary>
/// Workspace symbol search: the declarations of the open documents (tagged strings included, located in
/// their C# file), of the closed workspace files, and of the closed grammars, matched by a subsequence of
/// their name ignoring case, ordered by name and capped.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    const int MaxWorkspaceSymbols = 1_000;

    /// <summary>The workspace languages' grammars that aren't open, each bound in a project of its own: search only, so two grammars exporting one module don't clash.</summary>
    readonly Dictionary<string, (Document Document, Project Project)> _closedGrammars = new(StringComparer.Ordinal);

    /// <summary>Reads the grammars of the workspace languages that aren't open; one whose text hasn't changed is kept.</summary>
    void RefreshClosedGrammars()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in _grammarLanguages)
            foreach (string path in language.Files())
            {
                string uri = new Uri(path).AbsoluteUri;
                if (_documents.ContainsKey(uri) || texts.ContainsKey(uri)) continue;
                try
                {
                    if (new FileInfo(path).Length <= MaxIndexedFileBytes) texts[uri] = File.ReadAllText(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        foreach (var (uri, kept) in _closedGrammars.ToList())
            if (!texts.TryGetValue(uri, out string? text) || text != kept.Document.Text)
            {
                _closedGrammars.Remove(uri);
                kept.Document.Dispose();
            }
        foreach (var (uri, text) in texts)
        {
            if (_closedGrammars.ContainsKey(uri) || !Registry.TryFind(uri, out var language, out var start)) continue;
            var document = new Document(uri, 0, text, language, start);
            var project = new Project(language.Language);
            project.Set(uri, document.Parsed.Tree);
            _closedGrammars[uri] = (document, project);
        }
    }

    void DisposeClosedGrammars()
    {
        foreach (var (document, _) in _closedGrammars.Values) document.Dispose();
        _closedGrammars.Clear();
    }

    public IReadOnlyList<WorkspaceSymbol> WorkspaceSymbols(string query)
    {
        var symbols = new List<WorkspaceSymbol>();
        foreach (var document in _documents.Values.Concat(_closed.Values)) symbols.AddRange(SymbolsOf(document, _projects[document.Language]));
        foreach (var (document, project) in _closedGrammars.Values) symbols.AddRange(SymbolsOf(document, project));
        return symbols.Where(s => Matches(query, s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Location.Uri, StringComparer.Ordinal)
            .ThenBy(s => s.Location.Range.Start.Line)
            .ThenBy(s => s.Location.Range.Start.Character)
            .Take(MaxWorkspaceSymbols)
            .ToList();
    }

    /// <summary>A document's declarations, each with the name of the nearest declaration whose node contains it, as the outline nests them.</summary>
    IEnumerable<WorkspaceSymbol> SymbolsOf(Document document, Project project)
    {
        var tree = document.Parsed.Tree;
        var presentation = document.Language.Presentation;
        var open = new Stack<(TextSpan Span, string Name)>();
        var declarations = project[document.Uri].Declarations
            .OrderBy(d => tree.Span(d.Node).Start)
            .ThenByDescending(d => tree.Span(d.Node).Length);
        foreach (var declaration in declarations)
        {
            var span = tree.Span(declaration.Node);
            while (open.Count > 0 && !(open.Peek().Span.Start <= span.Start && span.End <= open.Peek().Span.End)) open.Pop();
            string? container = open.Count > 0 ? open.Peek().Name : null;
            open.Push((span, declaration.Name));
            yield return new WorkspaceSymbol(declaration.Name, declaration.Kind, presentation.StyleOf(declaration.Kind).Outline,
                OutOf(new DocumentLocation(document.Uri, document.Lines.RangeOf(declaration.NameSpan))), container);
        }
    }

    /// <summary>Whether the query's characters appear in the name in order, ignoring case; the empty query matches every name.</summary>
    static bool Matches(string query, string name)
    {
        int matched = 0;
        foreach (char c in name)
            if (matched < query.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[matched])) matched++;
        return matched == query.Length;
    }
}
