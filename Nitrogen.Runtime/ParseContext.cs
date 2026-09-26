namespace Nitrogen;

/// <summary>Per-thread arena and memo, reused by every parse. A nested parse gets a fresh pair.</summary>
internal sealed class ParseContext
{
    [ThreadStatic] static ParseContext? t_cached;

    public readonly BuildArena Arena = new();
    public readonly MemoTable Memo = new();

    public static ParseContext Acquire()
    {
        var context = t_cached;
        if (context is null) return new ParseContext();
        t_cached = null;
        return context;
    }

    public static void Release(ParseContext context) => t_cached = context;
}
