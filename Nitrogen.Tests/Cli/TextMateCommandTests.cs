using System.Text.Json;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

/// <summary><c>nitrogen generate textmate</c> writes a language's TextMate grammar from its nitrogen.json entry.</summary>
public sealed class TextMateCommandTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-textmate-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static string DateCalcConfig => Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", "nitrogen.json");

    [Fact]
    public async Task Writes_grammar_for_a_config_language()
    {
        string output = Path.Combine(_root, "syntaxes", "datecalc.tmLanguage.json");
        var log = new StringWriter();
        int code = await NitrogenCli.RunAsync(
            ["generate", "textmate", "--config", DateCalcConfig, "--language", "DateCalc", "--id", "datecalc", "--output", output], log, CancellationToken.None);

        Assert.True(code == 0, log.ToString());
        var grammar = JsonDocument.Parse(File.ReadAllText(output)).RootElement;
        Assert.Equal("source.datecalc", grammar.GetProperty("scopeName").GetString());
        // "let" declares a value: its keyword is a declaration keyword, the name a variable (the tokens style).
        var declaration = Assert.Single(grammar.GetProperty("patterns").EnumerateArray(), p => p.TryGetProperty("captures", out _)
            && p.GetProperty("match").GetString()!.Contains("let", StringComparison.Ordinal));
        Assert.Equal("storage.type.datecalc", declaration.GetProperty("captures").GetProperty("1").GetProperty("name").GetString());
        Assert.Equal("variable.other.readwrite.datecalc", declaration.GetProperty("captures").GetProperty("2").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unknown_language_is_an_error()
    {
        var log = new StringWriter();
        int code = await NitrogenCli.RunAsync(
            ["generate", "textmate", "--config", DateCalcConfig, "--language", "Nope", "--output", Path.Combine(_root, "x.json")], log, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("error:", log.ToString());
    }
}
