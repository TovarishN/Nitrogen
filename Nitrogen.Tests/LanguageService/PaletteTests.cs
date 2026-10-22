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

    [Fact]
    public void Standard_types_have_vscode_default_scopes()
    {
        Assert.Equal("entity.name.type.class", Palette.StandardScope(TokenType.Class));
        Assert.Equal("variable.other.enummember", Palette.StandardScope(TokenType.EnumMember));
        Assert.Equal("keyword.operator", Palette.StandardScope(TokenType.Operator));
    }
}
