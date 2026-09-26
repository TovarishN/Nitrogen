using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record InspectionCacheEntry(int DocumentVersion, int ProjectVersion,
        LanguageEntry Language, DocumentInspection Result);

    readonly Dictionary<string, InspectionCacheEntry> _inspection = new(StringComparer.Ordinal);

    /// <summary>Lowered roots and diagnostics for the current open document, or null if closed.</summary>
    public DocumentInspection? InspectDocument(string uri)
    {
        if (!_documents.TryGetValue(uri, out var document)) return null;
        var project = _projects[document.Language];
        if (_inspection.TryGetValue(uri, out var hit) &&
            hit.DocumentVersion == document.Version && hit.ProjectVersion == project.Version &&
            ReferenceEquals(hit.Language, document.Language)) return hit.Result;

        var id = Guid.NewGuid();
        var lowered = HirLowering.Lower(SemanticsOf(document.Language)[uri],
            document.Language.Language.SemanticCatalog, id);
        var result = new DocumentInspection(document.Version, id, lowered.Roots, lowered.Diagnostics);
        _inspection[uri] = new InspectionCacheEntry(document.Version, project.Version, document.Language, result);
        return result;
    }

    /// <summary>The narrowest typed HIR origin at a UTF-16 document position.</summary>
    public SemanticInspection? Inspect(string uri, DocumentPosition position)
    {
        if (!_documents.TryGetValue(uri, out var document) || InspectDocument(uri) is not { } lowered)
            return null;
        var offset = document.Lines.OffsetOf(position);
        HirNode? best = null, bestRoot = null;
        SourceOrigin bestOrigin = default;
        var bestDepth = -1;

        void Visit(HirNode root, HirNode node, int depth)
        {
            foreach (var origin in node.Origins)
            {
                if (origin.Path != uri || origin.Span.Start > offset || offset >= origin.Span.End) continue;
                if (best is null || origin.Span.Length < bestOrigin.Span.Length ||
                    origin.Span.Length == bestOrigin.Span.Length && depth > bestDepth)
                {
                    best = node;
                    bestRoot = root;
                    bestOrigin = origin;
                    bestDepth = depth;
                }
            }
            if (node is HirOperation operation)
                foreach (var child in operation.Arguments) Visit(root, child, depth + 1);
        }

        foreach (var root in lowered.Roots) Visit(root, root, 0);
        if (best is null) return null;

        DocumentLocation? declaration = null;
        if (best is HirSymbolRef reference && !reference.Symbol.Binding.IsBuiltin &&
            reference.Symbol.Binding.Path is { } path && _documents.TryGetValue(path, out var source))
            declaration = new DocumentLocation(path, source.Lines.RangeOf(reference.Symbol.Binding.NameSpan));

        return new SemanticInspection(lowered.Version, lowered.SnapshotId, bestRoot!, best,
            document.Lines.RangeOf(bestOrigin.Span), best.Type, declaration);
    }
}
