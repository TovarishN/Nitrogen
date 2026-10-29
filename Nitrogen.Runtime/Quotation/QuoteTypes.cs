using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>The category and cardinality of a named quotation input.</summary>
public sealed record QuoteHole(string Category, bool IsList)
{
    /// <summary>A qualified named pattern the captured syntax must match; its category replaces <see cref="Category"/>.</summary>
    public string? PatternName { get; init; }

    /// <summary>With <see cref="PatternName"/>: search the hole node and its descendants, capturing the node found.</summary>
    public bool Descendant { get; init; }

    public static QuoteHole Node(string category) => new(category, false);
    public static QuoteHole List(string category) => new(category, true);
    public static QuoteHole Pattern(string qualifiedName) => new("", false) { PatternName = qualifiedName };
    public static QuoteHole PatternList(string qualifiedName) => new("", true) { PatternName = qualifiedName };
    public static QuoteHole DescendantOf(string qualifiedName) => new("", false) { PatternName = qualifiedName, Descendant = true };
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
