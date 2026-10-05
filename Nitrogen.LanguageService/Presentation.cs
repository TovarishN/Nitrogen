namespace Nitrogen.LanguageService;

/// <summary>LSP's standard semantic token types; the order is the legend's (a type's index is its value).</summary>
public enum TokenType
{
    Namespace,
    Type,
    Class,
    Enum,
    Interface,
    Struct,
    TypeParameter,
    Parameter,
    Variable,
    Property,
    EnumMember,
    Event,
    Function,
    Method,
    Macro,
    Keyword,
    Modifier,
    Comment,
    String,
    Number,
    Regexp,
    Operator,
}

/// <summary>Semantic token modifiers, as bits in the legend's order.</summary>
[Flags]
public enum TokenModifiers
{
    None = 0,
    Declaration = 1,
    DefaultLibrary = 2,
}

/// <summary>LSP's SymbolKind values (the outline's icons).</summary>
public enum OutlineKind
{
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

/// <summary>How the editor shows one symbol kind: its token colour and its outline icon.</summary>
public readonly record struct SymbolStyle(TokenType Token, OutlineKind Outline)
{
    public static SymbolStyle Default => new(TokenType.Variable, OutlineKind.Variable);
}

/// <summary>
/// A language's symbol kind → <see cref="SymbolStyle"/> map (issue 238); an unmapped kind is a variable.
/// <paramref name="types"/> colours a token that lowers to a value of a semantic type (by its id, such as
/// <c>DateCalc.Date</c>); an unmapped type is a number, or a string for <c>Core.Text</c>.
/// </summary>
public sealed class Presentation(IReadOnlyDictionary<string, SymbolStyle> styles, IReadOnlyDictionary<string, TokenType>? types = null)
{
    public static Presentation Default { get; } = new(new Dictionary<string, SymbolStyle>());

    public SymbolStyle StyleOf(string kind) => styles.TryGetValue(kind, out var style) ? style : SymbolStyle.Default;

    public TokenType? TypeStyle(Nitrogen.Semantic.SemanticType type) =>
        types is not null && types.TryGetValue(type.Id, out var token) ? token : null;
}
