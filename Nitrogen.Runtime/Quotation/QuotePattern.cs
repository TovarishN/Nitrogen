namespace Nitrogen;

/// <summary>A reusable typed syntax pattern. Matching neither rewrites nor lowers the input.</summary>
public sealed class QuotePattern
{
    readonly ParseResult _parsed;
    readonly string _category;
    readonly IReadOnlyDictionary<string, QuoteHole> _captures;
    readonly IReadOnlyDictionary<int, QuotePatternList> _lists;
    bool _disposed;

    internal QuotePattern(ParseResult parsed, string category, IReadOnlyDictionary<string, QuoteHole> captures,
        IReadOnlyDictionary<string, NamedPatternDefinition>? references = null)
    {
        _parsed = parsed;
        _category = category;
        _captures = captures;
        References = references ?? new Dictionary<string, NamedPatternDefinition>(StringComparer.Ordinal);
        // A whole-pattern hole naming a pattern matches the same node again: a non-consuming reference.
        int root = parsed.Tree.Root;
        while (parsed.Tree.Kind(root) == SyntaxKinds.Group && parsed.Tree.ChildCount(root) == 1) root = parsed.Tree.Child(root, 0);
        if (parsed.Tree.Kind(root) == SyntaxKinds.Hole &&
            References.TryGetValue(parsed.Tree.GetText(root)[1..].ToString(), out var target))
            RootReference = target;
        var lists = new Dictionary<int, QuotePatternList>();
        // A template list is a List node, or a list hole standing alone as its collapsed one-item list.
        foreach (var (node, shape) in parsed.Tree.TemplateLists)
        {
            int stride = shape.Separator is null ? 1 : 2;
            int[] items = Enumerable.Range(0, (ListSlot.Count(parsed.Tree, node) + stride - 1) / stride)
                .Select(index => ListSlot.Child(parsed.Tree, node, index * stride)).ToArray();
            int hole = Array.FindIndex(items, item => parsed.Tree.Kind(item) == SyntaxKinds.ListHole);
            if (hole >= 0) lists[node] = new(items, shape, parsed.Tree);
        }
        _lists = lists;
    }

    internal SyntaxTree Tree => _parsed.Tree;
    internal IReadOnlyDictionary<string, QuoteHole> Contracts => _captures;
    internal IReadOnlyDictionary<int, QuotePatternList> Lists => _lists;

    /// <summary>Named patterns required at holes, by capture name.</summary>
    internal IReadOnlyDictionary<string, NamedPatternDefinition> References { get; }

    /// <summary>The named pattern when this whole pattern is one reference hole; it consumes no syntax.</summary>
    internal NamedPatternDefinition? RootReference { get; }

    bool _hideCaptures;

    internal void HideCaptures() => _hideCaptures = true;

    internal void DisposeStorage()
    {
        if (_disposed) return;
        _disposed = true;
        _parsed.Dispose();
    }

    public QuoteMatchResult Match(QuoteNode input) => Match(input, new QuoteMatchOptions());

    public QuoteMatchResult Match(QuoteNode input, QuoteMatchOptions options) => MatchCore(input, null, options);

    internal QuoteMatchResult MatchResolved(QuoteNode input, Func<QuoteNode, QuoteNode> resolve, QuoteMatchOptions? options = null)
        => MatchCore(input, resolve, options ?? new QuoteMatchOptions());

