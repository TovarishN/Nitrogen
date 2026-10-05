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

    [Fact]
    public void Shared_Kotlin_from_a_CRLF_checkout_moves_into_the_plugin_package()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [ { "name": "calc", "extensions": [".calc"], "grammars": ["Calc.ngr"], "start": "Calc.Program" } ] }
            """);
        var model = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", dir.Path + "/out" }, out _)!.Model;

        // core.autocrlf or core.eol=crlf checks the renderer's raw-string templates out with CRLF.
        string kotlin = RiderPluginRenderer.InPackage("package org.nitrogen.rider\r\n\r\nimport a.B\r\n", model);

        Assert.Equal("package org.nitrogen.rider.lang_calc\n\nimport a.B\n", kotlin);
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
    public void Rendered_build_uses_the_template_plugins_and_platform_api()
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

        // Generated plugins build with the template's Kotlin and IntelliJ Platform Gradle plugins, and
        // leave the JVM to the platform plugin, which takes it from the Rider SDK (Java 25 for 262).
        static string[] Versions(string gradle) => System.Text.RegularExpressions.Regex
            .Matches(gradle, "(kotlin\\(\"jvm\"\\)|id\\(\"org\\.jetbrains\\.intellij\\.platform\"\\)) version \"([^\"]+)\"")
            .Select(match => match.Value).ToArray();
        string template = File.ReadAllText(Path.Combine(RepositoryRoot(), "editors", "rider", "build.gradle.kts"));
        Assert.Equal(2, Versions(template).Length);
        Assert.Equal(Versions(template), Versions(Read("build.gradle.kts")));
        Assert.Contains("rider(\"2026.2\") { useInstaller = false }", template, StringComparison.Ordinal);
        Assert.Contains("rider(\"2026.2\") { useInstaller = false }", Read("build.gradle.kts"), StringComparison.Ordinal);
        Assert.DoesNotContain("JvmTarget", Read("build.gradle.kts"), StringComparison.Ordinal);
        Assert.DoesNotContain("options.release", Read("build.gradle.kts"), StringComparison.Ordinal);
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
            Assert.Contains($"<applicationConfigurable parentId=\"tools\" instance=\"{KotlinPackage(plugin)}.NitrogenConfigurable\"",
                Read("src/main/resources/META-INF/plugin.xml"), StringComparison.Ordinal);
            Assert.Contains("NitrogenSettings.getInstance().resolveExecutable(defaultExecutable)",
                Read("src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"), StringComparison.Ordinal);
        }

        // The settings, page and bundles are shared verbatim apart from each plugin's own package line,
        // so the generator cannot drift from the template.
        static string AfterPackage(string text)
        {
            text = text.Replace("\r\n", "\n");
            Assert.StartsWith("package ", text, StringComparison.Ordinal);
            return text[text.IndexOf('\n')..];
        }
        foreach (string file in new[] { "NitrogenSettings.kt", "NitrogenConfigurable.kt", "NitrogenBundles.kt", "NitrogenCSharpStrings.kt", "NitrogenParserDefinition.kt", "NitrogenHighlighting.kt" })
        {
            string path = Path.Combine("src/main/kotlin/org/nitrogen/rider", file);
            Assert.Equal(AfterPackage(File.ReadAllText(Path.Combine(template, path))),
                AfterPackage(File.ReadAllText(Path.Combine(request.OutputDirectory, path))));
        }
    }

    [Fact]
    public void Generated_plugins_serve_csharp_strings_tagged_with_their_language()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(
            new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out string error);
        Assert.Equal("", error);
        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);

        string lsp = Kotlin(request.OutputDirectory, "NitrogenLspSupport.kt");
        Assert.Contains("if (NitrogenCSharpClient.isCSharp(file))", lsp, StringComparison.Ordinal);
        Assert.Contains("NitrogenCSharpClient(project, \"Calc\", ::commandLine)", lsp, StringComparison.Ordinal);
        Assert.StartsWith("package " + KotlinPackage(request.OutputDirectory) + "\n", Kotlin(request.OutputDirectory, "NitrogenCSharpStrings.kt"), StringComparison.Ordinal);
        Assert.Contains("/*lang=calc*/", File.ReadAllText(Path.Combine(request.OutputDirectory, "README.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_server_is_packaged_with_a_descriptor_the_plugin_reads()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        string server = dir.Write("server/nitrogen", "#!/bin/sh\necho nitrogen\n");
        var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--config", config,
            "--bundle", "macos-aarch64=" + server, "--output", Path.Combine(dir.Path, "out") }, out string error);
        Assert.Equal("", error);
        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);

        string resources = Path.Combine(request.OutputDirectory, "src/main/resources");
        Assert.Equal(File.ReadAllBytes(server), File.ReadAllBytes(Path.Combine(resources, "bundled/macos-aarch64/nitrogen")));
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(server))).ToLowerInvariant();
        // NitrogenBundles.kt reads entries with this pattern; keep the two in step.
        var entry = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(resources, "nitrogen-bundles.json")),
            "\\{\\s*\"target\":\\s*\"([^\"]+)\",\\s*\"file\":\\s*\"([^\"]+)\",\\s*\"sha256\":\\s*\"([0-9a-f]{64})\"\\s*}");
        Assert.True(entry.Success);
        Assert.Equal(("macos-aarch64", "bundled/macos-aarch64/nitrogen", hash),
            (entry.Groups[1].Value, entry.Groups[2].Value, entry.Groups[3].Value));
    }

    [Fact]
    public void Generated_plugins_and_the_template_have_distinct_ide_identities()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [
              { "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" },
              { "name": "Mini Lang", "extensions": [".mini"], "grammars": ["b.ngr"], "start": "Mini.File" }
            ] }
            """);
        var plugins = new[] { "Calc", "Mini Lang" }.Select(language =>
        {
            var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--config", config, "--language", language,
                "--output", Path.Combine(dir.Path, language) }, out string error);
            Assert.Equal("", error);
            RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);
            return request.OutputDirectory;
        }).Append(Path.Combine(RepositoryRoot(), "editors", "rider")).ToArray();

        // Rider registers services by class name and keeps language IDs and settings state names
        // application-wide, so each installed plugin needs its own of each.
        foreach (var identity in new Func<string, string>[] { KotlinPackage, LanguageId, SettingsName, SettingsFile })
            Assert.Equal(plugins.Length, plugins.Select(identity).Distinct(StringComparer.Ordinal).Count());
        foreach (string plugin in plugins)
        {
            string xml = File.ReadAllText(Path.Combine(plugin, "src/main/resources/META-INF/plugin.xml"));
            Assert.Contains($"language=\"{LanguageId(plugin)}\"", xml, StringComparison.Ordinal);
            foreach (var reference in System.Text.RegularExpressions.Regex.Matches(xml, "(?:implementationClass|instance|implementation)=\"([^\"]+)\"")
                         .Select(match => match.Groups[1].Value))
                Assert.StartsWith(KotlinPackage(plugin) + ".", reference, StringComparison.Ordinal);
        }
    }

    static string Kotlin(string plugin, string file) => File.ReadAllText(Path.Combine(plugin, "src/main/kotlin/org/nitrogen/rider", file));

    static string KotlinPackage(string plugin) =>
        System.Text.RegularExpressions.Regex.Match(Kotlin(plugin, "NitrogenLspSupport.kt"), "^package ([\\w.]+)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;

    static string LanguageId(string plugin) =>
        System.Text.RegularExpressions.Regex.Match(Kotlin(plugin, "NitrogenFileType.kt"), "Language\\(\"([^\"]+)\"\\)").Groups[1].Value;

    static string SettingsName(string plugin) =>
        System.Text.RegularExpressions.Regex.Match(Kotlin(plugin, "NitrogenPlugin.kt"), "SETTINGS_NAME = \"([^\"]+)\"").Groups[1].Value;

    static string SettingsFile(string plugin) =>
        System.Text.RegularExpressions.Regex.Match(Kotlin(plugin, "NitrogenPlugin.kt"), "SETTINGS_FILE = \"([^\"]+)\"").Groups[1].Value;

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

    static RiderPluginRequest SelfContained(TempDirectory dir)
    {
        dir.Write("project/a.ngr", "syntax module Calc { }");
        string config = dir.Write("project/nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program", "version": "2.0.1" }] }
            """);
        dir.Write("server/nitrogen.dll", "dll");
        dir.Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out"),
            "--self-contained", "--server", Path.Combine(dir.Path, "server") }, out string error);
        Assert.Equal("", error);
        return request!;
    }

    [Fact]
    public void Self_contained_plugin_ships_the_bundle_and_starts_it_with_dotnet()
    {
        using var dir = new TempDirectory();
        var request = SelfContained(dir);
        RiderPluginRenderer.Render(request, request.OutputDirectory, CancellationToken.None);
        string Read(string path) => File.ReadAllText(Path.Combine(request.OutputDirectory, path));

        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "language", "nitrogen.json")));
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "server", "nitrogen.dll")));
        Assert.Contains("PrepareSandboxTask", Read("build.gradle.kts"));
        Assert.Contains("version = \"2.0.1\"", Read("build.gradle.kts"));
        string lsp = Read("src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt");
        Assert.Contains("\"lsp\", \"--config\", config", lsp);
        Assert.Contains("NitrogenLanguageBundle.dotnet()", lsp);
        Assert.Contains("PluginId.getId(\"org.nitrogen.rider.calc\")", Read("src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt"));
    }

    [Fact]
    public void Plugins_without_self_contained_are_unchanged_apart_from_the_version()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out _)!;
        RiderPluginRenderer.Render(request, request.OutputDirectory, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(request.OutputDirectory, "bundle")));
        Assert.False(File.Exists(Path.Combine(request.OutputDirectory, "src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt")));
        Assert.DoesNotContain("PrepareSandbox", File.ReadAllText(Path.Combine(request.OutputDirectory, "build.gradle.kts")));
        Assert.Contains($"version = \"{typeof(LanguagePluginConfig).Assembly.GetName().Version!.ToString(3)}\"", File.ReadAllText(Path.Combine(request.OutputDirectory, "build.gradle.kts")));
    }

    [Theory]
    [InlineData("--self-contained --bundle macos-x64=nitrogen.json", "use either --self-contained or --bundle")]
    [InlineData("--server server", "--server needs --self-contained")]
    public void Self_contained_option_conflicts_are_reported(string options, string expected)
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var args = new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }
            .Concat(options.Replace("nitrogen.json", config).Split(' ')).ToArray();
        Assert.Null(RiderPluginInput.ParseRequest(args, out string error));
        Assert.Equal(expected, error);
    }

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
