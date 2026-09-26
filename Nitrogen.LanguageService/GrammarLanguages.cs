using System.Text.Json;
using Nitrogen.Binding;
using Nitrogen.Workspace;

namespace Nitrogen.LanguageService;

/// <summary>
/// Workspace grammar languages (issue 238): <c>nitrogen.json</c> maps file extensions to .ngr files,
/// compiled in-process by a <see cref="GrammarWorkspace"/> each. A compile that yields the start rule
/// re-registers the language and re-parses its documents; a failing one keeps the last good language
/// and shows its NGR diagnostics on the grammar files. An open grammar's editor text wins over disk.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    readonly List<GrammarLanguage> _grammarLanguages = new();
    string? _root;

    sealed class GrammarLanguage(string name, string[] extensions, string start, string root, string[] patterns, string[] sources, string[] usings,
        Presentation presentation)
    {
        public string Name { get; } = name;

        public string[] Extensions { get; } = extensions;

        public string Start { get; } = start;

        public Presentation Presentation { get; } = presentation;

        public GrammarWorkspace Workspace { get; } = new();

        public WorkspaceSnapshot? Snapshot { get; set; }

        public LanguageEntry? Entry { get; set; }

        public IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get; set; } = [];

        /// <summary>Namespaces the generated code imports (issue 239).</summary>
        public IReadOnlyList<string> Usings { get; } = usings;

        /// <summary>The grammar files on disk, by the configured patterns (<c>dir/*.ngr</c> or plain paths).</summary>
        public List<string> Files() => Matching(patterns);

        /// <summary>The helper C# files on disk (issue 239).</summary>
        public List<string> SourceFiles() => Matching(sources);

        List<string> Matching(string[] globs) => globs
            .SelectMany(pattern =>
            {
                string full = Path.GetFullPath(Path.Combine(root, pattern));
                string directory = Path.GetDirectoryName(full)!;
                return Directory.Exists(directory) ? Directory.GetFiles(directory, Path.GetFileName(full)) : [];
            })
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads <c>nitrogen.json</c> at <paramref name="root"/> and compiles its languages; the documents they now serve.</summary>
    public IReadOnlyList<string> ConfigureWorkspace(string root)
    {
        _root = Path.GetFullPath(root);
        return LoadConfiguration();
    }

    /// <summary>A file changed on disk (<c>workspace/didChangeWatchedFiles</c>): <c>nitrogen.json</c> or a closed grammar.</summary>
    public IReadOnlyList<string> FileChanged(string path)
    {
        path = Path.GetFullPath(path);
        if (_root is not null && path == Path.Combine(_root, "nitrogen.json")) return LoadConfiguration();
        return _documents.ContainsKey(new Uri(path).AbsoluteUri) ? [] : GrammarHookPath(path);
    }

    IReadOnlyList<string> LoadConfiguration()
    {
        var affected = new List<string>();
        foreach (var language in _grammarLanguages)
        {
            affected.AddRange(Unregister(language));
            language.Snapshot?.Dispose();
        }
        _grammarLanguages.Clear();
        string file = Path.Combine(_root!, "nitrogen.json");
        if (!File.Exists(file)) return affected;

        using var json = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var entry in json.RootElement.GetProperty("languages").EnumerateArray())
        {
            var styles = new Dictionary<string, SymbolStyle>();
            if (entry.TryGetProperty("tokens", out var tokens))
                foreach (var style in tokens.EnumerateObject()) styles[style.Name] = Style(style.Value.GetString() ?? "");
            var language = new GrammarLanguage(entry.GetProperty("name").GetString()!, Strings(entry, "extensions"),
                entry.GetProperty("start").GetString()!, _root!, Strings(entry, "grammars"),
                entry.TryGetProperty("sources", out _) ? Strings(entry, "sources") : [],
                entry.TryGetProperty("usings", out _) ? Strings(entry, "usings") : [], new Presentation(styles));
            _grammarLanguages.Add(language);
            affected.AddRange(Compile(language));
        }
        return affected.Distinct().ToList();
    }

    IReadOnlyList<string> GrammarHook(string uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? GrammarHookPath(Path.GetFullPath(parsed.LocalPath)) : [];

    IReadOnlyList<string> GrammarHookPath(string path) =>
        _grammarLanguages.FirstOrDefault(l => l.Files().Contains(path, StringComparer.Ordinal) || l.SourceFiles().Contains(path, StringComparer.Ordinal)) is { } language ? Compile(language) : [];

    IReadOnlyList<string> Compile(GrammarLanguage language)
    {
        var files = language.Files();
        foreach (string gone in language.Workspace.Paths.Except(files, StringComparer.Ordinal).ToList()) language.Workspace.RemoveGrammar(gone);
        foreach (string file in files)
            language.Workspace.SetGrammar(file, _documents.TryGetValue(new Uri(file).AbsoluteUri, out var open) ? open.Text : File.ReadAllText(file));

        language.Workspace.Sources.Clear();
        foreach (string source in language.SourceFiles()) language.Workspace.Sources[source] = File.ReadAllText(source);
        language.Workspace.Usings.Clear();
        foreach (string u in language.Usings) language.Workspace.Usings.Add(u);
        var snapshot = language.Workspace.Compile();
        var diagnostics = snapshot.Diagnostics.ToList();
        Rule? start = snapshot.Succeeded ? snapshot.FindRule(language.Start) : null;
        if (snapshot.Succeeded && start is null)
            diagnostics.Add(new WorkspaceDiagnostic(files.FirstOrDefault() ?? "", 1, 1, "NGR0000", $"no rule '{language.Start}' to start {language.Name} files", true));
        language.Diagnostics = diagnostics;
        var affected = files.Select(f => new Uri(f).AbsoluteUri).Where(_documents.ContainsKey).ToList();
        if (start is not { } rule)
        {
            snapshot.Dispose();
            return affected;
        }

        var entry = new LanguageEntry(language.Name, snapshot.Language!, language.Extensions.ToDictionary(e => e, _ => rule), language.Presentation);
        affected.AddRange(Reregister(language.Entry, entry));
        language.Entry = entry;
        language.Snapshot?.Dispose();
        language.Snapshot = snapshot;
        return affected;
    }

    /// <summary>Serves <paramref name="replacement"/> in place of <paramref name="old"/>: its documents and any unserved ones it now covers are re-parsed.</summary>
    IReadOnlyList<string> Reregister(LanguageEntry? old, LanguageEntry replacement)
    {
        if (old is not null) Registry.Remove(old);
        Registry.Add(replacement);
        var previous = old is null ? [] : _documents.Values.Where(d => d.Language == old).ToList();
        var texts = previous.Select(d => (d.Uri, d.Version, d.Text)).ToList();
        foreach (var (uri, pending) in _unserved.ToList())
            if (Registry.TryFind(uri, out var served, out _) && served == replacement) texts.Add((uri, pending.Version, pending.Text));

        var project = new Project(replacement.Language);
        _projects[replacement] = project;
        foreach (var (uri, version, text) in texts)
        {
            _inspection.Remove(uri);
            Registry.TryFind(uri, out _, out var start);
            var document = new Document(uri, version, text, replacement, start);
            project.Set(uri, document.Parsed.Tree);
            _documents[uri] = document;
            _unserved.Remove(uri);
        }
        foreach (var document in previous) document.Dispose(); // after their replacements are bound
        if (old is not null) _projects.Remove(old);
        return texts.Select(t => t.Uri).ToList();
    }

    IReadOnlyList<string> Unregister(GrammarLanguage language)
    {
        if (language.Entry is not { } entry) return [];
        Registry.Remove(entry);
        var moved = _documents.Values.Where(d => d.Language == entry).ToList();
        foreach (var document in moved)
        {
            _inspection.Remove(document.Uri);
            _documents.Remove(document.Uri);
            _unserved[document.Uri] = (document.Version, document.Text);
            document.Dispose();
        }
        _projects.Remove(entry);
        return moved.Select(d => d.Uri).ToList();
    }

    IEnumerable<ServiceDiagnostic> GrammarDiagnostics(string uri)
    {
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) yield break;
        string path = Path.GetFullPath(parsed.LocalPath);
        foreach (var language in _grammarLanguages)
            foreach (var diagnostic in language.Diagnostics)
                if (diagnostic.Path == path)
                {
                    var at = new DocumentPosition(Math.Max(diagnostic.Line - 1, 0), Math.Max(diagnostic.Column - 1, 0));
                    yield return new ServiceDiagnostic(new DocumentRange(at, at),
                        diagnostic.IsError ? ServiceSeverity.Error : ServiceSeverity.Warning, diagnostic.Code, diagnostic.Message);
                }
    }

    void DisposeGrammars()
    {
        foreach (var language in _grammarLanguages) language.Snapshot?.Dispose();
        _grammarLanguages.Clear();
    }

    static string[] Strings(JsonElement entry, string property) =>
        entry.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>A token type named as LSP names it (<c>class</c>, <c>enumMember</c>); its outline icon follows.</summary>
    static SymbolStyle Style(string tokenType)
    {
        var token = Enum.TryParse<TokenType>(tokenType, ignoreCase: true, out var parsed) ? parsed : TokenType.Variable;
        var outline = token switch
        {
            TokenType.Class or TokenType.Type or TokenType.Struct => OutlineKind.Class,
            TokenType.Function => OutlineKind.Function,
            TokenType.Method => OutlineKind.Method,
            TokenType.Property => OutlineKind.Property,
            TokenType.EnumMember => OutlineKind.EnumMember,
            TokenType.Namespace => OutlineKind.Module,
            TokenType.Event => OutlineKind.Event,
            _ => OutlineKind.Variable,
        };
        return new SymbolStyle(token, outline);
    }
}
