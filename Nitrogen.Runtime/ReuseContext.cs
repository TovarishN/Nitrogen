namespace Nitrogen;

/// <summary>
/// What a reparse reuses from: the previous tree and its reuse table, and the changes from its text to the
/// new one. It maps a new position to the old text and decides whether an old result's examined range
/// survived the edit.
/// </summary>
internal sealed class ReuseContext
{
    readonly TextChange[] _changes;
    readonly int[] _newStart;   // where each change starts in the new text
    readonly int[] _deltaAfter; // new − old offset for positions after each change

    public ReuseContext(SyntaxTree tree, ReuseTable table, TextChange[] changes)
    {
        Tree = tree;
        Table = table;
        _changes = changes;
        _newStart = new int[changes.Length];
        _deltaAfter = new int[changes.Length];
        int delta = 0;
        for (int i = 0; i < changes.Length; i++)
        {
            _newStart[i] = changes[i].Start + delta;
            delta += changes[i].NewLength - changes[i].OldLength;
            _deltaAfter[i] = delta;
        }
    }

    public SyntaxTree Tree { get; }

    public ReuseTable Table { get; }

    /// <summary>
    /// The old position of new position <paramref name="position"/>; false when it lies inside new text. A
    /// position right after inserted text maps to where the insertion was made.
    /// </summary>
    public bool TryMapToOld(int position, out int old)
    {
        int delta = 0;
        for (int i = 0; i < _changes.Length; i++)
        {
            int start = _newStart[i];
            if (position < start) break;
            if (position < start + _changes[i].NewLength)
            {
                old = 0;
                return false;
            }
            delta = _deltaAfter[i];
        }
        old = position - delta;
        return true;
    }

    /// <summary>
    /// Whether an edit touches the old examined range <c>[start, end)</c>: a replacement or deletion overlapping
    /// it, or an insertion strictly inside it (an insertion at its start lies before the result, one at its end
    /// after what it read).
    /// </summary>
    public bool Touches(int start, int end)
    {
        foreach (var c in _changes)
        {
            if (c.Start >= end) break;
            if (c.OldLength > 0 ? c.Start + c.OldLength > start : c.Start > start) return true;
        }
        return false;
    }
}
