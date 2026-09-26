namespace Nitrogen;

/// <summary>A typed, allocation-free view of one node. Generated per rule and alternative.</summary>
public interface ISyntaxView<TSelf> where TSelf : struct, ISyntaxView<TSelf>
{
    static abstract bool Is(SyntaxTree tree, int index);

    static abstract TSelf Create(SyntaxTree tree, int index);

    SyntaxTree Tree { get; }

    int Index { get; }
}

public static class SyntaxView
{
    public static bool TryCast<T>(SyntaxTree tree, int index, out T view) where T : struct, ISyntaxView<T>
    {
        if (T.Is(tree, index))
        {
            view = T.Create(tree, index);
            return true;
        }
        view = default;
        return false;
    }

    public static T Cast<T>(SyntaxTree tree, int index) where T : struct, ISyntaxView<T> =>
        T.Is(tree, index)
            ? T.Create(tree, index)
            : throw new InvalidCastException(
                $"Node {index} ({SyntaxDumper.KindName(tree.Kind(index), tree.Language)}) is not a {typeof(T).Name}.");
}

/// <summary>An untyped view of any node. The default value is the null node (<see cref="IsNull"/>).</summary>
public readonly struct SyntaxNode(SyntaxTree tree, int index) : ISyntaxView<SyntaxNode>
{
    public SyntaxTree Tree { get; } = tree;
    public int Index { get; } = index;
    public bool IsNull => Tree is null;
    public int Kind => Tree.Kind(Index);
    public TextSpan Span => Tree.Span(Index);
    public NodeFlags Flags => Tree.Flags(Index);
    public int ChildCount => Tree.ChildCount(Index);
    public bool IsMissing => (Flags & NodeFlags.Missing) != 0;
    public bool IsSkipped => (Flags & NodeFlags.Skipped) != 0;
    public ReadOnlySpan<char> Text => Tree.GetText(Index);
    public SyntaxNode Child(int k) => new(Tree, Tree.Child(Index, k));

    public SyntaxNode Parent
    {
        get
        {
            int parent = Tree.Parent(Index);
            return parent < 0 ? default : new SyntaxNode(Tree, parent);
        }
    }

    public ChildEnumerable Children => new(Tree, Index);

    public override string ToString() => IsNull ? "" : Tree.GetText(Index).ToString();

    public static bool Is(SyntaxTree tree, int index) => true;

    public static SyntaxNode Create(SyntaxTree tree, int index) => new(tree, index);

    public readonly struct ChildEnumerable(SyntaxTree tree, int index)
    {
        public Enumerator GetEnumerator() => new(tree, index);
    }

    public struct Enumerator(SyntaxTree tree, int index)
    {
        readonly int _count = tree.ChildCount(index);
        int _k = -1;

        public readonly SyntaxNode Current => new(tree, tree.Child(index, _k));

        public bool MoveNext() => ++_k < _count;
    }
}

/// <summary>A leaf: token, literal or keyword (not Empty, not an empty List).</summary>
public readonly struct Token(SyntaxTree tree, int index) : ISyntaxView<Token>
{
    public SyntaxTree Tree { get; } = tree;
    public int Index { get; } = index;
    public int Kind => Tree.Kind(Index);
    public TextSpan Span => Tree.Span(Index);
    public ReadOnlySpan<char> Text => Tree.GetText(Index);

    public bool IsMissing => (Tree.Flags(Index) & NodeFlags.Missing) != 0;

    public override string ToString() => Tree.GetText(Index).ToString();

    public static bool Is(SyntaxTree tree, int index)
    {
        int kind = tree.Kind(index);
        return tree.ChildCount(index) == 0 && kind != SyntaxKinds.Empty && kind != SyntaxKinds.List;
    }

    public static Token Create(SyntaxTree tree, int index) => new(tree, index);
}
