namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record NavigationScope(string Key, IReadOnlyList<string> Grammars, IReadOnlyList<string> Sources,
        string GeneratedNamespace = Nitrogen.Workspace.GrammarWorkspace.Namespace, IReadOnlyList<string>? Usings = null);

    readonly Dictionary<string, (Dictionary<string, string> Texts, SemanticSourceIndex Index)> _sourceIndexes = new(StringComparer.Ordinal);

    (NavigationScope Scope, AssistName Name, IReadOnlyList<DocumentLocation> Definitions)? AssistedNavigation(string uri, DocumentPosition position)
    {
        if (IsEmbedded(uri)) return null;
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        string path = Path.GetFullPath(parsed.LocalPath);
        if (!path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
        AssistName? name;
        NavigationScope? scope = null;
        if (path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDocument(uri, out var document) || AssistOf(document) is not { } assist) return null;
            int offset = document.Lines.OffsetOf(position);
            name = document.Language.Assist!.NavigationNames(assist).FirstOrDefault(n => n.Span.Start <= offset && offset < n.Span.End);
            if (name is null) return null;
        }
        else
        {
            scope = NavigationScopeFor(path);
            if (scope is null || ReadNavigationText(path) is not { } text) return null;
            int offset = new LineMap(text).OffsetOf(position);
            name = SourcesFor(scope).Entries.FirstOrDefault(e => e.Path == path && e.Name.Span.Start <= offset && offset < e.Name.Span.End)?.Name;
            if (name is null) return null;
        }
        scope ??= NavigationScopeFor(path);
        if (scope is null) return null;
        var entries = SourcesFor(scope).Entries.Where(e => SameName(e.Name, name)).ToList();
        // Unqualified types are allowed only when their short name has one identity in this source scope.
        if (!name.Id.Contains('.') && entries.Select(e => e.Name.Id).Distinct().Take(2).Count() > 1) return (scope, name, []);
        if (entries.Count == 0 && name.Kind == AssistNameKind.Type && BuiltinTypeSources.Definition(name.Id) is { } builtin)
            return (scope, name, [builtin]);
        if (entries.Count > 0) name = name with { Id = entries[0].Name.Id };
        return (scope, name, entries.Select(e => SourceLocation(e.Path, e.Name.Span)).Distinct().ToArray());
    }

    IReadOnlyList<DocumentLocation> AssistedReferences(NavigationScope scope, AssistName name, IReadOnlyList<DocumentLocation> declarations)
    {
        var locations = declarations.ToList();
        var sourceEntries = SourcesFor(scope).Entries;
        foreach (string path in scope.Grammars)
        {
            string uri = new Uri(path).AbsoluteUri;
            if (TryDocument(uri, out var open))
            {
                Add(open);
                continue;
            }
            if (ReadNavigationText(path) is not { } text || !Registry.TryFind(uri, out var language, out var start)) continue;
            using var document = new Document(uri, 0, text, language, start);
            Add(document);
        }
        return locations.Distinct().ToArray();

        void Add(Document document)
        {
            if (AssistOf(document) is not { } assist) return;
            foreach (var reference in document.Language.Assist!.NavigationNames(assist))
            {
                var resolved = reference;
                if (reference.Kind == AssistNameKind.Type && !reference.Id.Contains('.'))
                {
                    var identities = sourceEntries.Where(e => SameName(e.Name, reference))
                        .Select(e => e.Name.Id).Distinct().Take(2).ToArray();
                    if (identities.Length != 1) continue;
                    resolved = reference with { Id = identities[0] };
                }
                if (SameName(resolved, name)) locations.Add(new DocumentLocation(document.Uri, document.Lines.RangeOf(reference.Span)));
            }
        }
    }

    static bool SameName(AssistName a, AssistName b) => a.Kind == b.Kind &&
        (a.Id == b.Id || a.Kind == AssistNameKind.Type && !b.Id.Contains('.') && a.Id.EndsWith("." + b.Id, StringComparison.Ordinal));

    DocumentLocation SourceLocation(string path, TextSpan span) =>
        new(new Uri(path).AbsoluteUri, new LineMap(ReadNavigationText(path)!).RangeOf(span));

    SemanticSourceIndex SourcesFor(NavigationScope scope)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in scope.Sources)
            if (ReadNavigationText(path) is { } text) texts[path] = text;
        if (_sourceIndexes.TryGetValue(scope.Key, out var cached) && cached.Texts.Count == texts.Count &&
            texts.All(pair => cached.Texts.TryGetValue(pair.Key, out string? text) && text == pair.Value)) return cached.Index;
        var index = new SemanticSourceIndex(texts);
        _sourceIndexes[scope.Key] = (texts, index);
        return index;
    }

    string? ReadNavigationText(string path)
    {
        if (TextOf(new Uri(path).AbsoluteUri) is { } open) return open;
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaxIndexedFileBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    NavigationScope? NavigationScopeFor(string path)
    {
        foreach (var language in _grammarLanguages)
            if (language.Files().Contains(path, StringComparer.Ordinal) || language.SourceFiles().Contains(path, StringComparer.Ordinal))
                return new NavigationScope("language:" + language.Name, language.Files(), language.SourceFiles(), language.Workspace.GeneratedNamespace, language.Usings);
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) return null;
        string project = directory;
        while (Directory.Exists(project))
        {
            if (HasProject(project)) break;
            if (project == _root || Path.GetDirectoryName(project) is not { } parent)
            {
                project = directory;
                break;
            }
            project = parent;
        }
        var files = ProjectFiles(project).Take(MaxIndexedFiles).ToList();
        // Open unsaved grammars and C# sources also belong to their enclosing project.
        files.AddRange(_documents.Keys.Concat(_hosts.Keys).Select(uri => System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
                ? parsed.LocalPath : "").Where(file => file.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !files.Contains(file, StringComparer.Ordinal) && Path.GetDirectoryName(file) == directory));
        return new NavigationScope(project, files.Where(f => f.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase)).ToArray(),
            files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    static bool HasProject(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.csproj").Length > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static IEnumerable<string> ProjectFiles(string directory)
    {
        string[] files, children;
        try
        {
            files = Directory.GetFiles(directory);
            children = Directory.GetDirectories(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string file in files.Order(StringComparer.Ordinal)) yield return file;
        foreach (string child in children.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || s_skippedDirectories.Contains(name) || HasProject(child)) continue;
            foreach (string file in ProjectFiles(child)) yield return file;
        }
    }
}
