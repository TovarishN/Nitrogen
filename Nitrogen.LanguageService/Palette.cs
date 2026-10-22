using System.Text.Json.Nodes;

namespace Nitrogen.LanguageService;

/// <summary>
/// One palette token type: its legend <paramref name="Name"/>, the TextMate <paramref name="Scope"/> editors colour it
/// with, and the standard type a client that did not opt in sees instead (null: such a client gets no token).
/// </summary>
public sealed record PaletteEntry(string Name, string Scope, TokenType? Fallback);

/// <summary>
/// The TextMate palette: token types named after scopes, so the server's colours are a theme's TextMate colours.
/// A client that sends <c>initializationOptions.paletteTokens</c> gets these types after the standard ones in its
/// legend; any other client gets each entry's <see cref="PaletteEntry.Fallback"/>.
/// </summary>
public static partial class Palette
{
    static readonly PaletteEntry[] s_general =
    [
        new("operationWord", "entity.name.function", TokenType.Function),
        new("declarationKeyword", "storage.type", TokenType.Keyword),
        new("sectionKeyword", "keyword.other", TokenType.Keyword),
        new("modifierWord", "keyword.operator.word", TokenType.Keyword),
        new("constantWord", "constant.language", TokenType.EnumMember),
        new("blockBracket", "punctuation.section.block", null),
        new("parenthesis", "punctuation.section.parens", null),
        new("squareBracket", "punctuation.section.brackets", null),
        new("terminator", "punctuation.terminator", null),
        new("separator", "punctuation.separator", null),
        new("operator", "keyword.operator", TokenType.Operator),
    ];

    /// <summary>Every palette type in legend order: the general entries, then the language-specific ones.</summary>
    public static IReadOnlyList<PaletteEntry> Entries { get; } = [.. s_general, .. LanguageEntries()];

    // After Entries: static initializers run in text order.
    static readonly Dictionary<string, PaletteEntry> s_byName = Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

    public static PaletteEntry Named(string name) => s_byName[name];

    /// <summary>Whether <paramref name="name"/> is a palette entry.</summary>
    public static bool Contains(string name) => s_byName.ContainsKey(name);

    public static PaletteEntry Of(LiteralRole role) => s_general[role switch
    {
        LiteralRole.Operation => 0,
        LiteralRole.DeclarationKeyword => 1,
        LiteralRole.SectionKeyword => 2,
        LiteralRole.ModifierWord => 3,
        LiteralRole.ConstantWord => 4,
        LiteralRole.BlockBracket => 5,
        LiteralRole.Parenthesis => 6,
        LiteralRole.SquareBracket => 7,
        LiteralRole.Terminator => 8,
        LiteralRole.Separator => 9,
        _ => 10,
    }];

    /// <summary>The scope VS Code colours a standard token type with when no theme rule names the type.</summary>
    public static string StandardScope(TokenType type) => type switch
    {
        TokenType.Namespace => "entity.name.namespace",
        TokenType.Type => "entity.name.type",
        TokenType.Class => "entity.name.type.class",
        TokenType.Enum => "entity.name.type.enum",
        TokenType.Interface => "entity.name.type.interface",
        TokenType.Struct => "entity.name.type.struct",
        TokenType.TypeParameter => "entity.name.type.parameter",
        TokenType.Parameter => "variable.parameter",
        TokenType.Variable => "variable.other.readwrite",
        TokenType.Property => "variable.other.property",
        TokenType.EnumMember => "variable.other.enummember",
        TokenType.Event => "variable.other.event",
        TokenType.Function => "entity.name.function",
        TokenType.Method => "entity.name.function.member",
        TokenType.Macro => "entity.name.function.preprocessor",
        TokenType.Keyword => "keyword",
        TokenType.Modifier => "storage.modifier",
        TokenType.Comment => "comment",
        TokenType.String => "string",
        TokenType.Number => "constant.numeric",
        TokenType.Regexp => "constant.regexp",
        _ => "keyword.operator",
    };

    /// <summary>
    /// A VS Code extension's <c>contributes.semanticTokenTypes</c> and <c>contributes.semanticTokenScopes</c> for the palette,
    /// for every language. The types have no superType: a theme's colour for a supertype would win over the scope.
    /// </summary>
    public static JsonObject VsCodeContributions() => new()
    {
        ["semanticTokenTypes"] = new JsonArray(Entries.Select(e => (JsonNode)new JsonObject { ["id"] = e.Name, ["description"] = e.Scope }).ToArray()),
        ["semanticTokenScopes"] = new JsonArray(new JsonObject
        {
            ["scopes"] = new JsonObject(Entries.Select(e => KeyValuePair.Create(e.Name, (JsonNode?)new JsonArray(e.Scope)))),
        }),
    };

    /// <summary>Language-specific entries (the .ngr grammar's scopes), appended after the general ones.</summary>
    static IEnumerable<PaletteEntry> LanguageEntries() => NgrEntries();
}
