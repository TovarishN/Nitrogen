namespace Nitrogen;

public sealed record QuoteHelperSource(string Path, int Start, int Length);
public sealed record QuoteHelper(string Name, string Category, string Text,
    IReadOnlyDictionary<string, QuoteHole> Parameters, QuoteHelperSource Source);
public sealed record QuoteHelperError(QuoteHelper Helper, QuoteDiagnostic Diagnostic);
