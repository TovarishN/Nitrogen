using System.Buffers;

namespace Nitrogen;

/// <summary>
/// Immutable concrete syntax tree in preorder struct-of-arrays form over pooled arrays. Node 0 is
/// the root. The arrays go back to the pool on <see cref="Dispose"/>; any access after that
/// throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class SyntaxTree : IDisposable
{
    int[]? _kind, _start, _length, _childStart, _childCount, _children, _parent;
    NodeFlags[]? _flags;
    TextSpan[]? _trivia, _skipped;
    int _triviaCount, _skippedCount;

    internal SyntaxTree(string text, Language? language, int nodeCount,
        int[] kind, int[] start, int[] length, NodeFlags[] flags,
        int[] childStart, int[] childCount, int[] children,
        TextSpan[] trivia, int triviaCount, TextSpan[] skipped, int skippedCount)
    {
        Text = text;
        Language = language;
        NodeCount = nodeCount;
        _kind = kind;
        _start = start;
        _length = length;
        _flags = flags;
        _childStart = childStart;
        _childCount = childCount;
        _children = children;
        _trivia = trivia;
        _triviaCount = triviaCount;
        _skipped = skipped;
        _skippedCount = skippedCount;
    }

    public string Text { get; }

    public Language? Language { get; }

    public int NodeCount { get; }

    public int Root => 0;

    public bool IsDisposed => _kind is null;

    public int Kind(int node)
    {
        Check(node);
        return _kind![node];
    }

    public TextSpan Span(int node)
    {
        Check(node);
        return new TextSpan(_start![node], _length![node]);
    }

    public NodeFlags Flags(int node)
    {
        Check(node);
        return _flags![node];
    }

    public int ChildCount(int node)
    {
        Check(node);
        return _childCount![node];
    }

    public int Child(int node, int index)
    {
        Check(node);
        if ((uint)index >= (uint)_childCount![node]) throw new ArgumentOutOfRangeException(nameof(index));
        return _children![_childStart![node] + index];
    }

    /// <summary>The parent's index, or -1 for the root. The parent table is built on first use.</summary>
    public int Parent(int node)
    {
        Check(node);
        _parent ??= BuildParents();
        return _parent[node];
    }

    public ReadOnlySpan<char> GetText(int node)
    {
        var span = Span(node);
        return Text.AsSpan(span.Start, span.Length);
    }

    /// <summary>Gaps between tokens (whitespace and comments), in text order.</summary>
    public ReadOnlySpan<TextSpan> Trivia => (_trivia ?? throw Disposed()).AsSpan(0, _triviaCount);

    /// <summary>Input the recovery pass skipped (issue 235), in text order. It is part of <see cref="Trivia"/> too.</summary>
    public ReadOnlySpan<TextSpan> SkippedSpans => (_skipped ?? throw Disposed()).AsSpan(0, _skippedCount);

    /// <summary>The skipped spans inside <paramref name="node"/>'s span.</summary>
    public ReadOnlySpan<TextSpan> SkippedTrivia(int node)
    {
        var span = Span(node);
        var all = SkippedSpans;
        int from = 0;
        while (from < all.Length && all[from].Start < span.Start) from++;
        int to = from;
        while (to < all.Length && all[to].End <= span.End) to++;
        return all.Slice(from, to - from);
    }

    public void Dispose()
    {
        if (_kind is null) return;
        ArrayPool<int>.Shared.Return(_kind);
        ArrayPool<int>.Shared.Return(_start!);
        ArrayPool<int>.Shared.Return(_length!);
        ArrayPool<int>.Shared.Return(_childStart!);
        ArrayPool<int>.Shared.Return(_childCount!);
        ArrayPool<int>.Shared.Return(_children!);
        if (_parent is not null) ArrayPool<int>.Shared.Return(_parent);
        ArrayPool<NodeFlags>.Shared.Return(_flags!);
        ArrayPool<TextSpan>.Shared.Return(_trivia!);
        if (_skipped!.Length > 0) ArrayPool<TextSpan>.Shared.Return(_skipped);
        _kind = _start = _length = _childStart = _childCount = _children = _parent = null;
        _flags = null;
        _trivia = _skipped = null;
        _triviaCount = _skippedCount = 0;
    }

    void Check(int node)
    {
        if (_kind is null) throw Disposed();
        if ((uint)node >= (uint)NodeCount) throw new ArgumentOutOfRangeException(nameof(node));
    }

    int[] BuildParents()
    {
        var parent = ArrayPool<int>.Shared.Rent(NodeCount);
        parent[0] = -1;
        for (int n = 0; n < NodeCount; n++)
        {
            int first = _childStart![n];
            for (int k = 0; k < _childCount![n]; k++) parent[_children![first + k]] = n;
        }
        return parent;
    }

    static ObjectDisposedException Disposed() => new(nameof(SyntaxTree));
}
