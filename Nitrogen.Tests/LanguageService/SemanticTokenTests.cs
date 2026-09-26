using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Colour from tree and binding (issue 238), on the Scopes test language.</summary>
public class SemanticTokenTests
{
    static string[] Tokens(string text)
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, text);
        return service.SemanticTokens("file:///w/a.scopes")
            .Select(t => $"{t.Start.Line}:{t.Start.Character}:{t.Length}:{t.Type}:{t.Modifiers}").ToArray();
    }

    [Fact]
    public void Keywords_operators_numbers_names_and_comments()
    {
        Assert.Equal(new[]
        {
            "0:0:7:Comment:None",
            "1:0:4:Keyword:None", "1:5:1:Class:Declaration", "1:7:1:Operator:None",
            "1:9:3:Keyword:None", "1:13:1:Variable:Declaration", "1:15:1:Operator:None", "1:17:1:Number:None", "1:18:1:Operator:None",
            "1:20:3:Keyword:None", "1:24:1:Variable:Declaration", "1:26:1:Operator:None", "1:28:1:Variable:None",
            "1:30:1:Operator:None", "1:32:2:Variable:DefaultLibrary", "1:34:1:Operator:None", "1:36:1:Operator:None",
        }, Tokens("// head\nunit a { let x = 1; let y = x + pi; }"));
    }

    [Fact]
    public void A_dotted_builtin_is_one_token_and_an_unresolved_name_is_not_coloured()
    {
        var tokens = Tokens("unit a { let c = sys.clock; let d = nope; }");
        Assert.Contains("0:17:9:Variable:DefaultLibrary", tokens);
        Assert.DoesNotContain(tokens, t => t.StartsWith("0:20:", StringComparison.Ordinal)); // the '.' is inside the name
        Assert.DoesNotContain(tokens, t => t.StartsWith("0:36:", StringComparison.Ordinal)); // nope
    }

    [Fact]
    public void A_block_comment_is_split_per_line()
    {
        var tokens = Tokens("/* a\nbc */ unit a { }");
        Assert.Equal("0:0:4:Comment:None", tokens[0]);
        Assert.Equal("1:0:5:Comment:None", tokens[1]);
    }

    [Fact]
    public void A_document_nobody_opened_has_no_tokens()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        Assert.Empty(service.SemanticTokens("file:///w/none.scopes"));
    }
}
