namespace Nitrogen;

/// <summary>
/// Packrat memo for extensible rules, keyed by <c>(ruleKey, position)</c> and storing
/// <c>(node, end, examined)</c>, where a negative node means "failed here" and examined is one past
/// the furthest index the computation read (a tracked parse replays it on a hit; 0 otherwise). Open
/// addressing with linear probing over one array of 24-byte entries, grown at three-quarters load. A slot is live only
/// if its stamp equals the current generation, so <see cref="Clear"/> is O(1) however large the
/// table has grown and one table serves every parse on a thread without reallocating.
/// </summary>
internal sealed class MemoTable
{
    const int InitialCapacity = 256; // power of two
    internal const double MaxLoad = 0.75;

    struct Entry
    {
        public long Key;
        public int Node;
        public int End;
        public int Stamp;
        public int Examined;
    }

    Entry[] _entries = new Entry[InitialCapacity];
    int _limit = Limit(InitialCapacity); // most entries held before growing
    int _generation = 1;
    int _count;

    public int Count => _count;

    public int Capacity => _entries.Length;

    /// <summary>Test hook: lets a test place the generation next to its wrap point.</summary>
    internal int Generation
    {
        get => _generation;
        set => _generation = value;
    }

    public void Clear()
    {
        _count = 0;
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_entries);
            _generation = 1;
        }
    }

    /// <summary>Copy only successful strict recognition; failures must run again under recovery.</summary>
    public void CopySuccessesTo(MemoTable destination)
    {
        destination.Clear();
        foreach (var entry in _entries)
            if (entry.Stamp == _generation && entry.Node >= 0)
                destination.Set((int)(entry.Key >> 32), (int)entry.Key, entry.Node, entry.End, entry.Examined);
    }

    public bool TryGet(int ruleKey, int position, out int node, out int end) => TryGet(ruleKey, position, out node, out end, out _);

    public bool TryGet(int ruleKey, int position, out int node, out int end, out int examined)
    {
        long key = Key(ruleKey, position);
        var entries = _entries;
        int mask = entries.Length - 1;
        for (int i = Hash(key) & mask; ; i = (i + 1) & mask)
        {
            ref Entry e = ref entries[i];
            if (e.Stamp != _generation)
            {
                node = end = examined = 0;
                return false;
            }
            if (e.Key == key)
            {
                node = e.Node;
                end = e.End;
                examined = e.Examined;
                return true;
            }
        }
    }

    internal bool Contains(int ruleKey, int position) => TryGet(ruleKey, position, out _, out _);

    public void Set(int ruleKey, int position, int node, int end, int examined = 0)
    {
        if (_count + 1 > _limit) Grow();
        Insert(_entries, Key(ruleKey, position), node, end, examined);
    }

    void Insert(Entry[] entries, long key, int node, int end, int examined)
    {
        int mask = entries.Length - 1;
        int i = Hash(key) & mask;
        while (entries[i].Stamp == _generation && entries[i].Key != key) i = (i + 1) & mask;
        if (entries[i].Stamp != _generation) _count++;
        entries[i] = new Entry { Key = key, Node = node, End = end, Stamp = _generation, Examined = examined };
    }

    void Grow()
    {
        var old = _entries;
        int generation = _generation;
        var entries = new Entry[old.Length * 2];
        _generation = 1;
        _count = 0;
        foreach (var e in old)
            if (e.Stamp == generation) Insert(entries, e.Key, e.Node, e.End, e.Examined);
        _entries = entries;
        _limit = Limit(entries.Length);
    }

    static int Limit(int capacity) => (int)(capacity * MaxLoad);

    static long Key(int ruleKey, int position) => ((long)ruleKey << 32) | (uint)position;

    static int Hash(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 32);
}
