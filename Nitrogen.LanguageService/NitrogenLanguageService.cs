using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace Nitrogen.LanguageService;

/// <summary>An open document: its text, line map and current parse.</summary>
public sealed class Document : IDisposable
{
    internal Document(string uri, int version, string text, LanguageEntry language, Rule start)
    {
        Uri = uri;
        Version = version;
        Text = text;
        Language = language;
        Start = start;
        Lines = new LineMap(text);
        Parsed = language.Language.Parse(text, start);
    }

    public string Uri { get; }

    public int Version { get; }

    public string Text { get; }

    public LanguageEntry Language { get; }

    /// <summary>The start rule the document parses with.</summary>
    internal Rule Start { get; }

    public LineMap Lines { get; }

    public ParseResult Parsed { get; }

    public void Dispose() => Parsed.Dispose();
}

/// <summary>
/// The IDE surface as a pure API (issue 238). Documents are opened, changed (full text) and closed;
/// each language's open documents are bound together in one project. Positions are LSP's: 0-based
/// line, UTF-16 column. Not thread-safe: the LSP server calls it from one loop.
/// </summary>
public sealed partial class NitrogenLanguageService(LanguageRegistry registry) : IDisposable
{
    readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
    readonly Dictionary<LanguageEntry, Project> _projects = new();
    readonly Dictionary<string, (int Version, string Text)> _unserved = new(StringComparer.Ordinal);
    readonly Dictionary<LanguageEntry, ProjectSemantics> _semantics = new();
    readonly Dictionary<LanguageEntry, SemanticsInfo> _info = new();

    public LanguageRegistry Registry { get; } = registry;

    public bool IsOpen(string uri) => _documents.ContainsKey(uri) || _hosts.ContainsKey(uri);

    public int? VersionOf(string uri) =>
        _hosts.TryGetValue(uri, out var host) ? host.Version : _documents.TryGetValue(uri, out var document) ? document.Version : null;

    /// <returns>Every open document whose diagnostics may have changed: this one and the others of its language. Empty when nobody serves the file type.</returns>
    public IReadOnlyList<string> Open(string uri, int version, string text) => Visible(Update(uri, version, text));

    /// <inheritdoc cref="Open"/>
    public IReadOnlyList<string> Change(string uri, int version, string text) => Visible(Update(uri, version, text));

    /// <returns>The other open documents of the closed document's language, and a grammar's samples when a grammar closes.</returns>
    public IReadOnlyList<string> Close(string uri)
    {
        if (_hosts.ContainsKey(uri)) return Visible([.. CloseHost(uri), .. GrammarHook(uri)]);
        _inspection.Remove(uri);
        _hints.Remove(uri);
        _unserved.Remove(uri);
        var affected = new List<string>();
        if (_documents.Remove(uri, out var document))
        {
            _projects[document.Language].Remove(uri);
            document.Dispose();
            // An indexed workspace file goes back to its text on disk.
            if (System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile && InWorkspace(parsed.LocalPath) && File.Exists(parsed.LocalPath))
                LoadClosed(parsed.LocalPath);
            affected.AddRange(OpenDocuments(document.Language));
        }
        affected.AddRange(GrammarHook(uri)); // a closed grammar goes back to its text on disk
        return Visible(affected);
    }

    public IReadOnlyList<ServiceDiagnostic> Diagnostics(string uri)
    {
        if (_hosts.TryGetValue(uri, out var host)) return HostDiagnostics(host);
        if (!_documents.TryGetValue(uri, out var document)) return [];
        var diagnostics = new List<ServiceDiagnostic>();
        var parsed = document.Parsed;
        foreach (var diagnostic in parsed.Diagnostics)
            diagnostics.Add(new ServiceDiagnostic(document.Lines.RangeOf(diagnostic.Span), Severity(diagnostic.Severity),
                diagnostic.Code.ToString(), parsed.FormatMessage(diagnostic)));
        foreach (var diagnostic in _projects[document.Language].Diagnostics(uri))
            diagnostics.Add(new ServiceDiagnostic(document.Lines.RangeOf(diagnostic.Span), ServiceSeverity.Error, diagnostic.Code, diagnostic.Message));
        if (InfoOf(document.Language).HasSemantics)
            foreach (var diagnostic in SemanticsOf(document.Language)[uri].Diagnostics())
                diagnostics.Add(new ServiceDiagnostic(document.Lines.RangeOf(diagnostic.Span),
                    diagnostic.Code.StartsWith("NS000", StringComparison.Ordinal) ? ServiceSeverity.Warning : ServiceSeverity.Error,
                    diagnostic.Code, diagnostic.Message));
        diagnostics.AddRange(LoweringDiagnostics(document, diagnostics));
        if (!IsEmbedded(uri)) diagnostics.AddRange(GrammarDiagnostics(uri));
        if (AssistOf(document) is { } assisted)
            foreach (var diagnostic in document.Language.Assist!.Diagnostics(assisted))
                diagnostics.Add(new ServiceDiagnostic(document.Lines.RangeOf(diagnostic.Span), diagnostic.Severity, diagnostic.Code, diagnostic.Message));
        return diagnostics;
    }

