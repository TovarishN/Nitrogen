namespace Nitrogen.LanguageService.Lsp;

/// <summary>The semantic token legend and LSP's relative integer encoding (issue 238).</summary>
public static class SemanticTokenEncoding
{
    static readonly string[] s_standard = Enum.GetNames<TokenType>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]).ToArray();
    static readonly SemanticTokensLegend s_standardLegend = new(s_standard, ["declaration", "defaultLibrary"]);
    static readonly SemanticTokensLegend s_paletteLegend = new([.. s_standard, .. Palette.Entries.Select(e => e.Name)], ["declaration", "defaultLibrary"]);
    static readonly Dictionary<string, int> s_paletteIndex = Palette.Entries.Select((e, i) => (e.Name, i)).ToDictionary(p => p.Name, p => s_standard.Length + p.i, StringComparer.Ordinal);

    /// <summary>
    /// Every <see cref="TokenType"/> in order (a type's index is its value), then, for a <paramref name="palette"/> client,
    /// every <see cref="Palette"/> entry; the two modifiers are bits 0 and 1.
    /// </summary>
    public static SemanticTokensLegend Legend(bool palette) => palette ? s_paletteLegend : s_standardLegend;

    /// <summary>
    /// Five integers per token: line delta, start delta (from the previous token on the same line), length, type, modifiers.
    /// A palette token is its palette type for a <paramref name="palette"/> client and its fallback otherwise, or nothing without one.
    /// </summary>
    public static int[] Encode(IReadOnlyList<SemanticToken> tokens, bool palette)
    {
        var data = new List<int>(tokens.Count * 5);
        int line = 0, character = 0;
        foreach (var token in tokens)
        {
            int type;
            if (token.Palette is null) type = (int)token.Type;
            else if (palette) type = s_paletteIndex[token.Palette];
            else if (Palette.Named(token.Palette).Fallback is { } fallback) type = (int)fallback;
            else continue;
            int deltaLine = token.Start.Line - line;
            data.Add(deltaLine);
            data.Add(deltaLine == 0 ? token.Start.Character - character : token.Start.Character);
            data.Add(token.Length);
            data.Add(type);
            data.Add((int)token.Modifiers);
            line = token.Start.Line;
            character = token.Start.Character;
        }
        return data.ToArray();
    }
}
