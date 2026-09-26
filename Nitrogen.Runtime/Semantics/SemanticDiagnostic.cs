namespace Nitrogen.Semantics;

public sealed record SemanticDiagnostic(string Code, TextSpan Span, string Message)
{
    public override string ToString() => $"{Code} {Span}: {Message}";
}

public static class SemanticCodes
{
    /// <summary>A property that depends on itself; it takes its default.</summary>
    public const string Cycle = "NS0001";

    /// <summary>A property's or check's C# threw; the property takes its default.</summary>
    public const string Failed = "NS0002";

    /// <summary>A failed check without a code of its own.</summary>
    public const string Check = "NS0100";
}
