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
        var language = package["contributes"]!["languages"]![0]!;
        Assert.Equal("nitrogen-catalog", (string?)language["id"]);
        Assert.Equal(".ncat", (string?)language["extensions"]![0]);
        var properties = package["contributes"]!["configuration"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "nitrogen-catalog.dotnetPath", "nitrogen-catalog.server.path" }, properties.Select(p => p.Key));
        Assert.NotNull(package["dependencies"]!["vscode-languageclient"]);
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
