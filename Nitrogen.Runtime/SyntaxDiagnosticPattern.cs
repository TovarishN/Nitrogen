namespace Nitrogen;

/// <summary>A grammar-authored diagnostic pattern, checked when its language is composed.</summary>
public sealed record SyntaxDiagnosticPattern(string Category, string Text,
    IReadOnlyDictionary<string, QuoteHole> Captures, QuoteHelperSource Source)
{
    /// <summary>An <c>inside</c>/<c>parent</c> clause: an ancestor that must also match.</summary>
    public SyntaxDiagnosticContext? Context { get; init; }
}

/// <summary>
/// The ancestor a diagnostic pattern requires. <c>inside</c> tries ancestors nearest first; <c>parent</c> only the
/// semantic parent. Ancestors of another category, or with recovered syntax, do not match.
/// </summary>
public sealed record SyntaxDiagnosticContext(bool ParentOnly, string Category, string Text,
    IReadOnlyDictionary<string, QuoteHole> Captures, QuoteHelperSource Source);

public sealed unsafe partial class Language
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<SyntaxDiagnosticPattern, Lazy<PatternCompilation>> _diagnosticPatterns = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<SyntaxDiagnosticContext, Lazy<PatternCompilation>> _diagnosticContexts = new();

    internal PatternCompilation DiagnosticPattern(SyntaxDiagnosticPattern pattern) => _diagnosticPatterns.GetOrAdd(pattern,
        p => new Lazy<PatternCompilation>(() => CompilePattern(p.Category, p.Text, p.Captures))).Value;

    internal PatternCompilation DiagnosticContextPattern(SyntaxDiagnosticContext context) => _diagnosticContexts.GetOrAdd(context,
        c => new Lazy<PatternCompilation>(() => CompilePattern(c.Category, c.Text, c.Captures))).Value;
}
