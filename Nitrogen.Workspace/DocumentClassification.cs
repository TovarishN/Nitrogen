using System.Text.Json;
using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

public sealed record ClassificationResult(IReadOnlyList<AnalysisSelection> Selections,
    IReadOnlyList<AnalysisIssue> Diagnostics, IReadOnlyList<ProjectedInput> Regions);

public sealed class DocumentClassifier(ILanguageDefinitionResolver? resolver = null)
{
    readonly ILanguageDefinitionResolver _resolver = resolver ?? new LanguageDefinitionResolver();

    public ClassificationResult Classify(DocumentInput input, InputSnapshot inputs)
    {
        var selected = new Dictionary<string, AnalysisSelection>(DefinitionPaths.Comparer);
        var issues = new List<AnalysisIssue>();
        var header = DocumentSignatures.Read(input.Text);
        issues.AddRange(header.Diagnostics.Select(d => new AnalysisIssue(d.Code, d.Message,
            new(input.Path, d.Span.Start, d.Span.Length), [], "nitrogen", d.Severity == GrammarSeverity.Error)));
        if (header.Diagnostics.Any(d => d.Severity == GrammarSeverity.Error)) return new([], issues, []);
        bool primary = header.Signatures.Any(s => s.Mode is SignatureMode.Language or SignatureMode.Grammar);
        foreach (var signature in header.Signatures)
        {
            if (signature.Reference is null) continue;
            var resolved = _resolver.Resolve(signature.Reference, input.Path, inputs);
            issues.AddRange(resolved.Diagnostics.Select(d => d.Location.Path == input.Path
                ? d with { Location = new(input.Path, signature.Start, signature.Length) } : d));
            if (resolved.Definition is { } definition) Select(definition, signature.Input, signature.Mode == SignatureMode.Language);
        }
        foreach (var configuration in DefinitionPaths.RootsOf(inputs).Where(root => DefinitionPaths.Under(input.Path, root))
            .Select(root => DefinitionPaths.Find(inputs, Path.Combine(root, "nitrogen.json"))).OfType<DocumentInput>())
        {
            var augmentations = AugmentationConfiguration.Parse(configuration.Path, configuration.Text);
            issues.AddRange(augmentations.Diagnostics);
            foreach (var rule in augmentations.Matching(input.Path))
            {
                var resolved = _resolver.Resolve(rule.Language, rule.OriginPath, inputs);
                issues.AddRange(resolved.Diagnostics);
                if (resolved.Definition is { } definition) Select(definition, rule.Input, false);
            }
            if (!primary)
                try
                {
                    using var json = JsonDocument.Parse(configuration.Text);
                    if (json.RootElement.TryGetProperty("languages", out var languages))
                        foreach (var entry in languages.EnumerateArray())
                            if (entry.TryGetProperty("extensions", out var extensions) && extensions.EnumerateArray()
                                .Any(e => string.Equals(e.GetString(), Path.GetExtension(input.Path), StringComparison.OrdinalIgnoreCase)))
                            {
                                var resolved = LanguageDefinitionLoader.LoadLegacy(entry, configuration.Path, inputs);
                                issues.AddRange(resolved.Diagnostics);
                                if (resolved.Definition is { } definition) Select(definition, AnalysisInputMode.Document, true);
                            }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                {
                    issues.Add(DefinitionPaths.Error(configuration.Path, "invalid language configuration: " + error.Message));
                }
        }
        // Signed roots may also opt into extension recognition; no name resolution is implied here.
        if (!primary && !selected.Values.Any(s => s.IsPrimary))
            foreach (var candidate in inputs.Files.Values)
            {
                if (!candidate.Text.Contains("#!nitrogen", StringComparison.Ordinal)) continue;
                var candidateHeader = DocumentSignatures.Read(candidate.Text);
                if (!candidateHeader.Signatures.Any(s => s.Mode == SignatureMode.Grammar)) continue;
                var file = GrammarParser.Parse(candidateHeader.MaskedText).File;
                if (file?.Languages.Any(l => l.Extensions.Any(e => string.Equals(e, Path.GetExtension(input.Path), StringComparison.OrdinalIgnoreCase))) is not true) continue;
                var resolved = LanguageDefinitionLoader.Load(candidate.Path, inputs);
                issues.AddRange(resolved.Diagnostics);
                if (resolved.Definition is { } definition) Select(definition, AnalysisInputMode.Document, true);
            }
        if (selected.Values.Count(s => s.IsPrimary) > 1)
        {
            issues.Add(DefinitionPaths.Error(input.Path, "multiple Nitrogen languages claim this extension; use an explicit language signature"));
            foreach (string key in selected.Where(p => p.Value.IsPrimary).Select(p => p.Key).ToArray()) selected.Remove(key);
        }
        var regions = input.Text.Contains("#!nitrogen", StringComparison.Ordinal)
            ? RegionExtractor.Extract(input, _resolver, inputs) : new ProjectionResult([], []);
        issues.AddRange(regions.Diagnostics);
        return new(selected.Values.ToArray(), issues, regions.Inputs);

        void Select(LanguageDefinition definition, AnalysisInputMode mode, bool isPrimary)
        {
            string key = definition.Id + "#" + mode;
            if (selected.TryGetValue(key, out var existing)) isPrimary |= existing.IsPrimary;
            selected[key] = new(key, definition, mode, isPrimary);
        }
    }
}
