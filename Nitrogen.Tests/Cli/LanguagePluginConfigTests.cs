using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguagePluginConfigTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-config-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Sources_usings_tokens_and_version_are_read()
    {
        string config = Write("nitrogen.json", """
            { "languages": [ { "name": "Catalog", "extensions": [".ncat"], "grammars": ["language/*.ngr"],
              "start": "Catalog.Record", "sources": ["language/Checks.cs"], "usings": ["My.Checks"],
              "tokens": { "capability": "type" }, "version": "1.2.3" } ] }
            """);
        var model = LanguagePluginConfig.Load(config, null, out string error);

        Assert.Equal("", error);
        Assert.Equal(new[] { Path.Combine(_root, "language", "*.ngr") }, model!.GrammarPaths);
        Assert.Equal(new[] { Path.Combine(_root, "language", "Checks.cs") }, model.SourcePaths);
        Assert.Equal(new[] { "My.Checks" }, model.Usings);
        Assert.Equal("""{ "capability": "type" }""", model.TokensJson);
        Assert.Equal("1.2.3", model.Version);
    }

    [Fact]
    public void Optional_fields_default()
    {
        string config = Write("nitrogen.json", """
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" } ] }
            """);
        var model = LanguagePluginConfig.Load(config, null, out _)!;

        Assert.Empty(model.SourcePaths);
        Assert.Empty(model.Usings);
        Assert.Null(model.TokensJson);
        Assert.Equal("0.1.0", model.Version);
    }

    [Theory]
    [InlineData("\"1.2\"")]
    [InlineData("\"v1.2.3\"")]
    [InlineData("3")]
    public void A_bad_version_is_rejected(string version)
    {
        string config = Write("nitrogen.json", $$"""
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program", "version": {{version}} } ] }
            """);
        Assert.Null(LanguagePluginConfig.Load(config, null, out string error));
        Assert.Equal("version must be MAJOR.MINOR.PATCH", error);
    }
}
