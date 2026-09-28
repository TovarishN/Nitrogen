namespace Nitrogen.Semantic;

public enum DeclarativeForm
{
    None,
    Operation,
    Literal,
}

/// <summary>One syntax kind's declarative typing and lowering clauses (issue 251), emitted by the generator as data.</summary>
public sealed class DeclarativeRule
{
    public DeclarativeRule(int localKind, DeclarativeForm form, string? target, int[] arguments,
        string? declaredType, int declaredTypeChild)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        LocalKind = localKind;
        Form = form;
        Target = target;
        Arguments = Array.AsReadOnly((int[])arguments.Clone());
        DeclaredType = declaredType;
        DeclaredTypeChild = declaredTypeChild;
    }

    public int LocalKind { get; }

    public DeclarativeForm Form { get; }

    /// <summary>Operation: the operation ID. Literal: the type name. None: null.</summary>
    public string? Target { get; }

    /// <summary>Operation: argument child indices in parameter order. Literal: one child index, -1 for the node itself.</summary>
    public IReadOnlyList<int> Arguments { get; }

    /// <summary>A qualified type the declared symbol has; null when absent or read from <see cref="DeclaredTypeChild"/>.</summary>
    public string? DeclaredType { get; }

    /// <summary>The child whose text names the declared symbol's type; -1 when absent.</summary>
    public int DeclaredTypeChild { get; }
}
