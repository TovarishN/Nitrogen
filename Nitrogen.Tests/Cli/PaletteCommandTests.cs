using System.Text.Json.Nodes;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

/// <summary><c>nitrogen generate palette --merge</c> writes the palette's contributions into an extension's package.json.</summary>
public sealed class PaletteCommandTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-palette-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Merge_replaces_the_palette_keys_and_is_idempotent()
    {
        string package = Path.Combine(_root, "package.json");
        File.WriteAllText(package, """
            { "name": "x", "contributes": { "languages": [ { "id": "policy" } ], "semanticTokenScopes": [ { "scopes": { "old": ["x"] } } ] } }
            """);
        var log = new StringWriter();
        Assert.Equal(0, await NitrogenCli.RunAsync(["generate", "palette", "--merge", package], log, CancellationToken.None));
        string first = File.ReadAllText(package);
        Assert.Equal(0, await NitrogenCli.RunAsync(["generate", "palette", "--merge", package], log, CancellationToken.None));

        Assert.Equal(first, File.ReadAllText(package));
        var contributes = JsonNode.Parse(first)!["contributes"]!;
        Assert.Equal("policy", (string?)contributes["languages"]![0]!["id"]);
        Assert.True(JsonNode.DeepEquals(Nitrogen.LanguageService.Palette.VsCodeContributions()["semanticTokenScopes"], contributes["semanticTokenScopes"]));
    }
}
