namespace Nitrogen;

/// <summary>
/// Packrat memo for extensible rules, keyed by <c>(ruleKey, position)</c> and storing
/// <c>(node, end)</c>, where a negative node means "failed here". Open addressing with linear
/// probing over one array of 16-byte entries, grown at three-quarters load. An empty slot holds
/// <see cref="Empty"/> as its key, which no <c>(ruleKey, position)</c> can produce, so
/// <see cref="Clear"/> only rewrites keys, and only when something was stored.
/// </summary>
internal sealed class MemoTable
{
    const int InitialCapacity = 256; // power of two
    internal const double MaxLoad = 0.75;
    const long Empty = long.MinValue; // both key halves are non-negative, so the sign bit is never set

    struct Entry
    {
        public long Key;
        public int Node;
        public int End;
    }

    Entry[] _entries = NewEntries(InitialCapacity);
    int _count;

    public int Count => _count;

    public int Capacity => _entries.Length;

    public void Clear()
    {
        if (_count == 0) return;
        _count = 0;
        var entries = _entries;
        for (int i = 0; i < entries.Length; i++) entries[i].Key = Empty;
    }

    public bool TryGet(int ruleKey, int position, out int node, out int end)
    {
        long key = Key(ruleKey, position);
        var entries = _entries;
        int mask = entries.Length - 1;
        for (int i = Hash(key) & mask; ; i = (i + 1) & mask)
        {
            ref Entry e = ref entries[i];
            if (e.Key == key)
            {
                node = e.Node;
                end = e.End;
                return true;
            }
            if (e.Key == Empty)
            {
                node = end = 0;
                return false;
            }
        }
    }

    internal bool Contains(int ruleKey, int position) => TryGet(ruleKey, position, out _, out _);

    public void Set(int ruleKey, int position, int node, int end)
    {
        if (_count + 1 > _entries.Length * MaxLoad) Grow();
        Insert(_entries, Key(ruleKey, position), node, end);
    }

    void Insert(Entry[] entries, long key, int node, int end)
    {
        int mask = entries.Length - 1;
        int i = Hash(key) & mask;
        while (entries[i].Key != Empty && entries[i].Key != key) i = (i + 1) & mask;
        if (entries[i].Key == Empty) _count++;
        entries[i] = new Entry { Key = key, Node = node, End = end };
    }

    void Grow()
    {
        var old = _entries;
        var entries = NewEntries(old.Length * 2);
        _count = 0;
        foreach (var e in old)
            if (e.Key != Empty) Insert(entries, e.Key, e.Node, e.End);
        _entries = entries;
    }

    static Entry[] NewEntries(int capacity)
    {
        var entries = new Entry[capacity];
        for (int i = 0; i < entries.Length; i++) entries[i].Key = Empty;
        return entries;
    }

    static long Key(int ruleKey, int position) => ((long)ruleKey << 32) | (uint)position;

    static int Hash(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 32);
}
