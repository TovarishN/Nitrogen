using System.Text.Json.Nodes;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class VsCodeGenerationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-vscode-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    VsCodeRequest Request()
    {
        Write("project/language/Catalog.ngr", "syntax module Catalog { }");
        string config = Write("project/nitrogen.json", """
            { "languages": [ { "name": "Catalog", "extensions": [".ncat"], "grammars": ["language/Catalog.ngr"], "start": "Catalog.Record", "version": "1.4.0" } ] }
            """);
        Write("server/nitrogen.dll", "dll");
        Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = VsCodeInput.ParseRequest(
            ["generate", "vscode", "--config", config, "--output", Path.Combine(_root, "out"), "--server", Path.Combine(_root, "server")], out string error);
        Assert.Equal("", error);
        return request!;
    }

    string Read(VsCodeRequest request, string path) => File.ReadAllText(Path.Combine(request.OutputDirectory, path));

    [Fact]
    public void Package_json_contributes_the_language_and_its_settings()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        var package = JsonNode.Parse(Read(request, "package.json"))!;

        Assert.Equal("nitrogen-catalog", (string?)package["name"]);
        Assert.Equal("1.4.0", (string?)package["version"]);
        Assert.Equal("nitrogen", (string?)package["publisher"]);
        Assert.Equal("onLanguage:nitrogen-catalog", (string?)package["activationEvents"]![0]);
        Assert.Equal("onLanguage:csharp", (string?)package["activationEvents"]![1]);
        var language = package["contributes"]!["languages"]![0]!;
        Assert.Equal("nitrogen-catalog", (string?)language["id"]);
        Assert.Equal(".ncat", (string?)language["extensions"]![0]);
        var properties = package["contributes"]!["configuration"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "nitrogen-catalog.dotnetPath", "nitrogen-catalog.server.path", "nitrogen-catalog.developmentMode" }, properties.Select(p => p.Key));
        Assert.NotNull(package["dependencies"]!["vscode-languageclient"]);
    }

    [Fact]
    public void Generated_extension_paints_with_the_palette_and_a_textmate_grammar()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        var contributes = JsonNode.Parse(Read(request, "package.json"))!["contributes"]!;
        var palette = Nitrogen.LanguageService.Palette.VsCodeContributions();

        Assert.True(JsonNode.DeepEquals(palette["semanticTokenTypes"], contributes["semanticTokenTypes"]));
        Assert.True(JsonNode.DeepEquals(palette["semanticTokenScopes"], contributes["semanticTokenScopes"]));
        var grammar = contributes["grammars"]![0]!;
        Assert.Equal("nitrogen-catalog", (string?)grammar["language"]);
        Assert.Equal("source.nitrogen-catalog", (string?)grammar["scopeName"]);
        Assert.Equal("./syntaxes/nitrogen-catalog.tmLanguage.json", (string?)grammar["path"]);
        Assert.Equal("source.nitrogen-catalog", (string?)JsonNode.Parse(Read(request, "syntaxes/nitrogen-catalog.tmLanguage.json"))!["scopeName"]);
        Assert.Contains("paletteTokens: true", Read(request, "src/extension.ts"));
    }

    [Fact]
    public void Development_mode_is_opt_in_and_watches_workspace_grammar_and_helpers()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        var package = JsonNode.Parse(Read(request, "package.json"))!;
        var setting = package["contributes"]!["configuration"]!["properties"]!["nitrogen-catalog.developmentMode"]!;
        Assert.False((bool)setting["default"]!);
        Assert.Equal("boolean", (string?)setting["type"]);
        string extension = Read(request, "src/extension.ts");
        Assert.Contains("languageConfig(", extension);
        Assert.Contains("get<boolean>('developmentMode')", extension);
        Assert.Contains("workspace.createFileSystemWatcher('**/{*.ngr,*.cs,nitrogen.json}')", extension);
        Assert.Contains("fileEvents: watcher", extension);
        Assert.Contains("languageConfig", Read(request, "src/developmentConfig.ts"));
        Assert.Contains("nitrogen-catalog.developmentMode", Read(request, "README.md"));
        Assert.Contains("without restarting", Read(request, "README.md"));
    }

    [Fact]
    public void Lockfile_is_the_extension_lock_with_this_package_name()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        var lockfile = JsonNode.Parse(Read(request, "package-lock.json"))!;

        Assert.Equal("nitrogen-catalog", (string?)lockfile["name"]);
        Assert.Equal("1.4.0", (string?)lockfile["version"]);
        Assert.Equal("nitrogen-catalog", (string?)lockfile["packages"]![""]!["name"]);
        Assert.Null(lockfile["packages"]![""]!["license"]);
        Assert.NotNull(lockfile["packages"]!["node_modules/vscode-languageclient"]);
    }

    [Fact]
    public void GeneratedExtensionYieldsToNitride()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string extension = Read(request, "src/extension.ts");

        int check = extension.IndexOf("getExtension('nitride.nitride')", StringComparison.Ordinal);
        Assert.True(check >= 0, "the generated extension checks for Nitride");
        Assert.True(check < extension.IndexOf("new LanguageClient(", StringComparison.Ordinal));
    }

    [Fact]
    public void Extension_starts_the_bundled_server_with_its_config()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string extension = Read(request, "src/extension.ts");

        Assert.Contains("const LANGUAGE = \"nitrogen-catalog\";", extension);
        Assert.Contains("'lsp', '--config', config", extension);
        Assert.Contains("path.join('bundle', 'server', 'nitrogen.dll')", extension);
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "language", "nitrogen.json")));
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "server", "nitrogen.dll")));
    }

    [Fact]
    public void Extension_serves_csharp_strings_tagged_with_the_language()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);

        Assert.Contains("...csharpSelector", Read(request, "src/extension.ts"));
        Assert.Contains("strings.refreshVisible();", Read(request, "src/extension.ts"));
        Assert.Contains("signatureDocuments: signatureOwner", Read(request, "src/extension.ts"));
        Assert.Contains("ownsSignatureDocuments", Read(request, "src/signatureDocuments.ts"));
        Assert.Contains("setTextDocumentLanguage", Read(request, "src/signatureRouting.ts"));
        Assert.Contains("middleware: routing.middleware(strings.middleware", Read(request, "src/extension.ts"));
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "editors", "vscode", "src", "csharpStrings.ts")),
            Read(request, "src/csharpStrings.ts"));
    }

    [Fact]
    public void Readme_names_values_and_quick_fixes_as_features()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string readme = Read(request, "README.md");

        Assert.Contains("each statement's value as an inlay hint", readme, StringComparison.Ordinal);
        Assert.Contains("an expression's value on hover", readme, StringComparison.Ordinal);
        Assert.Contains("quick fixes", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_twice_is_byte_identical()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string first = Snapshot(request.OutputDirectory);
        VsCodeRenderer.Render(request, CancellationToken.None);
        Assert.Equal(first, Snapshot(request.OutputDirectory));
    }

    [Theory]
    [InlineData("--output out", "no --config")]
    [InlineData("--config nitrogen.json", "no --output")]
    [InlineData("--config nitrogen.json --output out --bundle x=y", "unknown option '--bundle'")]
    public void Missing_or_unknown_options_are_reported(string options, string expected)
    {
        Assert.Null(VsCodeInput.ParseRequest(new[] { "generate", "vscode" }.Concat(options.Split(' ')).ToArray(), out string error));
        Assert.Equal(expected, error);
    }

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
