namespace Nitrogen;

/// <summary>
/// Node kinds are <c>(moduleId &lt;&lt; 16) | localKind</c>. Module 0 is the runtime itself and owns
/// the built-in kinds below; syntax modules get ids from 1 when they are constructed.
/// </summary>
public static class SyntaxKinds
{
    public const int None = 0;
    /// <summary>Formerly an absent optional element; no longer emitted (an absent optional is a null slot, <see cref="SyntaxTree.NoNode"/>).</summary>
    public const int Empty = 1;
    /// <summary>A non-empty repetition or separated list; children are the items (and separators). An empty list is a null slot.</summary>
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

    /// <summary>A template's hole (<c>$Name</c>): a leaf standing for a node of the rule the hole names (spec: quasiquote rewrites).</summary>
    public const int Hole = 8;

    public const int ListHole = 9;

    /// <summary>
    /// A reparse's stand-in for a subtree reused from the previous tree: an arena marker only, which the freeze
    /// expands into a copy of that subtree; never in a frozen tree.
    /// </summary>
    public const int Graft = 10;

    static readonly string[] s_names = ["None", "Empty", "List", "Literal", "Ambiguous", "Error", "Group", "Skipped", "Hole", "ListHole", "Graft"];

    public static int ModuleOf(int kind) => kind >>> 16;

    public static int LocalOf(int kind) => kind & 0xFFFF;

    public static string GetBuiltinName(int localKind) =>
        (uint)localKind < (uint)s_names.Length ? s_names[localKind] : "#" + localKind;

    /// <summary>Whether a literal's text is a word (a letter or <c>_</c>, then letters, digits or <c>_</c>) rather than punctuation.</summary>
    public static bool IsWordText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !(char.IsLetter(text[0]) || text[0] == '_')) return false;
        foreach (char c in text)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }
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
