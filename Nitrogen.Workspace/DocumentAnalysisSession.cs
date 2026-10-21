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
            return Analyze(input, inputs, cancellationToken, epoch, bindPrimary: true);
        }
        finally { _gate.Release(); }
    }

    DocumentAnalysisResult Analyze(DocumentInput input, InputSnapshot inputs, CancellationToken cancellationToken, long epoch, bool bindPrimary)
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
            if (bindPrimary || !projection.Selection.IsPrimary)
            {
                var project = new Project(language);
                project.Set(input.Path, parsed.Tree);
                using var semantics = new ProjectSemantics(project);
                AddBindingDiagnostics(project, semantics, projection, issues);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(input.Path, input.Version, issues.DistinctBy(i => (i.Code, i.Message, i.Location, i.Source)).ToArray(), classification, epoch);
    }

    /// <summary>Checks primary documents in a shared language project; augmentation regions remain isolated.</summary>
    public async Task<IReadOnlyList<DocumentAnalysisResult>> AnalyzeManyAsync(IEnumerable<DocumentInput> documents, InputSnapshot inputs, CancellationToken cancellationToken)
    {
        var pending = documents.Select(input => (Input: input, Epoch: Interlocked.Increment(ref _nextEpoch))).ToArray();
        foreach (var item in pending) _epochs[item.Input.Path] = item.Epoch;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = pending.Select(item => Analyze(item.Input, inputs, cancellationToken, item.Epoch, bindPrimary: false)).ToArray();
            var primaryIds = results.SelectMany(r => r.Classification!.Selections.Where(s => s.IsPrimary)).Select(s => s.Definition.Id).ToHashSet(DefinitionPaths.Comparer);
            if (primaryIds.Count == 0) return results;
            var members = inputs.Files.Values.Concat(pending.Select(p => p.Input)).GroupBy(i => i.Path, DefinitionPaths.Comparer).Select(g => g.Last())
                .SelectMany(input => _classifier.Classify(input, inputs).Selections.Where(s => s.IsPrimary && primaryIds.Contains(s.Definition.Id))
                    .Select(selection => (Input: input, Selection: selection))).GroupBy(item => item.Selection.Definition.Id, DefinitionPaths.Comparer);
            foreach (var group in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_compiled.TryGetValue(group.Key, out var compilation) || compilation.Snapshot.Language is not { } language) continue;
                var parsed = new List<ParseResult>();
                try
                {
                    var project = new Project(language);
                    foreach (var member in group)
                    {
                        if (compilation.Snapshot.FindRule(member.Selection.Definition.Start) is not { } start) continue;
                        var tree = language.Parse(DocumentSignatures.Read(member.Input.Text).MaskedText, start);
                        parsed.Add(tree);
                        project.Set(member.Input.Path, tree.Tree);
                    }
                    using var semantics = new ProjectSemantics(project);
                    for (int index = 0; index < results.Length; index++)
                    {
                        var result = results[index];
                        var primary = result.Classification!.Selections.FirstOrDefault(s => s.IsPrimary && DefinitionPaths.Comparer.Equals(s.Definition.Id, group.Key));
                        if (primary is null || !project.Paths.Contains(result.Path)) continue;
                        var issues = result.Diagnostics.ToList();
                        AddBindingDiagnostics(project, semantics, new(primary, new(result.Path, "", 0)), issues);
                        results[index] = result with { Diagnostics = issues.DistinctBy(i => (i.Code, i.Message, i.Location, i.Source)).ToArray() };
                    }
                }
                finally { foreach (var tree in parsed) tree.Dispose(); }
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    static void AddBindingDiagnostics(Project project, ProjectSemantics semantics, ProjectedInput projection, List<AnalysisIssue> issues)
    {
        string path = projection.Projection.HostPath;
        string source = "nitrogen:" + projection.Selection.Definition.Name;
        foreach (var diagnostic in project.Diagnostics(path))
            issues.Add(projection.Projection.Map(new AnalysisIssue(diagnostic.Code, diagnostic.Message, new(path, diagnostic.Span.Start, diagnostic.Span.Length), [], source, true, projection.Selection.Key)));
        foreach (var diagnostic in semantics[path].Diagnostics())
            issues.Add(projection.Projection.Map(new AnalysisIssue(diagnostic.Code, diagnostic.Message, new(path, diagnostic.Span.Start, diagnostic.Span.Length), [], source,
                diagnostic.Severity == DiagnosticSeverity.Error && !diagnostic.Code.StartsWith("NS000", StringComparison.Ordinal), projection.Selection.Key)));
    }

    public bool IsCurrent(DocumentAnalysisResult result) =>
        !_disposed && _epochs.TryGetValue(result.Path, out long epoch) && epoch == result.Epoch;

    /// <summary>Borrowed by the serial editor adapter; owned and disposed by this session.</summary>
    internal LanguageDefinition? DefinitionFor(string id) => _compiled.GetValueOrDefault(id)?.Definition;

    internal WorkspaceSnapshot? SnapshotFor(string id) => _compiled.GetValueOrDefault(id)?.Snapshot;

    internal void RetainDefinitions(IEnumerable<string> ids)
    {
        var retained = ids.ToHashSet(DefinitionPaths.Comparer);
        foreach (string id in _compiled.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            _compiled[id].Snapshot.Dispose();
            _compiled.Remove(id);
        }
    }

    Compilation? Compile(LanguageDefinition definition, InputSnapshot inputs, List<AnalysisIssue> issues)
    {
        string fingerprint = string.Concat(definition.Grammars.Concat(definition.Sources).Select(f =>
            f.Path.Length + ":" + f.Path + f.Text.Length + ":" + f.Text)) + definition.GeneratedNamespace + "|" + definition.Name + "|" + definition.Start + "|" + string.Join("|", definition.Usings);
        if (_compiled.TryGetValue(definition.Id, out var previous) && previous.Fingerprint == fingerprint)
        {
            AddCompilationDiagnostics(previous.Snapshot, definition, inputs, issues);
            return previous;
        }
        var workspace = new GrammarWorkspace { GeneratedNamespace = definition.GeneratedNamespace, CompositionName = definition.Name, StartRule = definition.Start };
        foreach (var grammar in definition.Grammars) workspace.SetGrammar(grammar.Path, grammar.Text);
        foreach (var source in definition.Sources) workspace.Sources[source.Path] = source.Text;
        foreach (string name in definition.Usings) workspace.Usings.Add(name);
        var snapshot = workspace.Compile();
        AddCompilationDiagnostics(snapshot, definition, inputs, issues);
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

    static void AddCompilationDiagnostics(WorkspaceSnapshot snapshot, LanguageDefinition definition, InputSnapshot inputs, List<AnalysisIssue> issues)
    {
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            string path = diagnostic.Path.Length == 0 ? definition.Id : diagnostic.Path;
            string text = DefinitionPaths.Find(inputs, path)?.Text ?? "";
            issues.Add(new(diagnostic.Code, diagnostic.Message, new(path, Offset(text, diagnostic.Line, diagnostic.Column), 0), [], "nitrogen:" + definition.Name, diagnostic.IsError));
        }
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
