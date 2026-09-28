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

    [Theory]
    [InlineData("--output out", "no --config or --grammar")]
    [InlineData("--config a.json --grammar b.ngr --output out", "use either --config or --grammar")]
    [InlineData("--grammar Calc.ngr --output out", "no --start")]
    [InlineData("--grammar Calc.ngr --start Calc.Program", "no --output")]
    [InlineData("--config nitrogen.json", "no --output")]
    public void Missing_options_are_reported_source_first(string options, string expected)
    {
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider" }.Concat(options.Split(' ')).ToArray(), out string error);

        Assert.Null(request);
        Assert.Equal(expected, error);
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

    [Fact]
    public void Rendered_build_targets_the_rider_jvm_and_platform_api()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out string error);
        Assert.Equal("", error);
        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);
        string Read(string path) => File.ReadAllText(Path.Combine(request.OutputDirectory, path));

        // Both compilers target Rider's Java 21 runtime whatever JDK runs Gradle.
        Assert.Contains("options.release = 21", Read("build.gradle.kts"), StringComparison.Ordinal);
        Assert.Contains("JvmTarget.JVM_21", Read("build.gradle.kts"), StringComparison.Ordinal);
        // The IDE supplies the Kotlin standard library.
        Assert.Contains("kotlin.stdlib.default.dependency=false", Read("gradle.properties"), StringComparison.Ordinal);
        // LspClientStarter is nested in LspIntegrationProvider in the 262 platform.
        Assert.Contains("import com.intellij.platform.lsp.api.LspIntegrationProvider.LspClientStarter",
            Read("src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"), StringComparison.Ordinal);
    }

    [Fact]
    public void File_type_name_matches_the_class_in_rendered_and_template_plugins()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc Language", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out string error);
        Assert.Equal("", error);
        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);

        // IntelliJ rejects a <fileType> whose name differs from the class's getName().
        AssertFileTypeNamesMatch(request.OutputDirectory);
        AssertFileTypeNamesMatch(Path.Combine(RepositoryRoot(), "editors", "rider"));
    }

    [Fact]
    public void Executable_setting_page_is_registered_and_used_by_the_launcher()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out string error);
        Assert.Equal("", error);
        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);
        string template = Path.Combine(RepositoryRoot(), "editors", "rider");

        foreach (string plugin in new[] { request.OutputDirectory, template })
        {
            string Read(string path) => File.ReadAllText(Path.Combine(plugin, path)).Replace("\r\n", "\n");
            Assert.Contains("<applicationConfigurable parentId=\"tools\" instance=\"org.nitrogen.rider.NitrogenConfigurable\"",
                Read("src/main/resources/META-INF/plugin.xml"), StringComparison.Ordinal);
            Assert.Contains("NitrogenSettings.getInstance().resolveExecutable(defaultExecutable)",
                Read("src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"), StringComparison.Ordinal);
        }

        // The settings and page are shared verbatim, so the generator cannot drift from the template.
        foreach (string file in new[] { "NitrogenSettings.kt", "NitrogenConfigurable.kt" })
        {
            string path = Path.Combine("src/main/kotlin/org/nitrogen/rider", file);
            Assert.Equal(File.ReadAllText(Path.Combine(template, path)).Replace("\r\n", "\n"),
                File.ReadAllText(Path.Combine(request.OutputDirectory, path)).Replace("\r\n", "\n"));
        }
    }

    static void AssertFileTypeNamesMatch(string plugin)
    {
        string xml = File.ReadAllText(Path.Combine(plugin, "src/main/resources/META-INF/plugin.xml"));
        string kotlin = File.ReadAllText(Path.Combine(plugin, "src/main/kotlin/org/nitrogen/rider/NitrogenFileType.kt"));
        string declared = System.Text.RegularExpressions.Regex.Match(xml, "<fileType name=\"([^\"]*)\"").Groups[1].Value;
        string returned = System.Text.RegularExpressions.Regex.Match(kotlin, "getName\\(\\) = \"([^\"]*)\"").Groups[1].Value;
        Assert.NotEqual("", returned);
        Assert.Equal(returned, declared);
    }

    static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
