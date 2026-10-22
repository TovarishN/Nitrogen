using Nitrogen.LanguageService;
using Nitrogen.Tests.PaletteGrammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Semantic tokens carry palette entries: names and lowering first, then each literal's grammar role.</summary>
public sealed class PaletteTokenTests
{
    const string Uri = "file:///w/a.pal";

    const string Source = """
        goal g { 1 sharpness 2; }
        train { 1 hold 2 .. 3, 4 kg }
        policy p
        anchored xz;
        hold (1) [2]
        """;

    static readonly Language PaletteLanguage = new LanguageBuilder().Add(PaletteModule.Instance).Build();

    static IReadOnlyList<SemanticToken> Tokens(string source)
    {
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("palette", PaletteLanguage, new Dictionary<string, Rule> { [".pal"] = PaletteModule.File }));
        using var service = new NitrogenLanguageService(registry);
        service.Open(Uri, 1, source);
        return service.SemanticTokens(Uri);
    }

    /// <summary>The palette entry of the token at the <paramref name="occurrence"/>th <paramref name="text"/> (from 0).</summary>
    static string? PaletteAt(IReadOnlyList<SemanticToken> tokens, string source, string text, int occurrence = 0)
    {
        int offset = -1;
        for (int i = 0; i <= occurrence; i++) offset = source.IndexOf(text, offset + 1, StringComparison.Ordinal);
        var lines = source[..offset].Split('\n');
        var at = new DocumentPosition(lines.Length - 1, lines[^1].Length);
        var token = Assert.Single(tokens, t => t.Start == at);
        Assert.Equal(text.Length, token.Length);
        return token.Palette;
    }

    [Theory]
    [InlineData("goal", "declarationKeyword")]
    [InlineData("{", "blockBracket")]
    [InlineData("sharpness", "modifierWord")]
    [InlineData(";", "terminator")]
    [InlineData("train", "sectionKeyword")]
    [InlineData("..", "operator")]
    [InlineData(",", "separator")]
    [InlineData("kg", "constantWord")]
    [InlineData("xz", "constantWord")]
    [InlineData("(", "parenthesis")]
    [InlineData("[", "squareBracket")]
    public void Literal_roles_reach_tokens(string text, string palette) =>
        Assert.Equal(palette, PaletteAt(Tokens(Source), Source, text));

    [Fact]
    public void Same_word_takes_each_rules_role()
    {
        var tokens = Tokens(Source);
        Assert.Equal("modifierWord", PaletteAt(tokens, Source, "hold", 0));
        Assert.Equal("sectionKeyword", PaletteAt(tokens, Source, "hold", 1));
    }

    [Fact]
    public void List_and_alias_literals_use_the_rule_role()
    {
        var tokens = Tokens(Source);
        Assert.Equal("modifierWord", PaletteAt(tokens, Source, "hold", 0)); // inside ("hold" Num)*
        Assert.Equal("constantWord", PaletteAt(tokens, Source, "kg")); // from the Unit alias
    }

    [Fact]
    public void Recovered_document_still_colours()
    {
        const string broken = "goal g { 1 sharpness 2;\ntrain { }";
        var tokens = Tokens(broken);
        Assert.Equal("declarationKeyword", PaletteAt(tokens, broken, "goal"));
    }
}
