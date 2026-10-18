namespace Nitrogen;

/// <summary>
/// Per-thread arena and memo, reused by every parse. A nested parse gets a fresh pair. A context
/// that grew past the retain limits (a very large input) is dropped instead of cached, so one big
/// parse does not pin its scratch memory on the thread for good.
/// </summary>
internal sealed class ParseContext
{
    /// <summary>Largest arena node capacity kept for reuse (about 3.3 MB of node arrays).</summary>
    public const int RetainedArenaNodes = 1 << 17;

    /// <summary>Largest memo capacity kept for reuse (about 5.2 MB of slots).</summary>
    public const int RetainedMemoSlots = 1 << 18;

    [ThreadStatic] static ParseContext? t_cached;

    public readonly BuildArena Arena = new();
    public readonly MemoTable Memo = new();

    internal static ParseContext? Cached => t_cached;

    public bool IsRetainable => Arena.NodeCapacity <= RetainedArenaNodes && Memo.Capacity <= RetainedMemoSlots;

    public static ParseContext Acquire()
    {
        var context = t_cached;
        if (context is null) return new ParseContext();
        t_cached = null;
        return context;
    }

    public static void Release(ParseContext context)
    {
        if (context.IsRetainable) t_cached = context;
    }
}
