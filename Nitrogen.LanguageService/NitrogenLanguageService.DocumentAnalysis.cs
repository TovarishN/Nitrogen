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
    bool _serveSignatureDocuments = true;

    public void ServeSignatureDocuments(bool enabled) => _serveSignatureDocuments = enabled;

    IReadOnlyList<string> UpdateWithSignatures(string uri, int version, string text)
    {
        if (System.Uri.TryCreate(uri, UriKind.Absolute, out var file) && file.IsFile && !IsEmbedded(uri))
        {
            if (_signatureInputs.TryGetValue(uri, out var previous) && previous.Version > version) return [];
            _signatureInputs[uri] = new(Path.GetFullPath(file.LocalPath), text, version);
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
        string? root = _root ?? _signatureInputs.Values.Select(i => Path.GetDirectoryName(i.Path)).FirstOrDefault();
        if (root is null) return [];
        var inputs = WorkspaceInputReader.Read(root, _signatureInputs.Values);
        var previousUris = _signatureResults.Keys.Concat(_signaturePrimaries.Keys).ToHashSet(StringComparer.Ordinal);
        _signatureResults.Clear();
        _signaturePrimaryLayers.Clear();
        _signatureDefinitions.Clear();
        var oldPrimaries = _signaturePrimaries.ToDictionary(p => p.Key, p => p.Value);
        _signaturePrimaries.Clear();
        _signatureSnapshot = inputs;
        var config = inputs.Files.GetValueOrDefault(Path.Combine(root, "nitrogen.json"));
        bool augmentation = config is not null && AugmentationConfiguration.Parse(config.Path, config.Text).Rules.Count > 0;
        var candidates = inputs.Files.Values.Where(input => augmentation || HasSignature(input)).ToList();
        // Existing signed inputs continue to reclassify when their signature is removed.
        candidates.AddRange(_signatureInputs.Values.Where(i => previousUris.Contains(new Uri(i.Path).AbsoluteUri)));
        foreach (var input in candidates.DistinctBy(i => i.Path))
        {
            string uri = new Uri(input.Path).AbsoluteUri;
            var result = _documentAnalysis.AnalyzeAsync(input, inputs, CancellationToken.None).GetAwaiter().GetResult();
            if (!_documentAnalysis.IsCurrent(result)) continue;
            var header = DocumentSignatures.Read(input.Text);
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
            string path = new Uri(uri).LocalPath;
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
        return previousUris.Concat(_signatureResults.Keys).Concat(_signatureResults.Values.SelectMany(r => r.Diagnostics)
            .Select(d => new Uri(d.Location.Path).AbsoluteUri)).Distinct().ToList();
    }

    static bool HasSignature(DocumentInput input)
    {
        if (!input.Text.Contains("#!nitrogen", StringComparison.Ordinal)) return false;
        var header = DocumentSignatures.Read(input.Text);
        return header.Signatures.Count > 0 || header.Diagnostics.Count > 0 ||
            HostCommentExtractor.Read(input.Path, input.Text).Any(c => c.Text.Contains("#!nitrogen region", StringComparison.Ordinal));
    }

    void DropSignedDocument(string uri)
    {
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
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var file) || !file.IsFile) return [];
        string path = Path.GetFullPath(file.LocalPath);
        string? text = TextOf(uri) ?? _signatureSnapshot?.Files.GetValueOrDefault(path)?.Text;
        if (text is null) return [];
        var lines = new LineMap(text);
        return _signatureResults.Values.SelectMany(r => r.Diagnostics)
            .Where(d => d.Location.Path == path && (!_signaturePrimaryLayers.TryGetValue(uri, out var layer) || d.Layer != layer))
            .DistinctBy(d => (d.Code, d.Message, d.Location, d.Source))
            .Select(d => new ServiceDiagnostic(lines.RangeOf(new TextSpan(d.Location.Start, d.Location.Length)),
                d.IsError ? ServiceSeverity.Error : ServiceSeverity.Warning, d.Code, d.Message, d.Source,
                d.Related.Select(location => new DocumentLocation(new Uri(location.Path).AbsoluteUri,
                    new LineMap(_signatureSnapshot?.Files.GetValueOrDefault(location.Path)?.Text ?? "").RangeOf(new TextSpan(location.Start, location.Length)))).ToArray()))
            .ToArray();
    }
}
