namespace Nitrogen;

/// <summary>
/// The rule results an incremental parse recorded, keyed by <c>(rule, start)</c> and sorted by start: what an
/// incremental reparse may reuse. Each entry's node is an index into the result's tree. A syntax rule's key is
/// its kind and an extensible call's is its memo key with <see cref="ReuseRecord.ExtensibleTag"/>, so the key
/// carries the call context (minimum precedence) a result depends on. Results are keyed by rule, not read off
/// node kinds: one node can be the result of several rules (an alias, a transparent alternative, a one-item
/// list's item).
/// </summary>
internal sealed class ReuseTable
{
    readonly ReuseRecord[] _entries;

    ReuseTable(ReuseRecord[] entries) => _entries = entries;

    public static ReuseTable Empty { get; } = new([]);

    public int Count => _entries.Length;

    public ReuseRecord this[int index] => _entries[index];

    /// <summary>
    /// The table of a tracked parse just frozen: the arena's records whose nodes made it into the tree, with
    /// <paramref name="arenaToTree"/> mapping arena nodes to tree indices (-1 for nodes the tree dropped).
    /// </summary>
    public static ReuseTable From(BuildArena arena, int[] arenaToTree)
    {
        var entries = new List<ReuseRecord>(arena.RecordCount);
        for (int i = 0; i < arena.RecordCount; i++)
        {
            var r = arena.Records[i];
            int node = (uint)r.Node < (uint)arenaToTree.Length ? arenaToTree[r.Node] : -1;
            if (node >= 0) entries.Add(r with { Node = node });
        }
        return new ReuseTable(Sorted(entries));
    }

    static ReuseRecord[] Sorted(List<ReuseRecord> entries)
    {
        var array = entries.ToArray();
        // Stable by start; a rule recorded twice at one start (a rollback that kept its nodes) keeps the last.
        Array.Sort(array, (x, y) => x.Start.CompareTo(y.Start));
        return array;
    }

    /// <summary>The index of the entry for <paramref name="key"/> at <paramref name="start"/>, or -1.</summary>
    public int Find(long key, int start)
    {
        int lo = 0, hi = _entries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid].Start < start) lo = mid + 1;
            else hi = mid;
        }
        int found = -1;
        for (int i = lo; i < _entries.Length && _entries[i].Start == start; i++)
            if (_entries[i].Key == key) found = i;
        return found;
    }
}
