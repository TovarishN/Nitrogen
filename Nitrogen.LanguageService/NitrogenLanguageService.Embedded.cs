using Nitrogen.Binding;

namespace Nitrogen.LanguageService;

/// <summary>
/// Languages inside C# (tagged strings): an open <c>.cs</c> document is a host, and each string literal
/// tagged with a served language (<see cref="EmbeddedStrings"/>) is a virtual document of that language,
/// bound in its project like a file. Requests on the host go to the virtual document under the position,
/// and their ranges and locations are mapped back through the literal's source map. The tag names a
/// language by name or by an extension without its dot, ignoring case.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    const string EmbeddedMarker = "#nitrogen-embedded-";

    sealed class Host(string uri, int version, string text)
    {
        public string Uri { get; } = uri;

        public int Version { get; } = version;

        public string Text { get; } = text;

        public LineMap Lines { get; } = new(text);

        public List<string> Embedded { get; } = new();
    }

    readonly Dictionary<string, Host> _hosts = new(StringComparer.Ordinal);
    readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (Host Host, EmbeddedString Source)> _embedded = new(StringComparer.Ordinal);

    /// <summary>Documents whose tagged strings are served: C# sources.</summary>
    static bool IsHost(string uri)
    {
        int end = uri.IndexOfAny(['?', '#']);
        return (end < 0 ? uri : uri[..end]).EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsEmbedded(string uri) => uri.Contains(EmbeddedMarker, StringComparison.Ordinal);

    IReadOnlyList<string> UpdateHost(string uri, int version, string text)
    {
        var languages = new HashSet<LanguageEntry>();
        if (_hosts.Remove(uri, out var old)) languages.UnionWith(Unembed(old));
        var host = new Host(uri, version, text);
        _hosts[uri] = host;
        languages.UnionWith(Embed(host));
        return Visible(languages.SelectMany(OpenDocuments).Prepend(uri));
    }

    IReadOnlyList<string> CloseHost(string uri)
    {
        if (!_hosts.Remove(uri, out var host)) return [];
        var languages = Unembed(host);
        return Visible(languages.SelectMany(OpenDocuments));
    }

    /// <summary>Serves the host's tagged strings of the languages registered now; the languages they belong to.</summary>
    List<LanguageEntry> Embed(Host host)
    {
        var languages = new List<LanguageEntry>();
        foreach (var source in EmbeddedStrings.Find(host.Text))
        {
            if (Tagged(source.Tag) is not { } tagged) continue;
            string uri = host.Uri + EmbeddedMarker + host.Embedded.Count;
            var document = new Document(uri, host.Version, source.Value, tagged.Entry, tagged.Start);
            if (!_projects.TryGetValue(tagged.Entry, out var project)) _projects[tagged.Entry] = project = new Project(tagged.Entry.Language);
            project.Set(uri, document.Parsed.Tree);
            _documents[uri] = document;
            _embedded[uri] = (host, source);
            host.Embedded.Add(uri);
            languages.Add(tagged.Entry);
        }
        return languages;
    }

    List<LanguageEntry> Unembed(Host host)
    {
        var languages = new List<LanguageEntry>();
        foreach (string uri in host.Embedded)
        {
            _inspection.Remove(uri);
            _embedded.Remove(uri);
            if (!_documents.Remove(uri, out var document)) continue;
            if (_projects.TryGetValue(document.Language, out var project)) project.Remove(uri);
            languages.Add(document.Language);
            document.Dispose();
        }
        host.Embedded.Clear();
        return languages;
    }

    /// <summary>Stops serving every host's strings, while the languages change.</summary>
    void UnembedAll()
    {
        foreach (var host in _hosts.Values) Unembed(host);
    }

    /// <summary>Serves every host's strings again with the current languages; the hosts.</summary>
    IEnumerable<string> EmbedAll()
    {
        foreach (var host in _hosts.Values) Embed(host);
        return _hosts.Keys.ToList();
    }

    /// <summary>
    /// Leaves the tagged strings of these languages (by name, or extension without the dot, ignoring case)
    /// to another server: an editor plugin that carries the language serves them itself. Set before the
    /// first document opens.
    /// </summary>
    public void SkipEmbedded(IEnumerable<string> languages)
    {
        foreach (string language in languages) _skipped.Add(language.TrimStart('.'));
    }

    bool IsSkipped(LanguageEntry entry) =>
        _skipped.Contains(entry.Name) || entry.Starts.Keys.Any(extension => _skipped.Contains(extension.TrimStart('.')));

    (LanguageEntry Entry, Rule Start)? Tagged(string tag)
    {
        foreach (var entry in Registry.Entries)
        {
            if (IsSkipped(entry)) continue;
            if (entry.Starts.TryGetValue("." + tag, out var start)) return (entry, start);
            if (string.Equals(entry.Name, tag, StringComparison.OrdinalIgnoreCase) && entry.Starts.Count > 0)
                return (entry, entry.Starts.OrderBy(s => s.Key, StringComparer.Ordinal).First().Value);
        }
        return null;
    }

    /// <summary>Hosts stand for their virtual documents in the lists of documents to refresh.</summary>
    static IReadOnlyList<string> Visible(IEnumerable<string> uris) =>
        uris.Select(uri => IsEmbedded(uri) ? uri[..uri.IndexOf(EmbeddedMarker, StringComparison.Ordinal)] : uri).Distinct().ToList();

    /// <summary>The virtual document under a host position, and the position in it; null outside every tagged string.</summary>
    (string Uri, DocumentPosition Position)? Into(string hostUri, DocumentPosition position)
    {
        if (!_hosts.TryGetValue(hostUri, out var host)) return null;
        int offset = host.Lines.OffsetOf(position);
        foreach (string uri in host.Embedded)
            if (_embedded[uri].Source.ValueOffset(offset) is int inner)
                return (uri, _documents[uri].Lines.PositionOf(inner));
        return null;
    }

    /// <summary>A range of a virtual document as the range of its host it was written at.</summary>
    DocumentRange OutOf(string uri, DocumentRange range)
    {
        var (host, source) = _embedded[uri];
        var lines = _documents[uri].Lines;
        int Map(DocumentPosition position) => source.Map[Math.Clamp(lines.OffsetOf(position), 0, source.Map.Length - 1)];
        return new DocumentRange(host.Lines.PositionOf(Map(range.Start)), host.Lines.PositionOf(Map(range.End)));
    }

    DocumentLocation OutOf(DocumentLocation location) =>
        _embedded.TryGetValue(location.Uri, out var embedded) ? new DocumentLocation(embedded.Host.Uri, OutOf(location.Uri, location.Range)) : location;

    /// <summary>The diagnostics of the host's strings, and of the host itself when it is a workspace language's helper source.</summary>
    IReadOnlyList<ServiceDiagnostic> HostDiagnostics(Host host) => host.Embedded
        .SelectMany(uri => Diagnostics(uri).Select(d => d with { Range = OutOf(uri, d.Range) }))
        .Concat(GrammarDiagnostics(host.Uri))
        .ToList();

    /// <summary>Tokens of a virtual document at its host's positions; a token whose characters are not contiguous in the host (an escape) keeps its first line's part.</summary>
    IReadOnlyList<SemanticToken> HostTokens(Host host)
    {
        var tokens = new List<SemanticToken>();
        foreach (string uri in host.Embedded)
            foreach (var token in SemanticTokens(uri))
            {
                var range = OutOf(uri, new DocumentRange(token.Start, token.Start with { Character = token.Start.Character + token.Length }));
                int length = range.End.Line == range.Start.Line ? range.End.Character - range.Start.Character
                    : host.Lines.LineLength(range.Start.Line) - range.Start.Character;
                if (length > 0) tokens.Add(token with { Start = range.Start, Length = length });
            }
        tokens.Sort((a, b) => a.Start.Line != b.Start.Line ? a.Start.Line.CompareTo(b.Start.Line) : a.Start.Character.CompareTo(b.Start.Character));
        return tokens;
    }

    void DisposeHosts()
    {
        UnembedAll();
        _hosts.Clear();
    }
}
