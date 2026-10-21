namespace Nitrogen.Semantics;

/// <summary>Typed, borrowed syntax views from one successful diagnostic pattern.</summary>
public readonly struct SemanticPatternCaptures
{
    readonly FileSemantics _semantics;
    readonly IReadOnlyDictionary<string, QuoteArgument> _captures;

    internal SemanticPatternCaptures(FileSemantics semantics, IReadOnlyDictionary<string, QuoteArgument> captures)
    {
        _semantics = semantics;
        _captures = captures;
    }

    public T Scalar<T>(string name) where T : struct, ISemanticView<T>
        => View<T>(((QuoteArgument.Scalar)_captures[name]).Value);

    public IReadOnlyList<T> List<T>(string name) where T : struct, ISemanticView<T>
    {
        var nodes = ((QuoteArgument.Many)_captures[name]).Values;
        var views = new T[nodes.Count];
        for (int i = 0; i < views.Length; i++) views[i] = View<T>(nodes[i]);
        return Array.AsReadOnly(views);
    }

    T View<T>(QuoteNode node) where T : struct, ISemanticView<T>
    {
        if (!ReferenceEquals(node.Tree, _semantics.Tree))
            throw new InvalidOperationException("A diagnostic capture must belong to the checked semantic file.");
        return T.Create(_semantics, node.Node);
    }
}
