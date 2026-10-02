namespace Nitrogen.Binding;

/// <summary>
/// What one node kind binds (issue 237), emitted from the rule's binding clauses. A <c>Child</c>
/// is the index of the named element among the node's children, or -1 for the node itself.
/// </summary>
public sealed class BindingRule(BindingDeclaration? declares, BindingReference? references, bool scope, bool dynamic)
{
    public BindingDeclaration? Declares { get; } = declares;

    public BindingReference? References { get; } = references;

    /// <summary>The node opens a scope for its subtree; a declaration of the node itself goes to the enclosing scope.</summary>
    public bool Scope { get; } = scope;

    /// <summary>A declared name that contains a node of this kind is computed: it is not entered, and its scope opens for that kind.</summary>
    public bool Dynamic { get; } = dynamic;
}

public sealed class BindingDeclaration(string kind, int child, bool export, bool fileScope = false, bool sequential = false)
{
    public string Kind { get; } = kind;

    public int Child { get; } = child;

    public bool Export { get; } = export;

    /// <summary>The declaration belongs to this file's root scope, including dynamic names.</summary>
    public bool FileScope { get; } = fileScope;

    /// <summary>The declaration becomes visible after its entire node; later declarations replace it.</summary>
    public bool Sequential { get; } = sequential;
}

/// <param name="kinds">Tried in order at each level of the lookup.</param>
/// <param name="optional"><c>references?</c>: counts only when it resolves, and then hides the references inside it.</param>
public sealed class BindingReference(string[] kinds, int child, bool optional, string? qualifier = null)
{
    public IReadOnlyList<string> Kinds { get; } = kinds;

    public int Child { get; } = child;

    public bool Optional { get; } = optional;

    /// <summary><c>references … in K</c> (issue 239): the kind whose symbol's scope the name resolves in; null for a lexical lookup.</summary>
    public string? Qualifier { get; } = qualifier;
}

/// <summary>A module's <c>builtin kind [in Rule] { … }</c>.</summary>
/// <param name="scopeKind">The local kind of the rule whose scopes alone see these names; 0 when they are visible everywhere.</param>
public sealed class BuiltinSymbols(string kind, string[] names, int scopeKind = 0)
{
    public string Kind { get; } = kind;

    public IReadOnlyList<string> Names { get; } = names;

    public int ScopeKind { get; } = scopeKind;
}
