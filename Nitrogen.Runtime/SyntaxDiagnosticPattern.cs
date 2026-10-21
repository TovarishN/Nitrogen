namespace Nitrogen;

/// <summary>A grammar-authored diagnostic pattern, checked when its language is composed.</summary>
public sealed record SyntaxDiagnosticPattern(string Category, string Text,
    IReadOnlyDictionary<string, QuoteHole> Captures, QuoteHelperSource Source);

public sealed unsafe partial class Language
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<SyntaxDiagnosticPattern, Lazy<PatternCompilation>> _diagnosticPatterns = new();

    internal PatternCompilation DiagnosticPattern(SyntaxDiagnosticPattern pattern) => _diagnosticPatterns.GetOrAdd(pattern,
        p => new Lazy<PatternCompilation>(() => CompilePattern(p.Category, p.Text, p.Captures))).Value;
}
