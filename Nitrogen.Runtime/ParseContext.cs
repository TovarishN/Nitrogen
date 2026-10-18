namespace Nitrogen;

/// <summary>
/// Per-thread arena and memo, reused by every parse. A nested parse gets a fresh pair. A context
/// that grew past the retain limits (a very large input) is held only weakly: back-to-back large
/// parses still reuse it, but the next full collection frees it, so one big parse does not pin its
/// scratch memory on the thread for good.
/// </summary>
internal sealed class ParseContext
{
    /// <summary>Largest arena node capacity kept for reuse (about 3.3 MB of node arrays).</summary>
    public const int RetainedArenaNodes = 1 << 17;

    /// <summary>Largest memo capacity kept for reuse (about 5.2 MB of slots).</summary>
    public const int RetainedMemoSlots = 1 << 18;

    [ThreadStatic] static ParseContext? t_cached;
    [ThreadStatic] static WeakReference<ParseContext>? t_oversized;

    public readonly BuildArena Arena = new();
    public readonly MemoTable Memo = new();

    internal static ParseContext? Cached => t_cached;

    internal static bool HasOversized => t_oversized?.TryGetTarget(out _) == true;

    public bool IsRetainable => Arena.NodeCapacity <= RetainedArenaNodes && Memo.Capacity <= RetainedMemoSlots;

    public static ParseContext Acquire()
    {
        // An oversized context still alive serves any input; prefer it so a large input does not
        // grow the small one past the limits again.
        if (t_oversized is not null && t_oversized.TryGetTarget(out var oversized))
        {
            t_oversized.SetTarget(null!);
            return oversized;
        }
        var context = t_cached;
        if (context is null) return new ParseContext();
        t_cached = null;
        return context;
    }

    public static void Release(ParseContext context)
    {
        if (context.IsRetainable) t_cached = context;
        else if (t_oversized is null) t_oversized = new WeakReference<ParseContext>(context);
        else t_oversized.SetTarget(context);
    }
}
