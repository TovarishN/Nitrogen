namespace Nitrogen.Binding;

/// <summary>A declared or built-in name (issue 237). A built-in has no path, span or node.</summary>
public sealed class Symbol
{
    internal Symbol(string kind, string name, string? path, TextSpan nameSpan, int node, int scope, bool exported, int? availableFrom = null)
    {
        Kind = kind;
        Name = name;
        Path = path;
        NameSpan = nameSpan;
        Node = node;
        Scope = scope;
        IsExported = exported;
        AvailableFrom = availableFrom;
    }

    public string Kind { get; }

    public string Name { get; }

    /// <summary>The declaring document; null for a built-in.</summary>
    public string? Path { get; }

    public TextSpan NameSpan { get; }

    /// <summary>The declaring node; -1 for a built-in.</summary>
    public int Node { get; }

    public bool IsExported { get; }

    /// <summary>Sequential declarations become visible at this source offset; null means whole-scope visibility.</summary>
    public int? AvailableFrom { get; }

    public bool IsBuiltin => Path is null;

    /// <summary>The scope the symbol is declared in (an index into its file's scopes); -1 for a built-in.</summary>
    internal int Scope { get; }

    public override string ToString() => IsBuiltin ? $"builtin {Kind} {Name}" : $"{Kind} {Name} ({Path} {NameSpan})";
}

/// <summary>A name the text uses (issue 237).</summary>
public sealed class Reference
{
    internal Reference(string path, int index, IReadOnlyList<string> kinds, string name, TextSpan nameSpan, int node,
        int scope, bool optional, int guard, int enclosing = -1, string? qualifier = null)
    {
        Path = path;
        Index = index;
        Kinds = kinds;
        Name = name;
        NameSpan = nameSpan;
        Node = node;
        Scope = scope;
        IsOptional = optional;
        Guard = guard;
        Enclosing = enclosing;
        Qualifier = qualifier;
    }

    public string Path { get; }

    /// <summary>The reference's position in its file's <see cref="FileBinding.References"/>.</summary>
    public int Index { get; }

    public IReadOnlyList<string> Kinds { get; }

    public string Name { get; }

    public TextSpan NameSpan { get; }

    public int Node { get; }

    public bool IsOptional { get; }

    internal int Scope { get; }

    /// <summary>The nearest enclosing optional reference (an index), or -1.</summary>
    internal int Guard { get; }

    /// <summary>The nearest enclosing reference (an index), or -1: a qualified reference looks into what it names.</summary>
    internal int Enclosing { get; }

    /// <summary>For a qualified reference (issue 239), the kind of the enclosing reference whose symbol's scope it resolves in.</summary>
    public string? Qualifier { get; }

    public override string ToString() => $"{string.Join("|", Kinds)} {Name}{(IsOptional ? "?" : "")} ({Path} {NameSpan})";
}

public static class BindingCodes
{
    public const string Unresolved = "NB0001";
    public const string Duplicate = "NB0002";
    public const string AmbiguousExport = "NB0003";
    public const string NotVisible = "NB0004";
}

public sealed record BindingDiagnostic(string Code, TextSpan Span, string Message)
{
    public override string ToString() => $"{Code} {Span}: {Message}";
}

/// <summary>
/// A reference whose name is Missing (issue 238): recovery inserted it. It reports nothing; its kinds
/// and position tell completion what may go there.
/// </summary>
public sealed class Hole
{
    internal Hole(string path, IReadOnlyList<string> kinds, int position, int scope, int node = -1, int enclosing = -1, string? qualifier = null)
    {
        Path = path;
        Kinds = kinds;
        Position = position;
        Scope = scope;
        Node = node;
        Enclosing = enclosing;
        Qualifier = qualifier;
    }

    public string Path { get; }

    public IReadOnlyList<string> Kinds { get; }

    public int Position { get; }

    /// <summary>The Missing node: its expected type tells completion what fits.</summary>
    public int Node { get; }

    /// <summary>The nearest enclosing reference, as for <see cref="Reference"/>.</summary>
    internal int Enclosing { get; }

    /// <summary>A qualified hole's kind (issue 239): candidates come from that symbol's scope.</summary>
    public string? Qualifier { get; }

    internal int Scope { get; }
}
