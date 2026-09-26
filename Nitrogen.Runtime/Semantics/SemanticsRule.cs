namespace Nitrogen.Semantics;

/// <summary>What one node kind computes (issue 239), emitted from its semantics block.</summary>
public sealed class SemanticsRule(SemanticsOut[] outs, SemanticsIn[] ins, SemanticsSymbol[] symbols, SemanticsCheck[] checks)
{
    public IReadOnlyList<SemanticsOut> Outs { get; } = outs;

    public IReadOnlyList<SemanticsIn> Ins { get; } = ins;

    public IReadOnlyList<SemanticsSymbol> Symbols { get; } = symbols;

    public IReadOnlyList<SemanticsCheck> Checks { get; } = checks;
}

/// <summary>An <c>out</c> property the node defines.</summary>
public abstract class SemanticsOut(Property property)
{
    public Property Property { get; } = property;

    public static SemanticsOut<T> Of<T>(Property<T> property, Func<FileSemantics, int, T> compute) => new(property, compute);
}

public sealed class SemanticsOut<T>(Property<T> property, Func<FileSemantics, int, T> compute) : SemanticsOut(property)
{
    public T Compute(FileSemantics semantics, int node) => compute(semantics, node);
}

/// <summary>
/// An <c>in</c> property the node assigns to a child. <see cref="Child"/> is the element's index among
/// the node's children, and <see cref="Inner"/> the item's index inside a group element, or -1. A list
/// element assigns every item. The value is computed on the parent.
/// </summary>
public abstract class SemanticsIn(Property property, int child, int inner)
{
    public Property Property { get; } = property;

    public int Child { get; } = child;

    public int Inner { get; } = inner;

    public static SemanticsIn<T> Of<T>(Property<T> property, int child, int inner, Func<FileSemantics, int, T> compute) =>
        new(property, child, inner, compute);
}

public sealed class SemanticsIn<T>(Property<T> property, int child, int inner, Func<FileSemantics, int, T> compute)
    : SemanticsIn(property, child, inner)
{
    public T Compute(FileSemantics semantics, int parent) => compute(semantics, parent);
}

/// <summary>A property of the symbol the node declares.</summary>
public abstract class SemanticsSymbol(SymbolProperty property)
{
    public SymbolProperty Property { get; } = property;

    public static SemanticsSymbol<T> Of<T>(SymbolProperty<T> property, Func<FileSemantics, int, T> compute) => new(property, compute);
}

public sealed class SemanticsSymbol<T>(SymbolProperty<T> property, Func<FileSemantics, int, T> compute) : SemanticsSymbol(property)
{
    public T Compute(FileSemantics semantics, int node) => compute(semantics, node);
}

/// <summary>A <c>check</c>: when the condition is false, the message is reported at the node under the code.</summary>
public sealed class SemanticsCheck(string code, Func<FileSemantics, int, bool> condition, Func<FileSemantics, int, string> message,
    Func<FileSemantics, int, TextSpan>? at = null)
{
    public string Code { get; } = code;

    public bool Holds(FileSemantics semantics, int node) => condition(semantics, node);

    public string Message(FileSemantics semantics, int node) => message(semantics, node);

    /// <summary>Where the diagnostic sits: the child <c>at</c> names, else the node (issue 241).</summary>
    public TextSpan Span(FileSemantics semantics, int node) => at is null ? semantics.Tree.Span(node) : at(semantics, node);
}
