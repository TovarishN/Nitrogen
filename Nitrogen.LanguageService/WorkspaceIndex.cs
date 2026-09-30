using Nitrogen.Binding;

namespace Nitrogen.LanguageService;

/// <summary>
/// Closed workspace files of nitrogen.json languages (spec: workspace indexing): read from disk and bound
/// in their language's project beside the open documents, so references into them resolve.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    const int MaxIndexedFiles = 10_000;
    const long MaxIndexedFileBytes = 1 << 20;

    static readonly HashSet<string> s_skippedDirectories = new(StringComparer.Ordinal) { "bin", "obj", "node_modules" };

    readonly Dictionary<string, Document> _closed = new(StringComparer.Ordinal);
    string? _workspaceRoot;

    /// <summary>Binds the files under <paramref name="root"/> of the workspace grammar languages; the open documents that may change.</summary>
    public IReadOnlyList<string> IndexWorkspace(string root)
    {
        _workspaceRoot = Path.GetFullPath(root);
        return Reindex();
    }

    /// <summary>The file extensions indexed now, for clients that watch files for the server.</summary>
    public IReadOnlyList<string> IndexedExtensions() => _grammarLanguages
        .Where(l => l.Entry is not null).SelectMany(l => l.Extensions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>Drops every closed document and reads the workspace again.</summary>
    IReadOnlyList<string> Reindex()
    {
        foreach (var uri in _closed.Keys.ToList()) RemoveClosed(uri);
        if (_workspaceRoot is null) return [];
        var languages = new HashSet<LanguageEntry>();
        int count = 0;
        foreach (string path in WorkspaceFiles(_workspaceRoot))
        {
            if (count == MaxIndexedFiles) break;
            if (LoadClosed(path) is { } language)
            {
                languages.Add(language);
                count++;
            }
        }
        return languages.SelectMany(OpenDocuments).Distinct().ToList();
    }

    static IEnumerable<string> WorkspaceFiles(string directory)
    {
        string[] files, directories;
        try
        {
            files = Directory.GetFiles(directory);
            directories = Directory.GetDirectories(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string file in files.Order(StringComparer.Ordinal)) yield return file;
        foreach (string child in directories.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || s_skippedDirectories.Contains(name)) continue;
            foreach (string file in WorkspaceFiles(child)) yield return file;
        }
    }

    /// <summary>The language of an indexed file, or null for a file this index does not keep.</summary>
    LanguageEntry? IndexedLanguage(string uri, out Rule start)
    {
        start = default;
        if (!Registry.TryFind(uri, out var language, out start)) return null;
        return _grammarLanguages.Any(l => l.Entry == language) ? language : null;
    }

    bool InWorkspace(string path) =>
        _workspaceRoot is not null && Path.GetFullPath(path).StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Reads <paramref name="path"/> into the index unless it is open, too big, unreadable, or of no indexed language; its language when read.</summary>
    LanguageEntry? LoadClosed(string path)
    {
        string uri = new Uri(path).AbsoluteUri;
        if (_documents.ContainsKey(uri) || IndexedLanguage(uri, out var start) is not { } language) return null;
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxIndexedFileBytes) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var document = new Document(uri, 0, text, language, start);
        if (!_projects.TryGetValue(language, out var project)) _projects[language] = project = new Project(language.Language);
        project.Set(uri, document.Parsed.Tree);
        if (_closed.Remove(uri, out var previous)) previous.Dispose();
        _closed[uri] = document;
        _inspection.Remove(uri);
        return language;
    }

    /// <summary>Removes a closed document from the index and its project; its language, or null when it was not indexed.</summary>
    LanguageEntry? RemoveClosed(string uri)
    {
        if (!_closed.Remove(uri, out var document)) return null;
        if (_projects.TryGetValue(document.Language, out var project)) project.Remove(uri);
        document.Dispose();
        return document.Language;
    }

    /// <summary>An open document, else a closed one.</summary>
    bool TryDocument(string uri, out Document document) =>
        _documents.TryGetValue(uri, out document!) || _closed.TryGetValue(uri, out document!);

    Document DocumentAt(string uri) => TryDocument(uri, out var document) ? document : throw new KeyNotFoundException(uri);

    void DisposeClosed()
    {
        foreach (var document in _closed.Values) document.Dispose();
        _closed.Clear();
    }
}
