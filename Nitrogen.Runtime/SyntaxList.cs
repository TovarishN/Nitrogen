namespace Nitrogen;

/// <summary>Items of a List node (<c>X*</c>, <c>X+</c>).</summary>
public readonly struct SyntaxList<T>(SyntaxTree tree, int listIndex) where T : struct, ISyntaxView<T>
{
    public int Count => tree.ChildCount(listIndex);

    public T this[int i] => T.Create(tree, tree.Child(listIndex, i));

    public Enumerator GetEnumerator() => new(tree, listIndex, step: 1);

    public struct Enumerator(SyntaxTree tree, int listIndex, int step)
    {
        readonly int _childCount = tree.ChildCount(listIndex);
        int _k = -step;

        public readonly T Current => T.Create(tree, tree.Child(listIndex, _k));

        public bool MoveNext() => (_k += step) < _childCount;
    }
}

/// <summary>Items of a separated List node (<c>(X; S)*</c>): items at even child indices, separators at odd.</summary>
public readonly struct SeparatedList<T>(SyntaxTree tree, int listIndex) where T : struct, ISyntaxView<T>
{
    public int Count => (tree.ChildCount(listIndex) + 1) / 2;

    public int SeparatorCount => tree.ChildCount(listIndex) / 2;

    public T this[int i] => T.Create(tree, tree.Child(listIndex, 2 * i));

    public Token Separator(int i) => new(tree, tree.Child(listIndex, 2 * i + 1));

    public SyntaxList<T>.Enumerator GetEnumerator() => new(tree, listIndex, step: 2);
}

/// <summary>An optional element: either the element or an Empty node.</summary>
public readonly struct Optional<T>(SyntaxTree tree, int index) where T : struct, ISyntaxView<T>
{
    public bool HasValue => tree.Kind(index) != SyntaxKinds.Empty;

    public T Value => HasValue ? T.Create(tree, index) : throw new InvalidOperationException("Optional element is absent.");
}
