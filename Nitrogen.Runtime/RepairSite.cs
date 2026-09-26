namespace Nitrogen;

/// <summary>
/// What the recovery pass (issue 235) looks for at a position: literals, keywords (with a word
/// boundary), tokens (through their generated matchers) and the end of input. Generated per commit
/// point from the grammar's FIRST and FOLLOW sets.
/// </summary>
public sealed unsafe class LookaheadSet
{
    readonly string[] _literals;
    readonly string[] _keywords;
    readonly delegate*<ReadOnlySpan<char>, int, int>[] _tokens;
    readonly bool _end;

    public LookaheadSet(string[] literals, string[] keywords, delegate*<ReadOnlySpan<char>, int, int>[] tokens, bool end)
    {
        _literals = literals;
        _keywords = keywords;
        _tokens = tokens;
        _end = end;
    }

    public bool Matches(ReadOnlySpan<char> text, int position)
    {
        if (position >= text.Length) return _end;
        var rest = text.Slice(position);
        foreach (string literal in _literals)
            if (rest.StartsWith(literal)) return true;
        foreach (string keyword in _keywords)
            if (rest.StartsWith(keyword) && (rest.Length == keyword.Length || !ParserState.IsIdentifierPart(rest[keyword.Length])))
                return true;
        for (int i = 0; i < _tokens.Length; i++)
            if (_tokens[i](text, position) > position) return true;
        return false;
    }
}

/// <summary>A place a skip may stop: element <see cref="Element"/> of the sequence can start with <see cref="First"/>.</summary>
public readonly struct SyncPoint
{
    public SyncPoint(int element, LookaheadSet first)
    {
        Element = element;
        First = first;
    }

    public int Element { get; }

    public LookaheadSet First { get; }
}

/// <summary>
/// A commit point (issue 235): element <see cref="Element"/> of a generated sequence, whose failure
/// in the recovery pass <see cref="ParserState.Recover"/> repairs.
/// </summary>
public sealed class RepairSite
{
    public RepairSite(int element, string expected, LookaheadSet insert, SyncPoint[] sync)
    {
        Element = element;
        Expected = expected;
        Insert = insert;
        Sync = sync;
    }

    public int Element { get; }

    /// <summary>The element as a diagnostic phrase: <c>'}'</c>, <c>String</c>, <c>Lifecycle</c>.</summary>
    public string Expected { get; }

    /// <summary>What may follow the element: input already starting with it means the element alone is missing.</summary>
    public LookaheadSet Insert { get; }

    /// <summary>
    /// Where a skip may stop, in priority order: the element itself, later elements, and, for the
    /// loop of a list just before the element, the list's item (<c>Element - 1</c>).
    /// </summary>
    public SyncPoint[] Sync { get; }
}
