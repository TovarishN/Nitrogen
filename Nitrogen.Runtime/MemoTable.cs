namespace Nitrogen;

/// <summary>
/// Packrat memo for extensible rules, keyed by <c>(ruleKey, position)</c> and storing
/// <c>(node, end)</c>, where a negative node means "failed here". Open addressing with linear
/// probing. A slot is live only if its stamp equals the current generation, so <see cref="Clear"/>
/// is O(1) and one table serves every parse on a thread without reallocating.
/// </summary>
internal sealed class MemoTable
{
    const int InitialCapacity = 256; // power of two

    long[] _keys = new long[InitialCapacity];
    int[] _nodes = new int[InitialCapacity];
    int[] _ends = new int[InitialCapacity];
    int[] _stamps = new int[InitialCapacity];
    int _generation = 1;
    int _count;

    public int Count => _count;

    public int Capacity => _keys.Length;

    public void Clear()
    {
        _count = 0;
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamps);
            _generation = 1;
        }
    }

    public bool TryGet(int ruleKey, int position, out int node, out int end)
    {
        long key = Key(ruleKey, position);
        int mask = _keys.Length - 1;
        for (int i = Hash(key) & mask; ; i = (i + 1) & mask)
        {
            if (_stamps[i] != _generation)
            {
                node = end = 0;
                return false;
            }
            if (_keys[i] == key)
            {
                node = _nodes[i];
                end = _ends[i];
                return true;
            }
        }
    }

    public void Set(int ruleKey, int position, int node, int end)
    {
        if ((_count + 1) * 2 > _keys.Length) Grow();
        Insert(Key(ruleKey, position), node, end);
    }

    void Insert(long key, int node, int end)
    {
        int mask = _keys.Length - 1;
        int i = Hash(key) & mask;
        while (_stamps[i] == _generation && _keys[i] != key) i = (i + 1) & mask;
        if (_stamps[i] != _generation) _count++;
        _keys[i] = key;
        _nodes[i] = node;
        _ends[i] = end;
        _stamps[i] = _generation;
    }

    void Grow()
    {
        var (keys, nodes, ends, stamps, generation) = (_keys, _nodes, _ends, _stamps, _generation);
        int capacity = keys.Length * 2;
        _keys = new long[capacity];
        _nodes = new int[capacity];
        _ends = new int[capacity];
        _stamps = new int[capacity];
        _generation = 1;
        _count = 0;
        for (int i = 0; i < keys.Length; i++)
            if (stamps[i] == generation) Insert(keys[i], nodes[i], ends[i]);
    }

    static long Key(int ruleKey, int position) => ((long)ruleKey << 32) | (uint)position;

    static int Hash(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 32);
}
