using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class RiderPluginGenerationTests
{
    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("nitrogen-rider-").FullName;

        public string Write(string name, string text)
        {
            string path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void Generation_request_reads_config_and_normalizes_language_metadata()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            {
              "languages": [
                {
                  "name": "Calc Language",
                  "extensions": [".ZCALC", ".calc"],
                  "grammars": ["grammars/Calc.ngr"],
                  "start": "Calc.Program"
                }
              ]
            }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", dir.Path + "/out" }, out string error);

        Assert.Equal("", error);
        Assert.Equal("calc-language", request!.Model.PluginId);
        Assert.Equal(new[] { ".calc", ".zcalc" }, request.Model.Extensions);
        Assert.Equal("Calc.Program", request.Model.StartRule);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir.Path, "grammars/Calc.ngr")), request.Model.GrammarPaths.Single());
    }

    [Theory]
    [InlineData("{", "invalid JSON")]
    [InlineData("{}", "no languages")]
    [InlineData("{\"languages\":[{}]}", "language name")]
    [InlineData("{\"languages\":[{\"name\":\"Calc\",\"extensions\":[\"calc\"],\"grammars\":[\"a.ngr\"],\"start\":\"Calc.Program\"}]}", "extension")]
    public void Invalid_config_reports_a_specific_error(string json, string expected)
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", json);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", dir.Path + "/out" }, out string error);

        Assert.Null(request);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_grammar_input_requires_a_start_rule_and_output()
    {
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--grammar", "Calc.ngr" }, out string error);

        Assert.Null(request);
        Assert.Equal("no --start", error);
    }

    [Fact]
    public void Equivalent_language_order_produces_the_same_canonical_extensions()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [
              { "name": "Calc", "extensions": [".b", ".a"], "grammars": ["a.ngr"], "start": "Calc.Program" },
              { "name": "Other", "extensions": [".d", ".c"], "grammars": ["b.ngr"], "start": "Other.Document" }
            ] }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--language", "Other", "--output", dir.Path + "/out" }, out string error);

        Assert.Equal("", error);
        Assert.Equal(new[] { ".c", ".d" }, request!.Model.Extensions);
    }

    [Fact]
    public void Renderer_publishes_a_stable_plugin_tree()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out string error);
        Assert.Equal("", error);

        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);
        string first = Snapshot(request.OutputDirectory);
        RiderPluginRenderer.Render(request, request.OutputDirectory, CancellationToken.None);

        Assert.Equal(first, Snapshot(request.OutputDirectory));
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "src/main/resources/META-INF/plugin.xml")));
        Assert.Contains(".calc", File.ReadAllText(Path.Combine(request.OutputDirectory, "src/main/resources/META-INF/plugin.xml")), StringComparison.Ordinal);
    }

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
