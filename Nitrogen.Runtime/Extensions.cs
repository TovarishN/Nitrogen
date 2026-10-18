namespace Nitrogen;

public enum Associativity : byte
{
    Left,
    Right,
}

/// <summary>
/// An alternative of an extension point that does not start with the point itself. The runtime
/// opens the node of <see cref="Kind"/> before calling <see cref="Parse"/> and closes it after,
/// so <see cref="Parse"/> matches only the elements. It may leave partial state on failure; the
/// runtime rolls back. A <see cref="Transparent"/> alternative has no node: its single element's
/// node stands in for it.
/// </summary>
public readonly unsafe struct PrefixExtension
{
    public PrefixExtension(string name, int kind, int precedence,
        delegate*<ref ParserState, bool> parse, AsciiSet firstChars = default, bool transparent = false)
    {
        Name = name;
        Kind = kind;
        Precedence = precedence;
        Parse = parse;
        FirstChars = firstChars;
        Transparent = transparent;
    }

    public readonly string Name;
    public readonly int Kind;
    public readonly int Precedence;
    public readonly delegate*<ref ParserState, bool> Parse;
    public readonly AsciiSet FirstChars;

    /// <summary>
    /// The runtime closes the alternative with <see cref="ParserState.CloseTransparent"/>: the
    /// <see cref="Kind"/> frame is popped and the one node <see cref="Parse"/> pushed is the result.
    /// </summary>
    public readonly bool Transparent;
}

/// <summary>
/// An alternative that starts with the point itself (<c>Expr "+" Expr</c>). The runtime opens the
/// node, adds the already-parsed left operand as its first child, and calls
/// <see cref="ParseRest"/> to match the remaining elements. <see cref="FirstChars"/> filters on
/// the first character after trivia. The right operand must be parsed with minimum precedence
/// <c>Precedence</c> for left associativity and <c>Precedence - 1</c> for right associativity.
/// </summary>
public readonly unsafe struct PostfixExtension
{
    public PostfixExtension(string name, int kind, int precedence, Associativity associativity,
        delegate*<ref ParserState, bool> parseRest, AsciiSet firstChars = default)
    {
        Name = name;
        Kind = kind;
        Precedence = precedence;
        Associativity = associativity;
        ParseRest = parseRest;
        FirstChars = firstChars;
    }

    public readonly string Name;
    public readonly int Kind;
    public readonly int Precedence;
    public readonly Associativity Associativity;
    public readonly delegate*<ref ParserState, bool> ParseRest;
    public readonly AsciiSet FirstChars;
}
