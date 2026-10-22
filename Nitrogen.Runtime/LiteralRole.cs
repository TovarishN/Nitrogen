namespace Nitrogen;

/// <summary>
/// What a literal is for, from its place in the rule that matched it (the TextMate palette). The
/// generator classifies every literal of a module; editors colour each role with its own scope.
/// </summary>
public enum LiteralRole
{
    /// <summary>A word of a rule that lowers to an operation, outside the operation's arguments.</summary>
    Operation,
    /// <summary>A word that can start a rule that declares a symbol (<c>goal</c>, <c>syntax</c>).</summary>
    DeclarationKeyword,
    /// <summary>A word that can start any other rule (<c>reward</c>, <c>train</c>).</summary>
    SectionKeyword,
    /// <summary>A word after an earlier element of its rule (<c>sharpness</c>, <c>every</c>).</summary>
    ModifierWord,
    /// <summary>One of several bare word alternatives, not at the start of its rule (<c>xz</c>, <c>rms</c>).</summary>
    ConstantWord,
    BlockBracket,
    Parenthesis,
    SquareBracket,
    Terminator,
    Separator,
    /// <summary>Any other symbol literal (<c>&gt;</c>, <c>..</c>, <c>=</c>).</summary>
    Operator,
}
