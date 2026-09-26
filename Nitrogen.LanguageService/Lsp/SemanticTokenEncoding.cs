namespace Nitrogen.LanguageService.Lsp;

/// <summary>The semantic token legend and LSP's relative integer encoding (issue 238).</summary>
public static class SemanticTokenEncoding
{
    /// <summary>Every <see cref="TokenType"/> in order (a type's index is its value) and the two modifiers as bits 0 and 1.</summary>
    public static SemanticTokensLegend Legend { get; } =
        new(Enum.GetNames<TokenType>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]).ToArray(), ["declaration", "defaultLibrary"]);

    /// <summary>Five integers per token: line delta, start delta (from the previous token on the same line), length, type, modifiers.</summary>
    public static int[] Encode(IReadOnlyList<SemanticToken> tokens)
    {
        var data = new int[tokens.Count * 5];
        int line = 0, character = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            int deltaLine = token.Start.Line - line;
            data[5 * i] = deltaLine;
            data[5 * i + 1] = deltaLine == 0 ? token.Start.Character - character : token.Start.Character;
            data[5 * i + 2] = token.Length;
            data[5 * i + 3] = (int)token.Type;
            data[5 * i + 4] = (int)token.Modifiers;
            line = token.Start.Line;
            character = token.Start.Character;
        }
        return data;
    }
}
