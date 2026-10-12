using Nitrogen.Semantic;

namespace Nitrogen;

public sealed partial class QuoteInstantiation
{
    /// <summary>Maps only origins belonging to this constructed tree. Explicit lowering snapshot IDs can use the overload below.</summary>
    public SourceOrigin MapOrigin(SourceOrigin origin) =>
        Tree is { IsDisposed: false, QuotationIdentity: { } identity } && origin.SnapshotId == identity
            ? MapNode(origin) : origin;

    SourceOrigin MapNode(SourceOrigin origin) =>
        Origins.TryGetValue(origin.Node, out var mapped) && Tree is { } tree &&
        (uint)origin.Node < (uint)tree.NodeCount && origin.Span.Start >= tree.Span(origin.Node).Start &&
        origin.Span.End <= tree.Span(origin.Node).End ? mapped : origin;

    /// <summary>Original identities for the smallest mapped nodes covering or intersecting a generated range.</summary>
    public IReadOnlyList<SourceOrigin> MapRange(string generatedPath, TextSpan span)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedPath);
        if (Tree is not { IsDisposed: false } tree) return [];
        var candidates = Origins.Keys.Where(n =>
        {
            var at = tree.Span(n);
            return at.Start <= span.Start && at.End >= span.End;
        }).ToArray();
        if (candidates.Length == 0)
            candidates = Origins.Keys.Where(n => tree.Span(n).Start < span.End && tree.Span(n).End > span.Start).ToArray();
        return candidates.Where(n => !candidates.Any(other => other != n && tree.Span(other).Length < tree.Span(n).Length &&
                tree.Span(other).Start >= tree.Span(n).Start && tree.Span(other).End <= tree.Span(n).End))
            .Select(n => Origins[n]).Distinct().ToArray();
    }

    /// <summary>Remaps HIR and diagnostics from the default lowering of this constructed tree.</summary>
    public LoweringResult Remap(LoweringResult result) => RemapCore(result, MapOrigin);

    /// <summary>Remaps a document explicitly lowered with a caller-supplied snapshot identity.</summary>
    public LoweringResult Remap(LoweringResult result, string generatedPath, Guid snapshotId) =>
        RemapCore(result, origin => origin.Path == generatedPath && origin.SnapshotId == snapshotId ? MapNode(origin) : origin);

    static LoweringResult RemapCore(LoweringResult result, Func<SourceOrigin, SourceOrigin> map)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Roots.Select(Copy).ToArray(), result.Diagnostics.Select(d => d with { Origin = map(d.Origin) }).ToArray());

        HirNode Copy(HirNode node)
        {
            var origins = node.Origins.Select(map).Distinct().ToArray();
            return node switch
            {
                HirOperation operation => new HirOperation(operation.Signature, operation.Arguments.Select(Copy).ToArray(), origins),
                HirSequence sequence => new HirSequence(sequence.ElementType, sequence.Items.Select(Copy).ToArray(), origins),
                HirOptional optional => new HirOptional(optional.ElementType, optional.Value is { } value ? Copy(value) : null, origins),
                HirRepeat repeat => new HirRepeat(Copy(repeat.Count), repeat.Iterator, (HirSequence)Copy(repeat.Template), origins),
                _ => HirTraversal.WithOrigins(node, origins),
            };
        }
    }
}
