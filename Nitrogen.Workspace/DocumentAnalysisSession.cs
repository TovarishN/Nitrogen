using System.Collections.Concurrent;
using Nitrogen.Binding;
using Nitrogen.Grammar;
using Nitrogen.Semantics;

namespace Nitrogen.Workspace;

public sealed record DocumentAnalysisResult(string Path, long Version, IReadOnlyList<AnalysisIssue> Diagnostics, ClassificationResult? Classification = null, long Epoch = 0);

/// <summary>Shared checking with collectible compiled languages and explicit source input ownership.</summary>
public sealed class DocumentAnalysisSession : IDisposable
{
    sealed record Compilation(LanguageDefinition Definition, WorkspaceSnapshot Snapshot, string Fingerprint);
    readonly DocumentClassifier _classifier;
    readonly Dictionary<string, Compilation> _compiled = new(DefinitionPaths.Comparer);
    readonly SemaphoreSlim _gate = new(1);
    readonly ConcurrentDictionary<string, long> _epochs = new(DefinitionPaths.Comparer);
    long _nextEpoch;
    bool _disposed;

    public DocumentAnalysisSession(ILanguageDefinitionResolver? resolver = null) =>
        _classifier = new DocumentClassifier(new LastGoodResolver(resolver ?? new LanguageDefinitionResolver(), _compiled));

    sealed class LastGoodResolver(ILanguageDefinitionResolver inner, Dictionary<string, Compilation> compiled) : ILanguageDefinitionResolver
    {
        public DefinitionResult Resolve(LanguageReference reference, string originPath, InputSnapshot inputs)
        {
            var result = inner.Resolve(reference, originPath, inputs);
            if (result.Definition is not null || !reference.IsPath && result.CandidateId is null) return result;
            string path;
            try { path = result.CandidateId ?? DefinitionPaths.RelativeTo(reference.Value, originPath); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return result; }
            if (DefinitionPaths.Find(inputs, path) is null || !compiled.TryGetValue(path, out var previous) ||
                !reference.IsPath && previous.Definition.Name != reference.Value) return result;
            var stale = new AnalysisIssue("NGR0405", "using the last successfully compiled language while its definition contains errors",
                new(path, 0, 0), [], "nitrogen:" + previous.Definition.Name, false);
            return new(previous.Definition, result.Diagnostics.Append(stale).ToArray());
        }
    }

