namespace Nitrogen;

/// <summary>
/// A rule result a tracked parse recorded: the rule (a syntax rule's kind, or an extensible call's memo
/// key tagged with <see cref="ExtensibleTag"/>), where it was called, where it ended, one past the
/// furthest index it examined, and its node (an arena index until the freeze maps it).
/// </summary>
internal readonly record struct ReuseRecord(long Key, int Start, int End, int ExaminedEnd, int Node, ulong Context = 0)
{
    /// <summary>Set on extensible calls' keys so they never collide with a syntax rule's kind.</summary>
    public const long ExtensibleTag = 1L << 40;

    public bool IsExtensible => Key >= ExtensibleTag;
}
