using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>The innermost lowered node whose origin covers a position — smallest span, then
/// deepest — searched through every composite HIR node: operation arguments, sequence items, an
/// optional's value, a repeat's count and template.</summary>
internal static class HirInspection
{
    internal readonly record struct Hit(HirNode Root, HirNode Node, SourceOrigin Origin);

    internal static Hit? Find(IReadOnlyList<HirNode> roots, string uri, int offset)
    {
        Hit? best = null;
        int bestDepth = -1;

        void Visit(HirNode root, HirNode node, int depth)
        {
            foreach (var origin in node.Origins)
            {
                if (origin.Path != uri || origin.Span.Start > offset || offset >= origin.Span.End) continue;
                if (best is null || origin.Span.Length < best.Value.Origin.Span.Length ||
                    origin.Span.Length == best.Value.Origin.Span.Length && depth > bestDepth)
                {
                    best = new Hit(root, node, origin);
                    bestDepth = depth;
                }
            }
            switch (node)
            {
                case HirOperation operation:
                    foreach (var child in operation.Arguments) Visit(root, child, depth + 1);
                    break;
                case HirSequence sequence:
                    foreach (var item in sequence.Items) Visit(root, item, depth + 1);
                    break;
                case HirOptional { Value: { } value }:
                    Visit(root, value, depth + 1);
                    break;
                case HirRepeat repeat:
                    Visit(root, repeat.Count, depth + 1);
                    Visit(root, repeat.Template, depth + 1);
                    break;
            }
        }

        foreach (var root in roots) Visit(root, root, 0);
        return best;
    }
}
