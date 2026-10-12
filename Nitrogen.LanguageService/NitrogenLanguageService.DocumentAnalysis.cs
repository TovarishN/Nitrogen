using System.Diagnostics.CodeAnalysis;
using Nitrogen.Binding;
using Nitrogen.Grammar;
using Nitrogen.Workspace;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    readonly DocumentAnalysisSession _documentAnalysis = new();
    readonly Dictionary<string, DocumentInput> _signatureInputs = new(StringComparer.Ordinal);
    readonly Dictionary<string, DocumentAnalysisResult> _signatureResults = new(StringComparer.Ordinal);
    readonly Dictionary<string, (LanguageEntry Entry, Rule Start)> _signaturePrimaries = new(StringComparer.Ordinal);
    readonly Dictionary<string, LanguageEntry> _signatureEntries = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _signaturePrimaryLayers = new(StringComparer.Ordinal);
    readonly Dictionary<string, LanguageDefinition> _signatureDefinitions = new(StringComparer.Ordinal);
    readonly HashSet<string> _signatureOwnedOpen = new(StringComparer.Ordinal);
    InputSnapshot? _signatureSnapshot;
    string[]? _signatureRoots;

    internal IReadOnlyList<string> SetSignatureRoots(IEnumerable<string> roots)
    {
        _signatureRoots = roots.Select(Path.GetFullPath).Distinct().ToArray();
        return _root is null ? [] : RefreshSignedDocuments();
    }
    bool _serveSignatureDocuments = true;

    public void ServeSignatureDocuments(bool enabled) => _serveSignatureDocuments = enabled;

    IReadOnlyList<string> UpdateWithSignatures(string uri, int version, string text)
    {
        if (System.Uri.TryCreate(uri, UriKind.Absolute, out var file) && (file.IsFile || file.Scheme == "untitled") && !IsEmbedded(uri))
        {
            if (_signatureInputs.TryGetValue(uri, out var previous) && previous.Version > version) return [];
            string path = file.IsFile ? Path.GetFullPath(file.LocalPath) : UntitledPath(uri);
            _signatureInputs[uri] = new(path, text, version);
        }
        var affected = RefreshSignedDocuments();
        var core = Update(uri, version, text);
        return Visible(core.Concat(affected).Concat(_signatureOwnedOpen.Contains(uri) ? [uri] : []));
    }

    public IReadOnlyList<string> Close(string uri)
    {
        _signatureInputs.Remove(uri);
        _signatureOwnedOpen.Remove(uri);
        var affected = CloseCore(uri);
        return Visible(affected.Concat(RefreshSignedDocuments()));
    }

    IReadOnlyList<string> RefreshSignedDocuments()
    {
        if (!_serveSignatureDocuments) return [];
        var configuredRoots = _signatureRoots ?? (_root is null ? [] : new[] { _root });
        string? root = configuredRoots.FirstOrDefault() ?? _signatureInputs.Values.Select(i => Path.GetDirectoryName(i.Path)).FirstOrDefault() ?? _signatureSnapshot?.Root;
        if (root is null) return [];
        var roots = _signatureRoots ?? (_root is null ? new[] { root } : configuredRoots);
        if (_signatureRoots is null && _root is null && _signatureInputs.Keys.All(uri => uri.StartsWith("untitled:", StringComparison.Ordinal))) roots = [];
        foreach (string uri in _signatureInputs.Keys.Where(uri => uri.StartsWith("untitled:", StringComparison.Ordinal)).ToArray())
            _signatureInputs[uri] = _signatureInputs[uri] with { Path = UntitledPath(uri) };
        var files = new Dictionary<string, DocumentInput>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string folder in roots)
            foreach (var pair in WorkspaceInputReader.Read(folder, _signatureInputs.Values).Files)
                if (files.ContainsKey(pair.Key) || files.Count < WorkspaceInputReader.MaxFiles) files[pair.Key] = pair.Value;
        foreach (var overlay in _signatureInputs.Values) files[overlay.Path] = overlay;
        var inputs = new InputSnapshot(root, files, false, roots);
        var oldEntries = _signatureEntries.Values.ToArray();
        var previousUris = _signatureResults.Keys.Concat(_signaturePrimaries.Keys).ToHashSet(StringComparer.Ordinal);
        _signatureResults.Clear();
        _signaturePrimaryLayers.Clear();
        _signatureDefinitions.Clear();
        var oldPrimaries = _signaturePrimaries.ToDictionary(p => p.Key, p => p.Value);
        _signaturePrimaries.Clear();
        _signatureSnapshot = inputs;
        bool augmentation = roots.Select(folder => inputs.Files.GetValueOrDefault(Path.Combine(folder, "nitrogen.json"))).OfType<DocumentInput>()
            .Any(config => AugmentationConfiguration.Parse(config.Path, config.Text).Rules.Count > 0);
        var declaredExtensions = inputs.Files.Values.Where(input => roots.Any(folder => DefinitionPaths.Under(input.Path, folder)) &&
                DocumentSignatures.Read(input.Text).Signatures.Any(signature => signature.Mode == SignatureMode.Grammar))
            .SelectMany(input => GrammarParser.ParseDeclaration(DocumentSignatures.Read(input.Text).MaskedText).File?.Languages.SelectMany(language => language.Extensions) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = inputs.Files.Values.Where(input => augmentation || HasSignature(input) || declaredExtensions.Contains(Path.GetExtension(input.Path)) || Path.GetFileName(input.Path) == "nitrogen.json").ToList();
        // Existing signed inputs continue to reclassify when their signature is removed.
        candidates.AddRange(_signatureInputs.Values.Where(i => previousUris.Contains(SignatureUri(i.Path))));
        foreach (var input in candidates.DistinctBy(i => i.Path))
        {
            string uri = SignatureUri(input.Path);
            var header = DocumentSignatures.Read(input.Text);
            bool needsLocation = uri.StartsWith("untitled:", StringComparison.Ordinal) && configuredRoots.Length != 1 &&
                (header.Signatures.Any(signature => signature.Reference is { IsPath: true }) ||
                 RegionExtractor.References(input).Any(reference => reference.IsPath) || header.Signatures.Any(signature => signature.Mode == SignatureMode.Grammar));
            var result = needsLocation
                ? new DocumentAnalysisResult(input.Path, input.Version,
                    [DefinitionPaths.Error(input.Path, "save this document to establish a location, or open exactly one workspace folder for relative language references")])
                : _documentAnalysis.AnalyzeAsync(input, inputs, CancellationToken.None).GetAwaiter().GetResult();
            if (!needsLocation && !_documentAnalysis.IsCurrent(result)) continue;
            if (!header.Signatures.Any(signature => signature.Mode == SignatureMode.Language) && result.Classification is { } classification)
            {
                var unowned = classification.Selections.Where(selection => selection.IsPrimary &&
                    (_skipped.Contains(selection.Definition.Name) || _skipped.Contains(Path.GetExtension(input.Path).TrimStart('.'))))
                    .Select(selection => selection.Key).ToHashSet(StringComparer.Ordinal);
                result = result with
                {
                    Diagnostics = result.Diagnostics.Where(issue => issue.Layer is null || !unowned.Contains(issue.Layer)).ToArray(),
                    Classification = classification with { Selections = classification.Selections.Where(selection => !unowned.Contains(selection.Key)).ToArray() },
                };
            }
            bool owned = result.Diagnostics.Count > 0 || result.Classification is { Selections.Count: > 0 } or { Regions.Count: > 0 } ||
                header.Signatures.Any(s => s.Mode == SignatureMode.Grammar);
            if (owned)
            {
                _signatureResults[uri] = result;
                if (_signatureInputs.ContainsKey(uri)) _signatureOwnedOpen.Add(uri);
            }
            if (header.Signatures.Any(s => s.Mode == SignatureMode.Grammar))
            {
                var grammar = Registry.Entries.FirstOrDefault(e => e.Name == "ngr");
                if (grammar is not null) _signaturePrimaries[uri] = (grammar, grammar.Starts.Values.First());
                if (_documentAnalysis.DefinitionFor(input.Path) is { } definition) _signatureDefinitions[definition.Id] = definition;
            }
            else if (result.Classification?.Selections.FirstOrDefault(s => s.IsPrimary) is { } primary &&
                _documentAnalysis.SnapshotFor(primary.Definition.Id) is { Language: { } language } snapshot &&
                snapshot.FindRule(primary.Definition.Start) is { } start)
            {
                _signatureDefinitions[primary.Definition.Id] = primary.Definition;
                _signaturePrimaryLayers[uri] = primary.Key;
                if (header.Signatures.Any(s => s.Mode == SignatureMode.Language) || !Registry.TryFind(uri, out _, out _))
                {
                    if (!_signatureEntries.TryGetValue(primary.Definition.Id, out var entry) || !ReferenceEquals(entry.Language, language))
                    {
                        var styles = primary.Definition.Presentation.Where(p => p.Category == "tokens").ToDictionary(p => p.Name, p => Style(p.Value));
                        var types = primary.Definition.Presentation.Where(p => p.Category == "types").ToDictionary(p => p.Name, p => Style(p.Value).Token);
                        entry = new LanguageEntry(primary.Definition.Name, language, new Dictionary<string, Rule>(), new Presentation(styles, types),
                            evaluation: snapshot.Evaluation, fixes: snapshot.Fixes, calls: snapshot.Calls);
                        _signatureEntries[primary.Definition.Id] = entry;
                        LanguagesVersion++;
                    }
                    _signaturePrimaries[uri] = (entry, start);
                }
            }
            foreach (var selection in result.Classification?.Regions.Select(r => r.Selection).Concat(result.Classification.Selections) ?? [])
                _signatureDefinitions[selection.Definition.Id] = selection.Definition;
        }
        foreach (var (uri, old) in oldPrimaries)
            if (!_signaturePrimaries.TryGetValue(uri, out var current) || current.Entry != old.Entry)
                DropSignedDocument(uri);
        foreach (var (uri, primary) in _signaturePrimaries)
        {
            string path = SignaturePath(uri)!;
            if (!inputs.Files.TryGetValue(path, out var input)) continue;
            if (_hosts.ContainsKey(uri)) CloseHost(uri);
            DropSignedDocument(uri);
            var document = new Document(uri, (int)input.Version, input.Text, primary.Entry, primary.Start);
            if (!_projects.TryGetValue(primary.Entry, out var project)) _projects[primary.Entry] = project = new Project(primary.Entry.Language);
            project.Set(uri, document.Parsed.Tree);
            if (_signatureInputs.ContainsKey(uri)) _documents[uri] = document;
            else _closed[uri] = document;
            _unserved.Remove(uri);
            _inspection.Remove(uri);
        }
        var activeEntries = _signaturePrimaries.Values.Select(primary => primary.Entry).ToHashSet();
        foreach (string id in _signatureEntries.Where(pair => !activeEntries.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            _signatureEntries.Remove(id);
        foreach (var entry in oldEntries.Where(entry => !activeEntries.Contains(entry)))
        {
            _projects.Remove(entry);
            if (_semantics.Remove(entry, out var semantics)) semantics.Dispose();
            _info.Remove(entry);
        }
        _documentAnalysis.RetainDefinitions(_signatureDefinitions.Keys);
        return previousUris.Concat(_signatureResults.Keys).Concat(_signatureResults.Values.SelectMany(r => r.Diagnostics)
            .Select(d => SignatureUri(d.Location.Path))).Distinct().ToList();
    }

    static bool HasSignature(DocumentInput input)
    {
        if (!input.Text.Contains("#!nitrogen", StringComparison.Ordinal)) return false;
        var header = DocumentSignatures.Read(input.Text);
        return header.Signatures.Count > 0 || header.Diagnostics.Count > 0 ||
            HostCommentExtractor.Read(input.Path, input.Text).Any(c => c.Text.Contains("#!nitrogen region", StringComparison.Ordinal));
    }

    string UntitledPath(string uri)
    {
        var roots = _signatureRoots ?? (_root is null ? [] : new[] { _root });
        string root = roots.Length == 1 ? roots[0] : Path.Combine(Path.GetTempPath(), "nitrogen-untitled");
        string name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri)));
        return Path.Combine(root, "Untitled-" + name + ".txt");
    }

    string SignatureUri(string path) => _signatureInputs.FirstOrDefault(pair => DefinitionPaths.Comparer.Equals(pair.Value.Path, path)).Key ?? new Uri(path).AbsoluteUri;

    string? SignaturePath(string uri) => _signatureInputs.TryGetValue(uri, out var input) ? input.Path
        : System.Uri.TryCreate(uri, UriKind.Absolute, out var file) && file.IsFile ? Path.GetFullPath(file.LocalPath) : null;

    void DropSignedDocument(string uri)
    {
        _inspection.Remove(uri);
        _hints.Remove(uri);
        if (_documents.Remove(uri, out var document))
        {
            if (_projects.TryGetValue(document.Language, out var project)) project.Remove(uri);
            document.Dispose();
        }
        RemoveClosed(uri);
    }

    bool IsGrammarDocument(string uri) => _signaturePrimaries.TryGetValue(uri, out var primary)
        ? primary.Entry.Name == "ngr" : Registry.TryFind(uri, out var entry, out _) && entry.Name == "ngr";

    bool TryLanguage(string uri, [NotNullWhen(true)] out LanguageEntry? entry, out Rule start)
    {
        if (_signaturePrimaries.TryGetValue(uri, out var primary))
        {
            entry = primary.Entry;
            start = primary.Start;
            return true;
        }
        return Registry.TryFind(uri, out entry, out start);
    }

    IReadOnlyList<ServiceDiagnostic> SignedDiagnostics(string uri)
    {
        if (SignaturePath(uri) is not { } path) return [];
        string? text = _signatureInputs.GetValueOrDefault(uri)?.Text ?? TextOf(uri) ?? _signatureSnapshot?.Files.GetValueOrDefault(path)?.Text;
        if (text is null) return [];
        var lines = new LineMap(text);
        return _signatureResults.Values.SelectMany(r => r.Diagnostics)
            .Where(d => d.Location.Path == path && (!_signaturePrimaryLayers.TryGetValue(uri, out var layer) || d.Layer != layer))
            .DistinctBy(d => (d.Code, d.Message, d.Location, d.Source))
            .Select(d => new ServiceDiagnostic(lines.RangeOf(new TextSpan(d.Location.Start, d.Location.Length)),
                d.IsError ? ServiceSeverity.Error : ServiceSeverity.Warning, d.Code, d.Message, d.Source,
                d.Related.Select(location => new DocumentLocation(SignatureUri(location.Path),
                    new LineMap(_signatureSnapshot?.Files.GetValueOrDefault(location.Path)?.Text ?? "").RangeOf(new TextSpan(location.Start, location.Length)))).ToArray()))
            .ToArray();
    }
}
