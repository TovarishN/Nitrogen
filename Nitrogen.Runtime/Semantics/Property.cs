using Nitrogen.Binding;

namespace Nitrogen.Semantics;

/// <summary>
/// A node property (issue 239). The module that owns the rule declares it once; alternatives in
/// other modules share the object, which is its identity.
/// </summary>
public abstract class Property(string name, bool inherited, bool hover = false, bool expected = false)
{
    public string Name { get; } = name;

    /// <summary>An <c>in</c> property: the parent assigns it. Otherwise <c>out</c>: the node computes it.</summary>
    public bool IsInherited { get; } = inherited;

    /// <summary>The language's hover property: its value is shown over a node.</summary>
    public bool IsHover { get; } = hover;

    /// <summary>The language's expected-type property: completion ranks names that fit it first.</summary>
    public bool IsExpected { get; } = expected;

    /// <summary>The value at a node, boxed, for tools that do not know its type.</summary>
    public abstract object? Read(FileSemantics semantics, int node);

    public override string ToString() => Name;
}

public sealed class Property<T>(string name, bool inherited, Func<T> defaultValue, bool hover = false, bool expected = false)
    : Property(name, inherited, hover, expected)
{
    /// <summary>The value when nothing defines it, for a Missing node, and after a cycle or an exception.</summary>
    public T Default() => defaultValue();

    public override object? Read(FileSemantics semantics, int node) => semantics.Get(node, this);
}

/// <summary>A property of symbols of <see cref="Kinds"/> (issue 239); the declaring rule sets it.</summary>
public abstract class SymbolProperty(string name, string[] kinds)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> Kinds { get; } = kinds;

    /// <summary>The value for a symbol, boxed, for tools that do not know its type.</summary>
    public abstract object? Read(FileSemantics semantics, Symbol symbol);

    public override string ToString() => Name;
}

public sealed class SymbolProperty<T>(string name, string[] kinds, Func<string, string, T> defaultValue) : SymbolProperty(name, kinds)
{
    /// <summary>The value for a built-in, for another kind, or when the declaring rule sets nothing.</summary>
    public T Default(string kind, string symbolName) => defaultValue(kind, symbolName);

    public override object? Read(FileSemantics semantics, Symbol symbol) => semantics.GetSymbol(symbol, this);
}
