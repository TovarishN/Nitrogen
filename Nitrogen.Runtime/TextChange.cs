namespace Nitrogen;

/// <summary>
/// One edit of a text, in the old text's coordinates: <paramref name="OldLength"/> characters at
/// <paramref name="Start"/> were replaced by <paramref name="NewLength"/> new ones. A reparse takes its
/// changes sorted by start and non-overlapping.
/// </summary>
public readonly record struct TextChange(int Start, int OldLength, int NewLength)
{
    /// <summary>
    /// The single change turning <paramref name="before"/> into <paramref name="after"/>: everything between their
    /// common prefix and common suffix. Exact about which text is unchanged, whatever edits produced it.
    /// </summary>
    public static TextChange Between(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        int prefix = before.AsSpan().CommonPrefixLength(after);
        int max = Math.Min(before.Length, after.Length) - prefix, suffix = 0;
        while (suffix < max && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        return new TextChange(prefix, before.Length - prefix - suffix, after.Length - prefix - suffix);
    }
}
