using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The TextMate palette: token types named after scopes, which opted-in clients colour by scope.</summary>
public sealed class PaletteTests
{
    [Fact]
    public void Every_role_has_an_entry()
    {
        foreach (var role in Enum.GetValues<LiteralRole>())
            Assert.Contains(Palette.Of(role), Palette.Entries);
        Assert.Equal("storage.type", Palette.Of(LiteralRole.DeclarationKeyword).Scope);
        Assert.Null(Palette.Of(LiteralRole.BlockBracket).Fallback);
    }

    [Fact]
    public void Names_are_valid_vscode_token_type_ids()
    {
        foreach (var entry in Palette.Entries)
            Assert.Matches(new Regex(@"^\w+[-_\w+]*$"), entry.Name);
        Assert.Equal(Palette.Entries.Count, Palette.Entries.Select(e => e.Name).Distinct().Count());
    }

    static string Repository([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Fact]
    public void Contributions_map_each_type_to_its_scope()
    {
        var contributions = Palette.VsCodeContributions();
        var types = contributions["semanticTokenTypes"]!.AsArray();
        Assert.Equal(Palette.Entries.Select(e => e.Name), types.Select(t => (string?)t!["id"]));
        Assert.All(types, t => Assert.Null(t!["superType"])); // a theme rule for a supertype would override the scope
        var scopes = contributions["semanticTokenScopes"]![0]!["scopes"]!;
        Assert.Equal("storage.type", (string?)scopes["declarationKeyword"]![0]);
        Assert.Null(contributions["semanticTokenScopes"]![0]!["language"]);
    }

    [Fact]
    public void Generic_extension_declares_the_palette()
    {
        var contributes = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository(), "editors", "vscode", "package.json")))!["contributes"]!;
        var palette = Palette.VsCodeContributions();
        Assert.True(JsonNode.DeepEquals(palette["semanticTokenTypes"], contributes["semanticTokenTypes"]));
        Assert.True(JsonNode.DeepEquals(palette["semanticTokenScopes"], contributes["semanticTokenScopes"]));
        Assert.Contains("paletteTokens: true", File.ReadAllText(Path.Combine(Repository(), "editors", "vscode", "src", "extension.ts")));
    }

    [Fact]
    public void Standard_types_have_vscode_default_scopes()
    {
        Assert.Equal("entity.name.type.class", Palette.StandardScope(TokenType.Class));
        Assert.Equal("variable.other.enummember", Palette.StandardScope(TokenType.EnumMember));
        Assert.Equal("keyword.operator", Palette.StandardScope(TokenType.Operator));
    }
}
