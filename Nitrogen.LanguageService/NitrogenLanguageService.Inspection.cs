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

    /// <summary>Lowering codes that only say an error elsewhere blocked lowering: recovered syntax, an unresolved name, invalid semantics.</summary>
    static readonly HashSet<string> s_blockedLowering = new(StringComparer.Ordinal) { "NH0001", "NH0002", "NH0003" };

    /// <summary>
    /// The lowering diagnostics located in <paramref name="document"/>, from lowering every open document of
    /// its language: a definition's error is found while lowering the file that calls it. One that repeats
    /// a diagnostic in <paramref name="shown"/> is left out: a blocked lowering with one within its span,
    /// and any with one at exactly its range (a lowerer's own report of an unresolved name).
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
            bool blocked = s_blockedLowering.Contains(diagnostic.Code);
            if (shown.Any(other => other.Range == range || blocked && Within(range, other.Range))) continue;
            yield return new ServiceDiagnostic(range, ServiceSeverity.Error, diagnostic.Code, diagnostic.Message);
        }
    }

    static bool Within(DocumentRange outer, DocumentRange inner) =>
        Compare(outer.Start, inner.Start) <= 0 && Compare(inner.End, outer.End) <= 0;

    static int Compare(DocumentPosition a, DocumentPosition b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);

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
