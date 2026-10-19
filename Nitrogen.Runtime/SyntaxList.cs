namespace Nitrogen;

/// <summary>
/// Reads a list slot. An empty list is a null slot (<see cref="SyntaxTree.NoNode"/>), and a one-item
/// list is its item: any node there that is not a <see cref="SyntaxKinds.List"/> (the generator keeps
/// the List whenever an item could itself be one).
/// </summary>
public static class ListSlot
{
    /// <summary>The slot's children: items, and for a separated list the separators between them.</summary>
    public static int Count(SyntaxTree tree, int slot) =>
        slot == SyntaxTree.NoNode ? 0 : tree.Kind(slot) == SyntaxKinds.List ? tree.ChildCount(slot) : 1;

    /// <summary>Child <paramref name="k"/> of the slot; the slot itself for a one-item list.</summary>
    public static int Child(SyntaxTree tree, int slot, int k) =>
        tree.Kind(slot) == SyntaxKinds.List ? tree.Child(slot, k)
        : k == 0 ? slot : throw new ArgumentOutOfRangeException(nameof(k));
}

/// <summary>Items of a list slot (<c>X*</c>, <c>X+</c>); see <see cref="ListSlot"/>.</summary>
public readonly struct SyntaxList<T>(SyntaxTree tree, int listIndex) where T : struct, ISyntaxView<T>
{
    public int Count => ListSlot.Count(tree, listIndex);

    public T this[int i] => T.Create(tree, ListSlot.Child(tree, listIndex, i));

    public Enumerator GetEnumerator() => new(tree, listIndex, step: 1);

    public struct Enumerator(SyntaxTree tree, int listIndex, int step)
    {
        readonly int _childCount = ListSlot.Count(tree, listIndex);
        int _k = -step;

        public readonly T Current => T.Create(tree, ListSlot.Child(tree, listIndex, _k));

        public bool MoveNext() => (_k += step) < _childCount;
    }
}

/// <summary>
/// Items of a separated list slot (<c>(X; S)*</c>): items at even child indices, separators at odd.
/// See <see cref="ListSlot"/>; a one-item list has no separators.
/// </summary>
public readonly struct SeparatedList<T>(SyntaxTree tree, int listIndex) where T : struct, ISyntaxView<T>
{
    public int Count => (ListSlot.Count(tree, listIndex) + 1) / 2;

    public int SeparatorCount => ListSlot.Count(tree, listIndex) / 2;

    public T this[int i] => T.Create(tree, ListSlot.Child(tree, listIndex, 2 * i));

    public Token Separator(int i) => new(tree, ListSlot.Child(tree, listIndex, 2 * i + 1));

    public SyntaxList<T>.Enumerator GetEnumerator() => new(tree, listIndex, step: 2);
}

/// <summary>An optional element: either the element or a null slot (<see cref="SyntaxTree.NoNode"/>).</summary>
public readonly struct Optional<T>(SyntaxTree tree, int index) where T : struct, ISyntaxView<T>
{
    public bool HasValue => index != SyntaxTree.NoNode;

    public T Value => HasValue ? T.Create(tree, index) : throw new InvalidOperationException("Optional element is absent.");
}
