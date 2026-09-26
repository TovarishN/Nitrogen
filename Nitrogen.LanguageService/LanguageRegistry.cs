using System.Diagnostics.CodeAnalysis;

namespace Nitrogen.LanguageService;

/// <summary>A language the service serves: its documents share one binding project.</summary>
/// <param name="starts">File extension (with the dot) → the start rule its documents parse with.</param>
/// <param name="presentation">How its symbol kinds look in the editor; <see cref="Presentation.Default"/> when null.</param>
public sealed class LanguageEntry(string name, Language language, IReadOnlyDictionary<string, Rule> starts, Presentation? presentation = null)
{
    public string Name { get; } = name;

    public Language Language { get; } = language;

    public IReadOnlyDictionary<string, Rule> Starts { get; } = starts;

    public Presentation Presentation { get; } = presentation ?? Presentation.Default;
}

/// <summary>File extensions → languages (issue 238). An extension belongs to one language.</summary>
public sealed class LanguageRegistry
{
    readonly Dictionary<string, (LanguageEntry Entry, Rule Start)> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    readonly List<LanguageEntry> _entries = new();

    public IReadOnlyList<LanguageEntry> Entries => _entries;

    public void Add(LanguageEntry entry)
    {
        foreach (var (extension, _) in entry.Starts)
            if (_byExtension.ContainsKey(extension))
                throw new ArgumentException($"'{extension}' is already served by {_byExtension[extension].Entry.Name}.", nameof(entry));
        foreach (var (extension, start) in entry.Starts) _byExtension[extension] = (entry, start);
        _entries.Add(entry);
    }

    /// <summary>Stops serving a language (a workspace grammar that is being replaced).</summary>
    public void Remove(LanguageEntry entry)
    {
        foreach (var (extension, _) in entry.Starts)
            if (_byExtension.TryGetValue(extension, out var found) && found.Entry == entry) _byExtension.Remove(extension);
        _entries.Remove(entry);
    }

    /// <summary>The language and start rule for a document URI, by its extension.</summary>
    public bool TryFind(string uri, [NotNullWhen(true)] out LanguageEntry? entry, out Rule start)
    {
        if (_byExtension.TryGetValue(Extension(uri), out var found))
        {
            (entry, start) = found;
            return true;
        }
        entry = null;
        start = default;
        return false;
    }

    static string Extension(string uri)
    {
        int end = uri.IndexOfAny(['?', '#']);
        string path = end < 0 ? uri : uri[..end];
        int dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('/') ? path[dot..] : "";
    }
}
