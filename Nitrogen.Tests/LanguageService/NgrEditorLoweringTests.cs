using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The ngr language with the Grammar catalog: hover says what a node lowers to, colouring is unchanged.</summary>
public sealed class NgrEditorLoweringTests
{
    const string Uri = "file:///w/m.ngr";
    const string Source = "syntax module M { token T = ['a'..'z']+; syntax R = \"r\" Name:T; }";

    static DocumentPosition At(string text, int shift = 0) => new(0, Source.IndexOf(text, StringComparison.Ordinal) + shift);

    [Fact]
    public void Ngr_tokens_are_not_coloured_from_lowering()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, Source);

        var tokens = service.SemanticTokens(Uri);

        Assert.DoesNotContain(tokens, token => token.Type == TokenType.Function);
        Assert.DoesNotContain(tokens, token => token.Start == At("Name:T", "Name:".Length) && token.Type == TokenType.String);
    }

    [Fact]
    public void Hover_over_a_grammar_node_names_what_it_lowers_to()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, Source);

        var hover = service.Hover(Uri, At("token"));

        Assert.NotNull(hover);
        Assert.Contains("`Grammar.TokenRule`", hover!.Markdown, StringComparison.Ordinal);
        Assert.Empty(service.Diagnostics(Uri));
    }
}
