using System.Runtime.CompilerServices;

namespace Nitrogen;

/// <summary>
/// Observes the text indices a matcher reads. Incremental reparse reuses an old result only when an
/// edit lies outside what the parser examined for it, so tracked parses report every read; an index
/// equal to the text's length means the matcher saw the end of input.
/// </summary>
public interface ITextReads
{
    void Read(int index);
}

/// <summary>Ignores reads: a plain parse's matchers instantiate with this and pay nothing.</summary>
public struct NoReads : ITextReads
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Read(int index) { }
}

/// <summary>One past the furthest index read.</summary>
public struct FurthestRead : ITextReads
{
    public int End;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Read(int index)
    {
        if (index >= End) End = index + 1;
    }
}

/// <summary>
/// Read reports for generated token matchers. Each returns true so it can lead a condition
/// (<c>Saw(ref reads, p) &amp;&amp; p &lt; text.Length &amp;&amp; ...</c>); with <see cref="NoReads"/> it compiles away and the
/// character tests stay as tight as a plain matcher's.
/// </summary>
public static class TokenText
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Saw<TReads>(ref TReads reads, int index) where TReads : struct, ITextReads
    {
        reads.Read(index);
        return true;
    }

    /// <summary>Reports a read of <paramref name="index"/>, or of the end of input when it lies past it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Saw<TReads>(ref TReads reads, int index, int length) where TReads : struct, ITextReads
    {
        reads.Read(index < length ? index : length);
        return true;
    }
}