    public async Task<DocumentAnalysisResult> AnalyzeAsync(DocumentInput input, InputSnapshot inputs, CancellationToken cancellationToken)
    {
        long epoch = Interlocked.Increment(ref _nextEpoch);
        _epochs[input.Path] = epoch;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var classification = _classifier.Classify(input, inputs);
            var issues = classification.Diagnostics.ToList();
            if (DocumentSignatures.Read(input.Text).Signatures.Any(s => s.Mode == SignatureMode.Grammar))
            {
                var definition = new LastGoodResolver(inner: new LanguageDefinitionResolver(), compiled: _compiled)
                    .Resolve(new LanguageReference(input.Path, true), input.Path, inputs);
                issues.AddRange(definition.Diagnostics);
                if (definition.Definition is { } grammar) Compile(grammar, inputs, issues);
            }
            var projections = classification.Regions.ToList();
            foreach (var selection in classification.Selections.Where(s => s.Input == AnalysisInputMode.Document))
                projections.Add(new(selection, new(input.Path, DocumentSignatures.Read(input.Text).MaskedText, 0)));
            foreach (var projection in projections.DistinctBy(p => (p.Selection.Definition.Id, p.Projection.HostStart, p.Projection.Text.Length)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = projection.Selection.Definition;
                var compiled = Compile(definition, inputs, issues);
                if (compiled?.Snapshot.Language is not { } language) continue;
                if (compiled.Snapshot.FindRule(definition.Start) is not { } start) { issues.Add(DefinitionPaths.Error(definition.Id, $"unknown start rule '{definition.Start}'")); continue; }
                using var parsed = language.Parse(projection.Projection.Text, start);
                string source = "nitrogen:" + definition.Name;
                foreach (var diagnostic in parsed.Diagnostics)
                    issues.Add(projection.Projection.Map(new AnalysisIssue(diagnostic.Code.ToString(), parsed.FormatMessage(diagnostic),
                        new(input.Path, diagnostic.Span.Start, diagnostic.Span.Length), [], source, diagnostic.Severity == DiagnosticSeverity.Error, projection.Selection.Key)));
                var project = new Project(language);
                project.Set(input.Path, parsed.Tree);
                foreach (var diagnostic in project.Diagnostics(input.Path))
                    issues.Add(projection.Projection.Map(new AnalysisIssue(diagnostic.Code, diagnostic.Message, new(input.Path, diagnostic.Span.Start, diagnostic.Span.Length), [], source, true, projection.Selection.Key)));
                var semantics = new ProjectSemantics(project)[input.Path];
                foreach (var diagnostic in semantics.Diagnostics())
                    issues.Add(projection.Projection.Map(new AnalysisIssue(diagnostic.Code, diagnostic.Message, new(input.Path, diagnostic.Span.Start, diagnostic.Span.Length), [], source,
                        !diagnostic.Code.StartsWith("NS000", StringComparison.Ordinal), projection.Selection.Key)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(input.Path, input.Version, issues.DistinctBy(i => (i.Code, i.Message, i.Location, i.Source)).ToArray(), classification, epoch);
        }
        finally { _gate.Release(); }
    }

    public bool IsCurrent(DocumentAnalysisResult result) =>
        !_disposed && _epochs.TryGetValue(result.Path, out long epoch) && epoch == result.Epoch;

    /// <summary>Borrowed by the serial editor adapter; owned and disposed by this session.</summary>
    internal LanguageDefinition? DefinitionFor(string id) => _compiled.GetValueOrDefault(id)?.Definition;

    internal WorkspaceSnapshot? SnapshotFor(string id) => _compiled.GetValueOrDefault(id)?.Snapshot;

    Compilation? Compile(LanguageDefinition definition, InputSnapshot inputs, List<AnalysisIssue> issues)
    {
        string fingerprint = string.Concat(definition.Grammars.Concat(definition.Sources).Select(f =>
            f.Path.Length + ":" + f.Path + f.Text.Length + ":" + f.Text)) + definition.GeneratedNamespace + "|" + string.Join("|", definition.Usings);
        if (_compiled.TryGetValue(definition.Id, out var previous) && previous.Fingerprint == fingerprint) return previous;
        var workspace = new GrammarWorkspace { GeneratedNamespace = definition.GeneratedNamespace };
        foreach (var grammar in definition.Grammars) workspace.SetGrammar(grammar.Path, grammar.Text);
        foreach (var source in definition.Sources) workspace.Sources[source.Path] = source.Text;
        foreach (string name in definition.Usings) workspace.Usings.Add(name);
        var snapshot = workspace.Compile();
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            string path = diagnostic.Path.Length == 0 ? definition.Id : diagnostic.Path;
            string text = DefinitionPaths.Find(inputs, path)?.Text ?? "";
            issues.Add(new(diagnostic.Code, diagnostic.Message, new(path, Offset(text, diagnostic.Line, diagnostic.Column), 0), [], "nitrogen:" + definition.Name, diagnostic.IsError));
        }
        if (!snapshot.Succeeded)
        {
            snapshot.Dispose();
            if (previous is not null) issues.Add(new("NGR0405", "using the last successfully compiled language while its sources contain errors",
                new(definition.Id, 0, 0), [], "nitrogen:" + definition.Name, false));
            return previous;
        }
        previous?.Snapshot.Dispose();
        var next = new Compilation(definition, snapshot, fingerprint);
        _compiled[definition.Id] = next;
        return next;
    }

    static int Offset(string text, int line, int column)
    {
        int offset = 0;
        for (int current = 1; current < line && offset < text.Length; current++)
        {
            int next = text.IndexOf('\n', offset);
            offset = next < 0 ? text.Length : next + 1;
        }
        return Math.Clamp(offset + Math.Max(column - 1, 0), 0, text.Length);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var compilation in _compiled.Values) compilation.Snapshot.Dispose();
            _compiled.Clear();
        }
        finally { _gate.Release(); }
    }
}
