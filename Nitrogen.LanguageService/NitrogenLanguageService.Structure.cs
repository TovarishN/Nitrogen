namespace Nitrogen.LanguageService;

/// <summary>
/// Folding and expand-selection from the syntax tree, for every language. A node's extent runs from its
/// first character to its last non-whitespace one. Every node spanning two or more lines folds (the
/// outermost per start line), leaving a last line of only punctuation (a closing brace) visible, and so
/// does a gap between tokens holding comments on two or more lines. Selection climbs from the innermost
/// node at a position through its enclosing nodes, skipping repeated extents. C# hosts get neither.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    public IReadOnlyList<ServiceFoldingRange> FoldingRanges(string uri)
    {
        if (_hosts.ContainsKey(uri) || !_documents.TryGetValue(uri, out var document)) return [];
        var tree = document.Parsed.Tree;
        var text = document.Text;
        var lines = document.Lines;
        var byStart = new Dictionary<int, ServiceFoldingRange>();

        void Offer(ServiceFoldingRange fold)
        {
            if (fold.EndLine <= fold.StartLine) return;
            if (!byStart.TryGetValue(fold.StartLine, out var kept) || fold.EndLine > kept.EndLine) byStart[fold.StartLine] = fold;
        }

        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (Extent(tree, text, node) is not { } extent) continue;
            int start = lines.PositionOf(extent.Start).Line, end = lines.PositionOf(extent.End).Line;
            if (end > start) Offer(new ServiceFoldingRange(start, end, false));
        }
        // Closers stay visible: applied after choosing the outermost per line, so it never picks a different node.
        foreach (var (start, fold) in byStart.ToList())
        {
            if (fold.IsComment) continue;
            if (IsOnlyPunctuation(LineText(text, lines, fold.EndLine)))
            {
                if (fold.EndLine - 1 > start) byStart[start] = fold with { EndLine = fold.EndLine - 1 };
                else byStart.Remove(start);
            }
        }
        var skipped = tree.SkippedSpans.ToArray();
        foreach (var gap in tree.Trivia.ToArray())
        {
            int first = -1, last = -1;
            for (int i = gap.Start; i < gap.End && i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]) || skipped.Any(s => s.Start <= i && i < s.End)) continue;
                int line = lines.PositionOf(i).Line;
                if (first < 0) first = line;
                last = line;
            }
            if (first >= 0) Offer(new ServiceFoldingRange(first, last, true));
        }
        return byStart.Values.OrderBy(f => f.StartLine).ToList();
    }

    /// <summary>A node's extent: its first character to its last non-whitespace one; null when empty.</summary>
    static TextSpan? Extent(SyntaxTree tree, string text, int node)
    {
        var span = tree.Span(node);
        int end = Math.Min(span.End, text.Length);
        while (end > span.Start && char.IsWhiteSpace(text[end - 1])) end--;
        return end > span.Start ? new TextSpan(span.Start, end - span.Start) : null;
    }

    static string LineText(string text, LineMap lines, int line)
    {
        int start = lines.OffsetOf(new DocumentPosition(line, 0));
        return text.Substring(start, lines.LineLength(line));
    }

    static bool IsOnlyPunctuation(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(c => !char.IsLetterOrDigit(c) && c != '_');
    }
}
