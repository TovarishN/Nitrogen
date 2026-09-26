namespace Nitrogen;

/// <summary>
/// Node kinds are <c>(moduleId &lt;&lt; 16) | localKind</c>. Module 0 is the runtime itself and owns
/// the built-in kinds below; syntax modules get ids from 1 when they are constructed.
/// </summary>
public static class SyntaxKinds
{
    public const int None = 0;
    /// <summary>An absent optional element. Zero-length leaf.</summary>
    public const int Empty = 1;
    /// <summary>A repetition or separated list; children are the items (and separators).</summary>
    public const int List = 2;
    /// <summary>A quoted literal or keyword matched by the grammar.</summary>
    public const int Literal = 3;
    /// <summary>Tied extensible alternatives; the children are the candidates.</summary>
    public const int Ambiguous = 4;
    /// <summary>Root of a failed parse.</summary>
    public const int Error = 5;
    /// <summary>A nested sequence inside a rule (<c>(A B)*</c> items, multi-element choice branches).</summary>
    public const int Group = 6;
    /// <summary>Input the recovery pass skipped: an arena marker only, never in a frozen tree (issue 235).</summary>
    public const int Skipped = 7;

    static readonly string[] s_names = ["None", "Empty", "List", "Literal", "Ambiguous", "Error", "Group", "Skipped"];

    public static int ModuleOf(int kind) => kind >>> 16;

    public static int LocalOf(int kind) => kind & 0xFFFF;

    public static string GetBuiltinName(int localKind) =>
        (uint)localKind < (uint)s_names.Length ? s_names[localKind] : "#" + localKind;
}

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    Missing = 1,
    Skipped = 2,
    Ambiguous = 4,
    Error = 8,
}
