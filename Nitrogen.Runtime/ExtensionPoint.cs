namespace Nitrogen;

/// <summary>The frozen extension tables of one extensible rule in one <see cref="Language"/>.</summary>
public sealed class ExtensionPoint
{
    const int NonAsciiBucket = 128, EndBucket = 129, BucketCount = 130;

    readonly PrefixExtension[] _prefix;
    readonly PostfixExtension[] _postfix;
    readonly int[][] _prefixByBucket;
    readonly int[][] _postfixByBucket;
    readonly bool[] _prefixCommit;
    readonly bool[] _postfixCommit;

    internal ExtensionPoint(ExtensionPointDecl decl, PrefixExtension[] prefix, PostfixExtension[] postfix)
    {
        Decl = decl;
        _prefix = prefix;
        _postfix = postfix;
        _prefixByBucket = Index(prefix.Select(p => p.FirstChars).ToArray());
        _postfixByBucket = Index(postfix.Select(p => p.FirstChars).ToArray());
        _prefixCommit = CommitEnabled(prefix.Select(p => p.FirstChars).ToArray());
        _postfixCommit = CommitEnabled(postfix.Select(p => p.FirstChars).ToArray());
    }

    public ExtensionPointDecl Decl { get; }

    public ReadOnlySpan<PrefixExtension> Prefix => _prefix;

    public ReadOnlySpan<PostfixExtension> Postfix => _postfix;

    /// <summary>The dispatch bucket of <paramref name="position"/>: its ASCII code, 128 for non-ASCII, 129 at the end.</summary>
    internal static int Bucket(ReadOnlySpan<char> text, int position) =>
        position >= text.Length ? EndBucket : text[position] < 128 ? text[position] : NonAsciiBucket;

    /// <summary>Indices into <see cref="Prefix"/> whose first characters admit the bucket, in registration order.</summary>
    internal ReadOnlySpan<int> PrefixCandidates(int bucket) => _prefixByBucket[bucket];

    internal ReadOnlySpan<int> PostfixCandidates(int bucket) => _postfixByBucket[bucket];

    /// <summary>
    /// Recovery (issue 235, Plan 2b): an alternative's failure after its leading part can only be an
    /// error when no other alternative on the same side could start at the same character.
    /// </summary>
    public bool IsCommitEnabled(int kind)
    {
        for (int i = 0; i < _prefix.Length; i++)
            if (_prefix[i].Kind == kind) return _prefixCommit[i];
        for (int i = 0; i < _postfix.Length; i++)
            if (_postfix[i].Kind == kind) return _postfixCommit[i];
        return false;
    }

    static bool[] CommitEnabled(AsciiSet[] firstChars)
    {
        var enabled = new bool[firstChars.Length];
        for (int i = 0; i < firstChars.Length; i++)
        {
            enabled[i] = !firstChars[i].IsAny;
            for (int j = 0; j < firstChars.Length && enabled[i]; j++)
                if (j != i && firstChars[i].Overlaps(firstChars[j])) enabled[i] = false;
        }
        return enabled;
    }

    static int[][] Index(AsciiSet[] firstChars)
    {
        var table = new int[BucketCount][];
        for (int bucket = 0; bucket < BucketCount; bucket++)
        {
            var candidates = new List<int>();
            for (int i = 0; i < firstChars.Length; i++)
            {
                bool admits = bucket == EndBucket
                    ? firstChars[i].IsAny
                    : firstChars[i].Matches(bucket == NonAsciiBucket ? '\u0080' : (char)bucket);
                if (admits) candidates.Add(i);
            }
            table[bucket] = candidates.ToArray();
        }
        return table;
    }
}
