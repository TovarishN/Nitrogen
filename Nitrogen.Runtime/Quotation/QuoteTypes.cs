using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>The category and cardinality of a named quotation input.</summary>
public sealed record QuoteHole(string Category, bool IsList)
{
    public static QuoteHole Node(string category) => new(category, false);
    public static QuoteHole List(string category) => new(category, true);
}

public sealed record QuoteNode(SyntaxTree Tree, int Node, SourceOrigin? Origin = null);
public abstract record QuoteArgument
{
    public sealed record Scalar(QuoteNode Value) : QuoteArgument;
    public sealed record Many(IReadOnlyList<QuoteNode> Values) : QuoteArgument;
    public static QuoteArgument Node(QuoteNode value) => new Scalar(value);
    public static QuoteArgument List(IReadOnlyList<QuoteNode> values) => new Many(values);
}

public sealed record QuoteDiagnostic(string Code, TextSpan Span, string Message, string? Hole = null);
public sealed record TemplateListShape(string Category, int MinimumCount, string? Separator);

public sealed record QuoteOptions(SourceOrigin? TemplateOrigin = null);
