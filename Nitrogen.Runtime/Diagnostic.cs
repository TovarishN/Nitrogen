namespace Nitrogen;

public enum DiagnosticSeverity : byte
{
    Error,
    Warning,
    Info,
}

public enum DiagnosticCode : ushort
{
    /// <summary>Parse failed; the expected items are on the <see cref="ParseResult"/>.</summary>
    Expected = 1,
    /// <summary>Parse failed with nothing recorded as expected.</summary>
    Unexpected = 2,
    /// <summary>Tied extensible alternatives. <c>Arg0</c> is the Ambiguous node's index.</summary>
    Ambiguous = 3,
    /// <summary>Recovery inserted a Missing node (issue 235). <c>Arg0</c> indexes the expected text.</summary>
    Missing = 4,
    /// <summary>Recovery skipped input. <c>Arg0</c> indexes the expected text.</summary>
    Skipped = 5,
    /// <summary>Recovery skipped input left after the start rule.</summary>
    ExpectedEndOfInput = 6,
}

/// <summary>An allocation-free diagnostic. Format the text with <see cref="ParseResult.FormatMessage"/>.</summary>
public readonly record struct Diagnostic(
    DiagnosticCode Code, DiagnosticSeverity Severity, TextSpan Span, int Arg0 = 0, int Arg1 = 0);
