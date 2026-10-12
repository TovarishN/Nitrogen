using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

/// <summary>Indexes declared language identities from the supplied snapshot, never from ambient files.</summary>
public sealed class WorkspaceLanguageIndex
{
    readonly object _gate = new();
    Dictionary<string, string[]> _names = new(StringComparer.Ordinal);
    IReadOnlyList<AnalysisIssue> _diagnostics = [];
    public IReadOnlyList<AnalysisIssue> Diagnostics { get { lock (_gate) return _diagnostics; } }

    public void Update(InputSnapshot inputs)
    {
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var issues = new List<AnalysisIssue>();
        foreach (var input in inputs.Files.Values.DistinctBy(i => DefinitionPaths.Canonical(i.Path), DefinitionPaths.Comparer))
        {
            if (!input.Text.Contains("#!nitrogen", StringComparison.Ordinal)) continue;
            var header = DocumentSignatures.Read(input.Text);
            if (!header.Signatures.Any(s => s.Mode == SignatureMode.Grammar)) continue;
            foreach (var diagnostic in header.Diagnostics) Add(diagnostic, input.Path);
            if (header.Diagnostics.Any(d => d.Severity == GrammarSeverity.Error)) continue;
            // A valid declaration retains identity through a broken module body, but not through a name removal.
            var parsed = GrammarParser.ParseDeclaration(header.MaskedText);
            foreach (var diagnostic in parsed.Diagnostics) Add(diagnostic, input.Path);
            if (parsed.File?.Languages.FirstOrDefault() is not { } declaration) continue;
            if (!names.TryGetValue(declaration.Name, out var roots)) names[declaration.Name] = roots = new(DefinitionPaths.Comparer);
            roots.Add(DefinitionPaths.Canonical(input.Path));
        }
        lock (_gate)
        {
            _names = names.ToDictionary(p => p.Key, p => p.Value.Order(DefinitionPaths.Comparer).ToArray(), StringComparer.Ordinal);
            _diagnostics = issues;
        }
        void Add(GrammarDiagnostic diagnostic, string path) => issues.Add(new(diagnostic.Code, diagnostic.Message,
            new(path, diagnostic.Span.Start, diagnostic.Span.Length), [], "nitrogen", diagnostic.Severity == GrammarSeverity.Error));
    }

    public IReadOnlyList<string> Find(string name)
    {
        lock (_gate) return _names.GetValueOrDefault(name) ?? [];
    }
}