    /// <summary>The document's semantic tokens in text order; empty when it is not open.</summary>
    public IReadOnlyList<SemanticToken> SemanticTokens(string uri) =>
        _hosts.TryGetValue(uri, out var host) ? HostTokens(host)
        : _documents.TryGetValue(uri, out var document)
            ? SemanticTokenBuilder.Build(document, _projects[document.Language],
                document.Language.Presentation.ColorFromLowering ? LoweredRoots(document) : [],
                AssistOf(document) is { } assisted ? document.Language.Assist!.Tokens(assisted) : null)
            : [];

    /// <summary>The document's outline; empty when it is not open.</summary>
    public IReadOnlyList<OutlineSymbol> DocumentSymbols(string uri) =>
        _documents.TryGetValue(uri, out var document) ? OutlineBuilder.Build(document, _projects[document.Language]) : [];

    /// <summary>Where the name at <paramref name="position"/> is declared; built-ins have no location.</summary>
    public IReadOnlyList<DocumentLocation> Definition(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri)) return Into(uri, position) is { } inner ? Definition(inner.Uri, inner.Position) : [];
        return NameAt(uri, position) is { } name ? name.Symbols.Where(s => !s.IsBuiltin).Select(LocationOf).Select(OutOf).ToList() : [];
    }

    /// <summary>Every reference to what the name at <paramref name="position"/> names, across the project; declarations first when asked.</summary>
    public IReadOnlyList<DocumentLocation> References(string uri, DocumentPosition position, bool includeDeclaration)
    {
        if (_hosts.ContainsKey(uri)) return Into(uri, position) is { } inner ? References(inner.Uri, inner.Position, includeDeclaration) : [];
        if (NameAt(uri, position) is not { } name) return [];
        var locations = new List<DocumentLocation>();
        foreach (var symbol in name.Symbols)
        {
            if (includeDeclaration && !symbol.IsBuiltin) locations.Add(LocationOf(symbol));
            locations.AddRange(name.Project.ReferencesTo(symbol).Select(LocationOf));
        }
        return locations.Select(OutOf).Distinct().ToList();
    }

    /// <summary>The occurrences in this document: the declaration written, the references read.</summary>
    public IReadOnlyList<DocumentHighlight> Highlights(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri))
            return Into(uri, position) is { } inner
                ? Highlights(inner.Uri, inner.Position).Select(h => h with { Range = OutOf(inner.Uri, h.Range) }).ToList()
                : [];
        if (NameAt(uri, position) is not { } name) return [];
        var highlights = new List<DocumentHighlight>();
        foreach (var symbol in name.Symbols)
        {
            if (symbol.Path == uri) highlights.Add(new DocumentHighlight(name.Document.Lines.RangeOf(symbol.NameSpan), HighlightKind.Write));
            foreach (var reference in name.Project.ReferencesTo(symbol))
                if (reference.Path == uri) highlights.Add(new DocumentHighlight(name.Document.Lines.RangeOf(reference.NameSpan), HighlightKind.Read));
        }
        return highlights;
    }

    /// <summary>
    /// What the name at <paramref name="position"/> is, with its type when the language has one; over
    /// any other node, the type of the innermost node that defines the hover property. Null otherwise.
    /// </summary>
    public HoverInfo? Hover(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri))
            return Into(uri, position) is { } inner && Hover(inner.Uri, inner.Position) is { } found
                ? found with { Range = OutOf(inner.Uri, found.Range) }
                : null;
        if (_documents.TryGetValue(uri, out var document) && AssistOf(document) is { } assisted &&
            document.Language.Assist!.Hover(assisted, document.Lines.OffsetOf(position)) is { } help)
            return new HoverInfo(help.Markdown, document.Lines.RangeOf(help.Span));
        var existing = HoverCore(uri, position);
        var inspection = Inspect(uri, position);
        if (inspection is null) return existing;
        // What the node lowers to says more than its hover property, which a name's hover keeps.
        bool atName = NameAt(uri, position) is { Symbols.Count: > 0 };
        if (!atName) existing = null;
        var summary = inspection.Node switch
        {
            HirOperation operation => $"`{inspection.Type}` · `{operation.Signature.Id}`",
            HirConstant constant => $"`{inspection.Type}` = {constant.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            HirSymbolRef symbol => $"`{inspection.Type}` · `{symbol.Symbol.Id}`",
            _ => $"`{inspection.Type}`",
        };
        if (!ReferenceEquals(inspection.Root, inspection.Node) && inspection.Root is HirOperation root)
            summary += $" · `{root.Signature.Id}`";
        if (HoverValue(_documents[uri], inspection, atName) is { } value) summary += "\n\n" + value;
        return new HoverInfo(existing is null ? summary : existing.Markdown + "\n\n" + summary,
            existing?.Range ?? inspection.Range);
    }

    HoverInfo? HoverCore(string uri, DocumentPosition position)
    {
        if (!_documents.TryGetValue(uri, out var document)) return null;
        var info = InfoOf(document.Language);
        var semantics = info.Hover is null ? null : SemanticsOf(document.Language)[uri];
        if (NameAt(uri, position) is { } name && name.Symbols.Count > 0)
        {
            string text = string.Join("\n\n", name.Symbols.Select(symbol =>
            {
                string head = $"`{symbol.Kind} {symbol.Name}`" + (SymbolTypeOf(document, symbol) is { } type ? $" : {type}" : "");
                if (symbol.IsBuiltin) return $"{head} — built-in";
                var at = OutOf(LocationOf(symbol));
                return $"{head} — declared in {FileName(at.Uri)} line {at.Range.Start.Line + 1}";
            }));
            return new HoverInfo(text, name.Document.Lines.RangeOf(name.Span));
        }
        if (semantics is null) return null;
        int offset = document.Lines.OffsetOf(position), best = -1;
        for (int node = 0; node < semantics.Tree.NodeCount; node++)
        {
            var span = semantics.Tree.Span(node);
            if (span.Start <= offset && offset < span.End && semantics.Defines(node, info.Hover!)
                && (best < 0 || span.Length <= semantics.Tree.Span(best).Length))
                best = node;
        }
        if (best < 0) return null;
        string shown = info.Hover!.Read(semantics, best)?.ToString() ?? "";
        if (info.Constant?.Read(semantics, best) is float constant) shown += " = " + constant.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new HoverInfo($"`{shown}`", document.Lines.RangeOf(semantics.Tree.Span(best)));
    }

    (Document Document, Project Project, TextSpan Span, IReadOnlyList<Symbol> Symbols)? NameAt(string uri, DocumentPosition position)
    {
        if (!_documents.TryGetValue(uri, out var document)) return null;
        var project = _projects[document.Language];
        return project.NameAt(uri, document.Lines.OffsetOf(position)) is { } found ? (document, project, found.Span, found.Symbols) : null;
    }

    DocumentLocation LocationOf(Symbol symbol) => new(symbol.Path!, DocumentAt(symbol.Path!).Lines.RangeOf(symbol.NameSpan));

    DocumentLocation LocationOf(Reference reference) => new(reference.Path, DocumentAt(reference.Path).Lines.RangeOf(reference.NameSpan));

    static string FileName(string uri) => uri[(uri.LastIndexOf('/') + 1)..];

    /// <summary>Names visible at the cursor of the kinds the grammar allows there (those of the expected type first), then the keywords it expects; empty when not open.</summary>
    public IReadOnlyList<CompletionItem> Completion(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri))
            return Into(uri, position) is { } inner
                ? Completion(inner.Uri, inner.Position).Select(i => i with { Replace = OutOf(inner.Uri, i.Replace) }).ToList()
                : [];
        if (!_documents.TryGetValue(uri, out var document)) return [];
        var info = InfoOf(document.Language);
        var semantics = SemanticFile(document);
        int offset = document.Lines.OffsetOf(position);
        var items = CompletionBuilder.Build(document, _projects[document.Language], semantics, info, offset);
        if (AssistOf(document) is not { } assisted) return items;
        var assistedItems = document.Language.Assist!.Completion(assisted, offset)
            .Select(i => new CompletionItem(i.Label, i.Kind, i.Detail, document.Lines.RangeOf(i.Replace)))
            .DistinctBy(i => i.Label).ToList();
        var labels = assistedItems.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
        return assistedItems.Concat(items.Where(i => !labels.Contains(i.Label)))
            .Select((item, i) => item with { SortText = i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) }).ToList();
    }

    /// <summary>The name a rename at <paramref name="position"/> would change; throws <see cref="RenameRefusedException"/> when it cannot.</summary>
    public DocumentRange PrepareRename(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri))
            return Into(uri, position) is { } inner ? OutOf(inner.Uri, PrepareRename(inner.Uri, inner.Position))
                : throw new RenameRefusedException("Nothing to rename here.");
        var (document, _, span, _) = RenameTarget(uri, position);
        return document.Lines.RangeOf(span);
    }

    /// <summary>
    /// Renames what the name at <paramref name="position"/> declares or refers to: its declaration and
    /// every reference in the project. Refuses (<see cref="RenameRefusedException"/>) a new name that is
    /// not a name there, or that is already visible with the same kind where the symbol is declared or used.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>> Rename(string uri, DocumentPosition position, string newName)
    {
        if (_hosts.ContainsKey(uri))
            return Into(uri, position) is { } inner ? Rename(inner.Uri, inner.Position, newName)
                : throw new RenameRefusedException("Nothing to rename here.");
        var (_, project, _, symbol) = RenameTarget(uri, position);
        CheckName(symbol, newName);
        var uses = project.ReferencesTo(symbol);
        foreach (var (path, offset) in uses.Select(r => (r.Path, r.NameSpan.Start)).Prepend((symbol.Path!, symbol.NameSpan.Start)))
            if (project.VisibleAt(path, offset, symbol.Kind).Any(s => s.Name == newName && s != symbol))
                throw new RenameRefusedException($"'{newName}' already names a {symbol.Kind} visible where '{symbol.Name}' is used.");

        var edits = new Dictionary<string, List<DocumentEdit>>(StringComparer.Ordinal);
        void Edit(string path, TextSpan span)
        {
            if (!edits.TryGetValue(path, out var list)) edits[path] = list = new List<DocumentEdit>();
            var range = DocumentAt(path).Lines.RangeOf(span);
            if (!list.Exists(e => e.Range == range)) list.Add(new DocumentEdit(range, newName));
        }
        Edit(symbol.Path!, symbol.NameSpan);
        foreach (var reference in uses) Edit(reference.Path, reference.NameSpan);
        // An edit in a tagged string is an edit of its C# host.
        return edits.SelectMany(e => e.Value.Select(edit => _embedded.ContainsKey(e.Key)
                ? (Uri: _embedded[e.Key].Host.Uri, Edit: edit with { Range = OutOf(e.Key, edit.Range) })
                : (Uri: e.Key, Edit: edit)))
            .GroupBy(e => e.Uri, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DocumentEdit>)g.Select(e => e.Edit).ToList(), StringComparer.Ordinal);
    }

    (Document Document, Project Project, TextSpan Span, Symbol Symbol) RenameTarget(string uri, DocumentPosition position)
    {
        if (NameAt(uri, position) is not { } name) throw new RenameRefusedException("Nothing to rename here.");
        string text = name.Document.Text.Substring(name.Span.Start, name.Span.Length);
        if (name.Symbols.Count == 0) throw new RenameRefusedException($"'{text}' is not declared anywhere.");
        if (name.Symbols.Count > 1) throw new RenameRefusedException($"'{text}' is ambiguous: {name.Symbols.Count} declarations share it.");
        var symbol = name.Symbols[0];
        if (symbol.IsBuiltin) throw new RenameRefusedException($"'{symbol.Name}' is built in.");
        return (name.Document, name.Project, name.Span, symbol);
    }

    /// <summary>The new name must re-parse and re-bind as the same declaration: same kind, same place, that name.</summary>
    void CheckName(Symbol symbol, string newName)
    {
        var document = DocumentAt(symbol.Path!);
        if (newName.Length > 0)
        {
            string text = document.Text[..symbol.NameSpan.Start] + newName + document.Text[symbol.NameSpan.End..];
            using var parsed = document.Language.Language.Parse(text, document.Start);
            var binding = FileBinding.Bind(symbol.Path!, parsed.Tree);
            if (binding.Declarations.Any(d => d.Kind == symbol.Kind && d.Name == newName && d.NameSpan.Start == symbol.NameSpan.Start))
                return;
        }
        throw new RenameRefusedException($"'{newName}' is not a valid {symbol.Kind} name.");
    }

    /// <summary>The open document, for features that read its tree.</summary>
    internal bool TryGet(string uri, out Document document) => _documents.TryGetValue(uri, out document!);

    internal Project ProjectOf(Document document) => _projects[document.Language];

    IReadOnlyList<string> Update(string uri, int version, string text)
    {
        if (IsHost(uri)) return [.. UpdateHost(uri, version, text), .. GrammarHook(uri)];
        _inspection.Remove(uri);
        if (!Registry.TryFind(uri, out var language, out var start))
        {
            _unserved[uri] = (version, text); // served as soon as a workspace grammar for it compiles
            return [];
        }
        var document = new Document(uri, version, text, language, start);
        if (!_projects.TryGetValue(language, out var project)) _projects[language] = project = new Project(language.Language);
        project.Set(uri, document.Parsed.Tree);
        if (_documents.Remove(uri, out var previous)) previous.Dispose();
        _documents[uri] = document;
        if (_closed.Remove(uri, out var closed)) closed.Dispose(); // the editor's text now stands for the file
        var affected = OpenDocuments(language).ToList();
        affected.AddRange(GrammarHook(uri));
        return affected.Distinct().ToList();
    }

    AssistDocument? AssistOf(Document document) =>
        document.Language.Assist is null ? null : new AssistDocument(document.Uri, document.Text, document.Parsed.Tree, CatalogFor(document.Uri));

    /// <summary>The document's semantics when its language has any: semantic rules, or a catalog to type and lower with.</summary>
    FileSemantics? SemanticFile(Document document) =>
        InfoOf(document.Language).HasSemantics || document.Language.Language.SemanticCatalog.Operations.Count > 0
            ? SemanticsOf(document.Language)[document.Uri] : null;

    /// <summary>A symbol's type: its semantic symbol property named like the hover one, else the type its declaring clause gives it.</summary>
    internal object? SymbolTypeOf(Document document, Symbol symbol) =>
        SemanticFile(document) is { } semantics
            ? InfoOf(document.Language).SymbolType?.Read(semantics, symbol) ?? semantics.DeclarativeTypes.TypeOfSymbol(symbol)
            : null;

    SemanticsInfo InfoOf(LanguageEntry language)
    {
        if (!_info.TryGetValue(language, out var info)) _info[language] = info = SemanticsInfo.Of(language.Language);
        return info;
    }

    /// <summary>The semantics beside the language's current project; a replaced project gets new semantics.</summary>
    ProjectSemantics SemanticsOf(LanguageEntry language)
    {
        var project = _projects[language];
        if (!_semantics.TryGetValue(language, out var semantics) || semantics.Project != project)
            _semantics[language] = semantics = new ProjectSemantics(project);
        return semantics;
    }

    IReadOnlyList<string> OpenDocuments(LanguageEntry language) =>
        _documents.Values.Where(d => d.Language == language).Select(d => d.Uri).ToList();

    static ServiceSeverity Severity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => ServiceSeverity.Error,
        DiagnosticSeverity.Warning => ServiceSeverity.Warning,
        _ => ServiceSeverity.Information,
    };

    public void Dispose()
    {
        _inspection.Clear();
        DisposeHosts();
        foreach (var document in _documents.Values) document.Dispose();
        _documents.Clear();
        DisposeClosed();
        DisposeGrammars();
        DisposeClosedGrammars();
    }
}
