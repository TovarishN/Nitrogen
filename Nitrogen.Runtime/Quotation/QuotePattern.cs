namespace Nitrogen;

/// <summary>A reusable typed syntax pattern. Matching neither rewrites nor lowers the input.</summary>
public sealed class QuotePattern
{
    readonly ParseResult _parsed;
    readonly string _category;
    readonly IReadOnlyDictionary<string, QuoteHole> _captures;
    readonly IReadOnlyDictionary<int, QuotePatternList> _lists;
    bool _disposed;

    internal QuotePattern(ParseResult parsed, string category, IReadOnlyDictionary<string, QuoteHole> captures)
    {
        _parsed = parsed;
        _category = category;
        _captures = captures;
        var lists = new Dictionary<int, QuotePatternList>();
        // A template list is a List node, or a list hole standing alone as its collapsed one-item list.
        foreach (var (node, shape) in parsed.Tree.TemplateLists)
        {
            int stride = shape.Separator is null ? 1 : 2;
            int[] items = Enumerable.Range(0, (ListSlot.Count(parsed.Tree, node) + stride - 1) / stride)
                .Select(index => ListSlot.Child(parsed.Tree, node, index * stride)).ToArray();
            int hole = Array.FindIndex(items, item => parsed.Tree.Kind(item) == SyntaxKinds.ListHole);
            if (hole >= 0) lists[node] = new(items, hole, shape);
        }
        _lists = lists;
    }

    internal void DisposeStorage()
    {
        if (_disposed) return;
        _disposed = true;
        _parsed.Dispose();
    }

    public QuoteMatchResult Match(QuoteNode input) => MatchCore(input, null);

    internal QuoteMatchResult MatchResolved(QuoteNode input, Func<QuoteNode, QuoteNode> resolve) => MatchCore(input, resolve);

    QuoteMatchResult MatchCore(QuoteNode input, Func<QuoteNode, QuoteNode>? resolve)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_disposed) return QuotePatternMatcher.Invalid("NQP0004", "The pattern compilation has been disposed.");
        if (resolve is not null) input = resolve(input);
        if (input.Tree is null || input.Tree.IsDisposed || (uint)input.Node >= (uint)input.Tree.NodeCount)
            return QuotePatternMatcher.Invalid("NQP0004", "Invalid or disposed input syntax.");
        var language = _parsed.Tree.Language!;
        var invalid = QuotePatternMatcher.Validate(input, language, resolve);
        if (invalid is not null) return invalid;
        if (!language.AcceptsQuoteNode(_category, input.Tree.Kind(input.Node)))
            return QuotePatternMatcher.Invalid("NQP0005", "Input category does not match.");
        return new QuotePatternMatcher(_parsed.Tree, _captures, _lists, resolve).Match(input);
    }
}

internal sealed record QuotePatternList(int[] Items, int HoleIndex, TemplateListShape Shape);
