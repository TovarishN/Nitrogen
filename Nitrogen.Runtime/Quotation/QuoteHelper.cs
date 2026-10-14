namespace Nitrogen;

public sealed record QuoteHelperSource(string Path, int Start, int Length);
public sealed record QuoteHelper(string Name, string Category, string Text,
    IReadOnlyDictionary<string, QuoteHole> Parameters, QuoteHelperSource Source)
{
    public QuoteHelperMatchSpec? Match { get; init; }
}
public sealed record QuoteHelperError(QuoteHelper Helper, QuoteDiagnostic Diagnostic)
{
    public QuoteHelperSource? Source { get; init; }
}
public sealed record QuoteHelperMatchSpec(string Selector, string SelectorCategory, IReadOnlyList<QuoteMatchArmSpec> Arms);
public sealed record QuoteMatchArmSpec(string Pattern, IReadOnlyDictionary<string, QuoteHole> Captures,
    string Result, QuoteHelperSource PatternSource, QuoteHelperSource ResultSource);
