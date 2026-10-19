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
    readonly int _count;

    ReuseTable(ReuseRecord[] entries, int count)
    {
        _entries = entries;
        _count = count;
    }

    public static ReuseTable Empty { get; } = new([], 0);

    public int Count => _count;

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
        var recorded = new ReuseRecord[arena.RecordCount];
        var starts = new int[arena.RecordCount];
        int count = 0;
        for (int i = 0; i < arena.RecordCount; i++)
        {
            var r = arena.Records[i];
            int node = (uint)r.Node < (uint)arenaToTree.Length ? arenaToTree[r.Node] : -1;
            if (node < 0) continue;
            recorded[count] = r with { Node = node };
            starts[count++] = r.Start;
        }
        // Records are appended as rules close, not in text order; sorting by an int key avoids a comparer per step.
        // Entries for one rule at one start (a rollback that kept its nodes, a carried-over copy) describe the same
        // parse of the same text, so their order does not matter.
        Array.Sort(starts, recorded, 0, count);
        if (previous is null || arena.Grafts.Count == 0) return new ReuseTable(recorded, count);
        return CarryOver(arena, arenaToTree, previous, recorded, count);
    }

    /// <summary>
    /// This parse's sorted <paramref name="recorded"/> entries merged with the previous entries inside each grafted
    /// block, moved with it. Each block's entries are a sorted slice of the previous table moved by a constant, and
    /// blocks are visited in text order, so one pass merges them into a single array.
    /// </summary>
    static ReuseTable CarryOver(BuildArena arena, int[] arenaToTree, ReuseTable previous, ReuseRecord[] recorded, int recordedCount)
    {
        var grafts = new List<(Graft Graft, int To)>(arena.Grafts.Count);
        foreach (var (arenaNode, graft) in arena.Grafts)
        {
            // A graft rolled back by backtracking may have had its arena slot reused by another node.
            if (arenaNode >= arena.NodeCount || arena.Kind[arenaNode] != SyntaxKinds.Graft || arena.ChildStart[arenaNode] != graft.Root) continue;
            int to = arenaToTree[arenaNode];
            if (to >= 0) grafts.Add((graft, to));
        }
        grafts.Sort((x, y) => (x.Graft.EntryStart + x.Graft.PosDelta).CompareTo(y.Graft.EntryStart + y.Graft.PosDelta));
        int capacity = recordedCount;
        foreach (var (graft, _) in grafts)
            capacity += SliceEnd(previous, graft) - previous.FirstAtOrAfter(graft.EntryStart);
        var entries = GC.AllocateUninitializedArray<ReuseRecord>(capacity);
        int count = 0, next = 0, lastStart = int.MinValue;
        bool sorted = true;
        var old = previous._entries;
        foreach (var (graft, to) in grafts)
        {
            int indexDelta = to - graft.Root, posDelta = graft.PosDelta, end = SliceEnd(previous, graft);
            int root = graft.Root, rootEnd = graft.Root + graft.Size;
            for (int i = previous.FirstAtOrAfter(graft.EntryStart); i < end; i++)
            {
                ref readonly var e = ref old[i];
                if (e.Node < root || e.Node >= rootEnd) continue;
                int start = e.Start + posDelta;
                while (next < recordedCount && recorded[next].Start <= start) entries[count++] = recorded[next++];
                sorted &= start >= lastStart;
                lastStart = start;
                entries[count++] = new ReuseRecord(e.Key, start, e.End + posDelta, e.ExaminedEnd + posDelta, e.Node + indexDelta);
            }
        }
        while (next < recordedCount) entries[count++] = recorded[next++];
        // A block reused twice (an ambiguity copied it once per occurrence) can interleave; sort that rare case.
        if (!sorted)
        {
            var starts = new int[count];
            for (int i = 0; i < count; i++) starts[i] = entries[i].Start;
            Array.Sort(starts, entries, 0, count);
        }
        return new ReuseTable(entries, count);
    }

    /// <summary>One past the previous table's last entry that starts within a graft's old call range.</summary>
    static int SliceEnd(ReuseTable previous, Graft graft) => previous.FirstAtOrAfter(graft.EntryEnd + 1);

    /// <summary>The index of the first entry starting at or after <paramref name="start"/>.</summary>
    public int FirstAtOrAfter(int start)
    {
        int lo = 0, hi = _count;
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
        for (int i = FirstAtOrAfter(start); i < _count && _entries[i].Start == start; i++)
            if (_entries[i].Key == key) found = i;
        return found;
    }
}
