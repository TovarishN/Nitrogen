using System.Collections;

namespace Nitrogen.Grammar;

/// <summary>
/// An immutable array with value equality, so records that hold collections compare by content.
/// The default value is an empty array.
/// </summary>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    readonly T[]? _items;

    public EquatableArray(T[] items)
    {
        _items = items;
    }

    public int Count => _items?.Length ?? 0;

    public T this[int index] => (_items ?? throw new IndexOutOfRangeException())[index];

    public bool Equals(EquatableArray<T> other)
    {
        int count = Count;
        if (count != other.Count) return false;
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < count; i++)
            if (!comparer.Equals(_items![i], other._items![i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            if (_items is not null)
                foreach (var item in _items) hash = hash * 31 + (item is null ? 0 : item.GetHashCode());
            return hash;
        }
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public static implicit operator EquatableArray<T>(T[] items) => new(items);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? new T[0])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "[" + string.Join(", ", this) + "]";
}
