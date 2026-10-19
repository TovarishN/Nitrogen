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
    /// <remarks>
    /// A reparse also carries over <paramref name="previous"/>'s entries inside each grafted block, moved with it:
    /// what they examined lies inside what the block's own call examined, which no edit touched, so the next reparse
    /// can reuse inside a block reused this time.
    /// </remarks>
    public static ReuseTable From(BuildArena arena, int[] arenaToTree, ReuseTable? previous = null)
    {
        var entries = new List<ReuseRecord>(arena.RecordCount);
        for (int i = 0; i < arena.RecordCount; i++)
        {
            var r = arena.Records[i];
            int node = (uint)r.Node < (uint)arenaToTree.Length ? arenaToTree[r.Node] : -1;
            if (node >= 0) entries.Add(r with { Node = node });
        }
        if (previous is not null)
        {
            foreach (var (arenaNode, graft) in arena.Grafts)
            {
                // A graft rolled back by backtracking may have had its arena slot reused by another node.
                if (arenaNode >= arena.NodeCount || arena.Kind[arenaNode] != SyntaxKinds.Graft || arena.ChildStart[arenaNode] != graft.Root) continue;
                int to = arenaToTree[arenaNode];
                if (to < 0) continue;
                int indexDelta = to - graft.Root, posDelta = graft.PosDelta;
                for (int i = previous.FirstAtOrAfter(graft.EntryStart); i < previous.Count && previous[i].Start <= graft.EntryEnd; i++)
                {
                    var e = previous[i];
                    if (e.Node < graft.Root || e.Node >= graft.Root + graft.Size) continue;
                    entries.Add(new ReuseRecord(e.Key, e.Start + posDelta, e.End + posDelta, e.ExaminedEnd + posDelta, e.Node + indexDelta));
                }
            }
        }
        return new ReuseTable(Sorted(entries));
    }

    static ReuseRecord[] Sorted(List<ReuseRecord> entries)
    {
        var array = entries.ToArray();
        // By start only: entries for one rule at one start (a rollback that kept its nodes, a carried-over copy)
        // describe the same parse of the same text, so any of them may be reused.
        Array.Sort(array, (x, y) => x.Start.CompareTo(y.Start));
        return array;
    }

    /// <summary>The index of the first entry starting at or after <paramref name="start"/>.</summary>
    public int FirstAtOrAfter(int start)
    {
        int lo = 0, hi = _entries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid].Start < start) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>The index of the entry for <paramref name="key"/> at <paramref name="start"/>, or -1.</summary>
    public int Find(long key, int start)
    {
        int found = -1;
        for (int i = FirstAtOrAfter(start); i < _entries.Length && _entries[i].Start == start; i++)
            if (_entries[i].Key == key) found = i;
        return found;
    }
}
