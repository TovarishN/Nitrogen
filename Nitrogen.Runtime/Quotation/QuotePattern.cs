namespace Nitrogen;

/// <summary>A reusable typed syntax pattern. Matching neither rewrites nor lowers the input.</summary>
public sealed class QuotePattern
{
    readonly ParseResult _parsed;
    readonly string _category;
    readonly IReadOnlyDictionary<string, QuoteHole> _captures;
    bool _disposed;

    internal QuotePattern(ParseResult parsed, string category, IReadOnlyDictionary<string, QuoteHole> captures)
    {
        _parsed = parsed;
        _category = category;
        _captures = captures;
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
        if (!ReferenceEquals(language, input.Tree.Language) || !language.AcceptsQuoteNode(_category, input.Tree.Kind(input.Node)))
            return QuotePatternMatcher.Invalid("NQP0005", "Input category or language snapshot does not match.");
        var invalid = QuotePatternMatcher.Validate(input, language, resolve);
        return invalid ?? new QuotePatternMatcher(_parsed.Tree, _captures, resolve).Match(input);
    }
}
