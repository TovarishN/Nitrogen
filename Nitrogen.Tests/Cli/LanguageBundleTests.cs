using System.Text.Json;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguageBundleTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-bundle-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A catalog-like language and a fake framework-dependent server.</summary>
    (LanguagePluginModel Model, string Server) Language(string grammars = "\"language/*.ngr\"")
    {
        Write("project/language/Catalog.ngr", "syntax module Catalog { }");
        Write("project/language/Extra.ngr", "syntax module Extra { }");
        Write("project/language/Checks.cs", "class Checks { }");
        string config = Write("project/nitrogen.json", $$"""
            { "languages": [ { "name": "Catalog", "extensions": [".ncat"], "grammars": [{{grammars}}],
              "start": "Catalog.Record", "sources": ["language/Checks.cs"], "usings": ["My.Checks"],
              "namespace": "My.Catalog", "tokens": { "capability": "type" } } ] }
            """);
        Write("server/nitrogen.dll", "dll");
        Write("server/nitrogen.runtimeconfig.json", "{}");
        Write("server/runtimes/any/dep.dll", "dep");
        return (LanguagePluginConfig.Load(config, null, out _)!, Path.Combine(_root, "server"));
    }

    [Fact]
    public void Stage_writes_a_rewritten_config_the_files_and_the_server()
    {
        var (model, server) = Language();
        string bundle = Path.Combine(_root, "bundle");
        LanguageBundle.Stage(model, server, bundle);

        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "language", "nitrogen.json")));
        var entry = config.RootElement.GetProperty("languages")[0];
        Assert.Equal("Catalog", entry.GetProperty("name").GetString());
        Assert.Equal("Catalog.Record", entry.GetProperty("start").GetString());
        Assert.Equal(new[] { "grammars/Catalog.ngr", "grammars/Extra.ngr" }, entry.GetProperty("grammars").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "sources/Checks.cs" }, entry.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "My.Checks" }, entry.GetProperty("usings").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("My.Catalog", entry.GetProperty("namespace").GetString());
        Assert.Equal("type", entry.GetProperty("tokens").GetProperty("capability").GetString());
        Assert.Equal("class Checks { }", File.ReadAllText(Path.Combine(bundle, "language", "sources", "Checks.cs")));
        Assert.True(File.Exists(Path.Combine(bundle, "language", "grammars", "Extra.ngr")));
        Assert.Equal("dep", File.ReadAllText(Path.Combine(bundle, "server", "runtimes", "any", "dep.dll")));
    }

    [Fact]
    public void Staging_twice_is_byte_identical()
    {
        var (model, server) = Language();
        LanguageBundle.Stage(model, server, Path.Combine(_root, "a"));
        LanguageBundle.Stage(model, server, Path.Combine(_root, "b"));
        Assert.Equal(Snapshot(Path.Combine(_root, "a")), Snapshot(Path.Combine(_root, "b")));
    }

    [Fact]
    public void A_pattern_that_matches_nothing_is_an_error()
    {
        var (model, server) = Language("\"missing/*.ngr\"");
        var error = Assert.Throws<ArgumentException>(() => LanguageBundle.Stage(model, server, Path.Combine(_root, "bundle")));
        Assert.Contains("no grammar file matches", error.Message);
    }

    [Fact]
    public void Two_files_with_one_name_are_an_error()
    {
        Write("project/other/Catalog.ngr", "syntax module Other { }");
        var (model, server) = Language("\"language/Catalog.ngr\", \"other/Catalog.ngr\"");
        var error = Assert.Throws<ArgumentException>(() => LanguageBundle.Stage(model, server, Path.Combine(_root, "bundle")));
        Assert.Contains("two grammar files named 'Catalog.ngr'", error.Message);
    }

    [Fact]
    public void A_server_without_nitrogen_dll_is_an_error()
    {
        var (model, _) = Language();
        string empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var error = Assert.Throws<ArgumentException>(() => LanguageBundle.Stage(model, empty, Path.Combine(_root, "bundle")));
        Assert.Contains("--server", error.Message);
    }

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
