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

    /// <summary>
    /// Lowering codes that only say lowering was blocked: by recovered syntax, an unresolved name, or invalid
    /// semantics. Their cause is shown on its own, or is no error at all (a name an open scope accepts).
    /// </summary>
    static readonly HashSet<string> s_blockedLowering = new(StringComparer.Ordinal) { "NH0001", "NH0002", "NH0003" };

    /// <summary>
    /// The lowering diagnostics located in <paramref name="document"/>, from lowering every open document of
    /// its language: a definition's error is found while lowering the file that calls it. A blocked lowering
    /// is left out, and so is one at exactly the range of a diagnostic in <paramref name="shown"/> (a
    /// lowerer's own report of an unresolved name).
    /// </summary>
    IEnumerable<ServiceDiagnostic> LoweringDiagnostics(Document document, IReadOnlyList<ServiceDiagnostic> shown)
    {
        var lowered = _documents.Values.Where(other => other.Language == document.Language)
            .OrderBy(other => other.Uri, StringComparer.Ordinal)
            .SelectMany(other => InspectDocument(other.Uri)!.Diagnostics)
            .Where(diagnostic => diagnostic.Origin.Path == document.Uri)
            .DistinctBy(diagnostic => (diagnostic.Code, diagnostic.Origin.Span, diagnostic.Message));
        foreach (var diagnostic in lowered)
        {
            var range = document.Lines.RangeOf(diagnostic.Origin.Span);
            if (s_blockedLowering.Contains(diagnostic.Code) || shown.Any(other => other.Range == range)) continue;
            yield return new ServiceDiagnostic(range, ServiceSeverity.Error, diagnostic.Code, diagnostic.Message);
        }
    }

    /// <summary>The narrowest typed HIR origin at a UTF-16 document position.</summary>
    public SemanticInspection? Inspect(string uri, DocumentPosition position)
    {
        if (!_documents.TryGetValue(uri, out var document) || InspectDocument(uri) is not { } lowered)
            return null;
        var offset = document.Lines.OffsetOf(position);
        if (HirInspection.Find(lowered.Roots, uri, offset) is not { } hit) return null;
        var (bestRoot, best, bestOrigin) = (hit.Root, hit.Node, hit.Origin);

        DocumentLocation? declaration = null;
        if (best is HirSymbolRef reference && !reference.Symbol.Binding.IsBuiltin &&
            reference.Symbol.Binding.Path is { } path && _documents.TryGetValue(path, out var source))
            declaration = new DocumentLocation(path, source.Lines.RangeOf(reference.Symbol.Binding.NameSpan));

        return new SemanticInspection(lowered.Version, lowered.SnapshotId, bestRoot, best,
            document.Lines.RangeOf(bestOrigin.Span), best.Type, declaration);
    }
}
