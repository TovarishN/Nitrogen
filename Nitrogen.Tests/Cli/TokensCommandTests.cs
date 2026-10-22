using System.Text.Json;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

/// <summary><c>nitrogen tokens</c> prints the scope the server's palette gives each token, for comparing with a TextMate grammar.</summary>
public sealed class TokensCommandTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-tokens-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static async Task<List<(int Line, int Character, int Length, string Scope)>> Tokens(params string[] args)
    {
        var output = new StringWriter();
        Assert.Equal(0, await NitrogenCli.RunAsync(["tokens", .. args], output, CancellationToken.None));
        return JsonDocument.Parse(output.ToString()).RootElement.EnumerateArray()
            .Select(t => (t.GetProperty("line").GetInt32(), t.GetProperty("character").GetInt32(), t.GetProperty("length").GetInt32(), t.GetProperty("scope").GetString()!))
            .ToList();
    }

    [Fact]
    public async Task Prints_scopes_for_a_ngr_file()
    {
        string file = Path.Combine(_root, "a.ngr");
        File.WriteAllText(file, "syntax module M\n{\n  token Name = ['a'..'z']+;\n  syntax S = \"go\" Value:Name;\n}\n");
        var tokens = await Tokens(file);

        // The hand-written ngr.tmLanguage.json's scopes, which the server matches for .ngr.
        Assert.Contains((0, 0, 6, "storage.type.syntax"), tokens);
        Assert.Contains((3, 9, 1, "entity.name.function.rule"), tokens);
        Assert.Contains((1, 0, 1, "punctuation.section.module.begin"), tokens);
    }

    [Fact]
    public async Task Prints_palette_scopes_for_a_workspace_language()
    {
        string config = Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", "nitrogen.json");
        string file = Path.Combine(_root, "a.datecalc");
        File.WriteAllText(file, "let start = 2 weeks;\n");
        var tokens = await Tokens(file, "--config", config);

        Assert.Contains((0, 0, 3, "storage.type"), tokens);
        Assert.Contains((0, 19, 1, "punctuation.terminator"), tokens);
    }
}