    QuoteMatchResult MatchCore(QuoteNode input, Func<QuoteNode, QuoteNode>? resolve, QuoteMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxWorkSteps);
        options.CancellationToken.ThrowIfCancellationRequested();
        try { return MatchChecked(input, resolve, new QuoteMatchBudget(options)); }
        catch (QuoteMatchLimitException)
        {
            return QuotePatternMatcher.Invalid("NQP0006", "The syntax-pattern match exceeded its work limit.");
        }
    }

    QuoteMatchResult MatchChecked(QuoteNode input, Func<QuoteNode, QuoteNode>? resolve, QuoteMatchBudget budget)
    {
        if (_disposed) return QuotePatternMatcher.Invalid("NQP0004", "The pattern compilation has been disposed.");
        if (resolve is not null) input = resolve(input);
        if (input.Tree is null || input.Tree.IsDisposed || (uint)input.Node >= (uint)input.Tree.NodeCount)
            return QuotePatternMatcher.Invalid("NQP0004", "Invalid or disposed input syntax.");
        var language = _parsed.Tree.Language!;
        var invalid = QuotePatternMatcher.Validate(input, language, resolve, budget);
        if (invalid is not null) return invalid;
        if (!language.AcceptsQuoteNode(_category, input.Tree, input.Node))
            return QuotePatternMatcher.Invalid("NQP0005", "Input category does not match.");
        return Published(new QuotePatternMatcher(this, resolve, budget).Match(input));
    }

    QuoteMatchResult Published(QuoteMatchResult result) => _hideCaptures && result.Captures.Count > 0
        ? result with { Captures = new System.Collections.ObjectModel.ReadOnlyDictionary<string, QuoteArgument>(new Dictionary<string, QuoteArgument>()) }
        : result;

    /// <summary>
    /// Matches every node at or below <paramref name="root"/> in preorder, document order, including nested matches.
    /// Nodes whose own subtree has recovered or ambiguous syntax are skipped. One work limit covers the whole search.
    /// </summary>
    public QuoteFindResult FindAll(QuoteNode root, QuoteMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new QuoteMatchOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxWorkSteps);
        options.CancellationToken.ThrowIfCancellationRequested();
        var matches = new List<QuoteFoundMatch>();
        if (_disposed) return Incomplete("NQP0004", "The pattern compilation has been disposed.");
        if (root.Tree is null || root.Tree.IsDisposed || (uint)root.Node >= (uint)root.Tree.NodeCount)
            return Incomplete("NQP0004", "Invalid or disposed input syntax.");
        var language = _parsed.Tree.Language!;
        if (!ReferenceEquals(root.Tree.Language, language)) return Incomplete("NQP0005", "Input language snapshot does not match.");
        var budget = new QuoteMatchBudget(options);
        try
        {
            var invalid = QuotePatternMatcher.InvalidSubtrees(root, budget);
            var work = new Stack<int>();
            work.Push(root.Node);
            while (work.TryPop(out int node))
            {
                budget.Step();
                var tree = root.Tree;
                for (int i = tree.ChildCount(node) - 1; i >= 0; i--)
                    if (tree.Child(node, i) is var child && child != SyntaxTree.NoNode) work.Push(child);
                if (invalid.Contains(node) || !language.AcceptsQuoteNode(_category, tree, node)) continue;
                var candidate = new QuoteNode(tree, node, root.Origin);
                var result = Published(new QuotePatternMatcher(this, null, budget).Match(candidate));
                if (result.Matched) matches.Add(new(candidate, result.Captures));
            }
        }
        catch (QuoteMatchLimitException)
        {
            return Incomplete("NQP0006", "The syntax-pattern search exceeded its work limit.");
        }
        return new(matches.AsReadOnly(), true, []);

        QuoteFindResult Incomplete(string code, string message) =>
            new(matches.AsReadOnly(), false, [new QuoteDiagnostic(code, default, message)]);
    }
}

internal sealed class QuotePatternList(int[] items, TemplateListShape shape, SyntaxTree pattern)
{
    internal int[] Items { get; } = items;
    internal TemplateListShape Shape { get; } = shape;
    internal int[] MinimumRemaining { get; } = Remaining(items, pattern, segments: false);
    internal int[] SegmentsRemaining { get; } = Remaining(items, pattern, segments: true);

    static int[] Remaining(int[] items, SyntaxTree pattern, bool segments)
    {
        var minimum = new int[items.Length + 1];
        for (int i = items.Length - 1; i >= 0; i--)
            minimum[i] = minimum[i + 1] + ((pattern.Kind(items[i]) == SyntaxKinds.ListHole) == segments ? 1 : 0);
        return minimum;
    }
}
