using Nitrogen.Binding;

namespace Nitrogen;

/// <summary>
/// A compiled syntax module. Each module is a singleton; its id is assigned process-wide when it
/// is constructed, so its kinds (<c>KindBase | local</c>) are the same in every language that
/// includes it.
/// </summary>
public abstract class SyntaxModule
{
    static int s_nextId; // 0 is the runtime's built-in kinds

    protected SyntaxModule(string name)
    {
        Name = name;
        Id = Interlocked.Increment(ref s_nextId);
        if (Id > 0xFFFF) throw new InvalidOperationException("Too many syntax modules in this process.");
        KindBase = Id << 16;
    }

    public string Name { get; }

    public int Id { get; }

    public int KindBase { get; }

    public abstract string GetKindName(int localKind);

    /// <summary>Declares this module's extension points and adds its extensions (to its own or other modules' points).</summary>
    public abstract void Register(ExtensionRegistry registry);

    /// <summary>The syntax rule of this module named <paramref name="name"/>, or null (issue 236: the workspace finds start rules by name).</summary>
    public virtual Rule? GetRule(string name) => null;

    /// <summary>What a node kind of this module binds, or null (issue 237).</summary>
    public virtual BindingRule? GetBinding(int localKind) => null;

    /// <summary>The module's built-in symbols (issue 237).</summary>
    public virtual IReadOnlyList<BuiltinSymbols> Builtins => [];

    /// <summary>What a node kind of this module computes, or null (issue 239).</summary>
    public virtual Nitrogen.Semantics.SemanticsRule? GetSemantics(int localKind) => null;

    /// <summary>The properties this module declares (issue 239).</summary>
    public virtual IReadOnlyList<Nitrogen.Semantics.Property> Properties => [];

    /// <summary>The symbol properties this module declares (issue 239).</summary>
    public virtual IReadOnlyList<Nitrogen.Semantics.SymbolProperty> SymbolProperties => [];

    /// <summary>Declarative typing and lowering clauses (issue 251); empty when the grammar has none.</summary>
    public virtual IReadOnlyList<Nitrogen.Semantic.DeclarativeRule> DeclarativeRules => [];

    public override string ToString() => Name;
}

/// <summary>An extensible rule declared by <see cref="Owner"/>. The id is process-wide.</summary>
public sealed class ExtensionPointDecl
{
    static int s_nextId = -1;

    public ExtensionPointDecl(SyntaxModule owner, string name)
    {
        Owner = owner;
        Name = name;
        GlobalId = Interlocked.Increment(ref s_nextId);
        if (GlobalId > MaxGlobalId)
            throw new InvalidOperationException(
                $"Too many extension points in this process (more than {MaxGlobalId}); the recovery memo key has room for 21 bits.");
    }

    /// <summary>The largest id: the recovery pass's memo key is <c>(id &lt;&lt; 10) | (bits &lt;&lt; 8) | precedence</c> (issue 235, Plan 2b).</summary>
    public const int MaxGlobalId = (1 << 21) - 1;

    public SyntaxModule Owner { get; }

    public string Name { get; }

    public int GlobalId { get; }

    public override string ToString() => Owner.Name + "." + Name;
}
