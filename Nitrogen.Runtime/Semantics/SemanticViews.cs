using System.Collections;
using Nitrogen.Binding;

namespace Nitrogen.Semantics;

/// <summary>A generated semantics struct (issue 239): a node seen with its properties.</summary>
public interface ISemanticView<TSelf> where TSelf : struct, ISemanticView<TSelf>
{
    static abstract TSelf Create(FileSemantics semantics, int node);
}

/// <summary>Any node in a semantics block: a token, or a child whose rule has no properties.</summary>
public readonly struct SemanticNode(FileSemantics semantics, int node) : ISemanticView<SemanticNode>
{
    public FileSemantics Semantics { get; } = semantics;

    public int Node { get; } = node;

    public int Kind => Semantics.Tree.Kind(Node);

    public TextSpan Span => Semantics.Tree.Span(Node);

    public string Text => Semantics.Tree.GetText(Node).ToString();

    public bool IsMissing => (Semantics.Tree.Flags(Node) & NodeFlags.Missing) != 0;

    public SemanticNode? Parent => Semantics.ParentOf(Node) is var parent && parent >= 0 ? new SemanticNode(Semantics, parent) : null;

    public T Get<T>(Property<T> property) => Semantics.Get(Node, property);

    public override string ToString() => Text;

    public static implicit operator string(SemanticNode node) => node.Text;

    public static SemanticNode Create(FileSemantics semantics, int node) => new(semantics, node);
}

/// <summary>
/// The items of a list child: <c>X*</c> and <c>X+</c> (step 1) or <c>(X; S)*</c> (step 2, separators
/// skipped). A non-negative <paramref name="inner"/> picks the item inside a group item.
/// </summary>
public readonly struct SemanticList<T>(FileSemantics semantics, int list, int step, int inner) : IReadOnlyList<T>
    where T : struct, ISemanticView<T>
{
    public int Count => (semantics.Tree.ChildCount(list) + step - 1) / step;

    public T this[int index]
    {
        get
        {
            int item = semantics.Tree.Child(list, index * step);
            return T.Create(semantics, inner < 0 ? item : semantics.Tree.Child(item, inner));
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        var self = this;
        return Enumerable.Range(0, Count).Select(i => self[i]).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A symbol seen with its properties, for grammars whose modules declare none.</summary>
public readonly struct SemanticSymbol(FileSemantics semantics, Symbol symbol)
{
    public Symbol Symbol { get; } = symbol;

    public string Name => Symbol.Name;

    public string Kind => Symbol.Kind;

    public bool IsBuiltin => Symbol.IsBuiltin;

    public T Get<T>(SymbolProperty<T> property) => semantics.GetSymbol(Symbol, property);

    public override string ToString() => Symbol.ToString();
}
