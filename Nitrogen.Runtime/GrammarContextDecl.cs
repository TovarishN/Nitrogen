namespace Nitrogen;

/// <summary>A Boolean grammar flag owned by a syntax module; each Language assigns its own bit.</summary>
public sealed class GrammarContextDecl
{
    public GrammarContextDecl(SyntaxModule owner, string name)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Owner = owner;
        Name = name;
    }
    public SyntaxModule Owner { get; }
    public string Name { get; }
    public string QualifiedName => Owner.Name + "." + Name;
    public override string ToString() => QualifiedName;
}

/// <summary>Optional grammar context metadata, kept separate from SyntaxModule's existing member names.</summary>
public interface IGrammarContextModule
{
    IReadOnlyList<GrammarContextDecl> ContextDeclarations { get; }
    IReadOnlyList<GrammarContextDecl> ContextReferences { get; }
}
