using System.Text.Json;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class ModulePackageTests
{
    const string Grammar = "syntax module GeneratedBox { token Digits = ['0'..'9']+; syntax Document = \"box\" Digits; }";

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nitrogen-package-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "box.ngr"), Grammar);
        }
        public void Manifest(string json) => File.WriteAllText(Path.Combine(Root, "module.json"), json);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    static string Valid(string[]? grammars = null, object[]? examples = null,
        string[]? capabilities = null) => JsonSerializer.Serialize(new
    {
        id = "box-demo", profile = "geometry-box", module = "GeneratedBox", start = "GeneratedBox.Document",
        capabilities = capabilities ?? ["Geometry.BoxMesh"],
        grammars = grammars ?? ["box.ngr"],
        examples = examples ?? [new { id = "valid", path = "valid.box", source = "box 1;",
            diagnostics = Array.Empty<string>(), expectedResult = "box:1" }]
    });

    [Fact]
    public void Loads_bounded_package_and_hashes_logical_content_not_json_whitespace()
    {
        using var compact = new Fixture();
        using var indented = new Fixture();
        compact.Manifest(Valid());
        using var json = JsonDocument.Parse(Valid());
        indented.Manifest(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        var first = ModulePackageLoader.Load(compact.Root);
        var second = ModulePackageLoader.Load(indented.Root);
        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        Assert.Equal("box-demo", first.Package!.Id);
        Assert.Equal("geometry-box", first.Package.ProfileId);
        Assert.Equal("GeneratedBox", first.Package.ModuleName);
        Assert.Equal("GeneratedBox.Document", first.Package.StartRule);
        Assert.Equal(["Geometry.BoxMesh"], first.Package.RequestedCapabilities);
        Assert.Equal(Grammar, first.Package.Grammars["box.ngr"]);
        Assert.Single(first.Package.Examples);
        Assert.Equal(first.Package.Sha256, second.Package!.Sha256);
        Assert.Equal(64, first.Package.Sha256.Length);
    }

    [Fact]
    public void Capability_requests_are_required_bounded_unique_and_string_typed()
    {
        using var fixture = new Fixture();
        foreach (var manifest in new[]
        {
            Valid().Replace("\"capabilities\":[\"Geometry.BoxMesh\"],", "", StringComparison.Ordinal),
            Valid(capabilities: ["Geometry.BoxMesh", "Geometry.BoxMesh"]),
            Valid(capabilities: [" "]),
            Valid(capabilities: Enumerable.Range(0, 17).Select(i => "Geometry." + i).ToArray()),
            Valid().Replace("\"Geometry.BoxMesh\"", "42", StringComparison.Ordinal)
        })
        {
            fixture.Manifest(manifest);
            var result = ModulePackageLoader.Load(fixture.Root);
            Assert.Null(result.Package);
            Assert.Contains(result.Diagnostics, d => d.Code == "NA0001");
        }
    }

    [Fact]
    public void Capability_order_does_not_change_hash_but_identity_does()
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid(capabilities: ["Geometry.BoxMesh", "Core.Clamp"]));
        var first = ModulePackageLoader.Load(fixture.Root).Package!;
        fixture.Manifest(Valid(capabilities: ["Core.Clamp", "Geometry.BoxMesh"]));
        var reordered = ModulePackageLoader.Load(fixture.Root).Package!;
        fixture.Manifest(Valid(capabilities: ["Core.Clamp", "Geometry.Other"]));
        var changed = ModulePackageLoader.Load(fixture.Root).Package!;
        Assert.Equal(["Core.Clamp", "Geometry.BoxMesh"], first.RequestedCapabilities);
        Assert.Equal(first.Sha256, reordered.Sha256);
        Assert.NotEqual(first.Sha256, changed.Sha256);
    }

    [Theory]
    [InlineData("../outside.ngr")]
    [InlineData("/tmp/outside.ngr")]
    [InlineData("sub/box.ngr")]
    public void Rejects_unsafe_grammar_paths(string name)
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid([name]));
        var result = ModulePackageLoader.Load(fixture.Root);
        Assert.Null(result.Package);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NA0001");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside.box")]
    [InlineData("/tmp/outside.box")]
    public void Rejects_unsafe_example_paths(string path)
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid(examples:
        [
            new { id = "valid", path, source = "box 1;", diagnostics = Array.Empty<string>(), expectedResult = "box:1" }
        ]));
        var result = ModulePackageLoader.Load(fixture.Root);
        Assert.Null(result.Package);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NA0001");
    }

    [Fact]
    public void Rejects_duplicate_grammar_path_and_example_id()
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid(["box.ngr", "box.ngr"]));
        Assert.Null(ModulePackageLoader.Load(fixture.Root).Package);
        fixture.Manifest(Valid(examples:
        [
            new { id = "same", path = "a.box", source = "box 1;", diagnostics = Array.Empty<string>(), expectedResult = "box:1" },
            new { id = "same", path = "b.box", source = "box 2;", diagnostics = Array.Empty<string>(), expectedResult = "box:2" }
        ]));
        var result = ModulePackageLoader.Load(fixture.Root);
        Assert.Null(result.Package);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NA0001");
    }

    [Fact]
    public void Rejects_unknown_property_missing_file_and_oversize_grammar()
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid().Replace("\"profile\":", "\"sources\":[],\"profile\":", StringComparison.Ordinal));
        Assert.Null(ModulePackageLoader.Load(fixture.Root).Package);
        fixture.Manifest(Valid(["missing.ngr"]));
        Assert.Null(ModulePackageLoader.Load(fixture.Root).Package);
        fixture.Manifest(Valid());
        File.WriteAllText(Path.Combine(fixture.Root, "box.ngr"), new string('a', 65 * 1024));
        var result = ModulePackageLoader.Load(fixture.Root);
        Assert.Null(result.Package);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NA0001");
    }

    [Fact]
    public void Rejects_seventeenth_example_and_grammar_symlink()
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid(examples: Enumerable.Range(0, 17).Select(i => (object)new
        {
            id = "x" + i, path = "x" + i + ".box", source = "box 1;",
            diagnostics = Array.Empty<string>(), expectedResult = "box:1"
        }).ToArray()));
        Assert.Null(ModulePackageLoader.Load(fixture.Root).Package);

        var outside = Path.Combine(Path.GetTempPath(), "nitrogen-outside-" + Guid.NewGuid().ToString("N") + ".ngr");
        try
        {
            File.WriteAllText(outside, Grammar);
            File.CreateSymbolicLink(Path.Combine(fixture.Root, "linked.ngr"), outside);
            fixture.Manifest(Valid(["linked.ngr"]));
            var result = ModulePackageLoader.Load(fixture.Root);
            Assert.Null(result.Package);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NA0001");
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Example_can_select_a_module_qualified_start_rule()
    {
        using var fixture = new Fixture();
        fixture.Manifest(Valid().Replace("\"source\":\"box 1;\"",
            "\"start\":\"GeneratedBox.Document\",\"source\":\"box 1;\"", StringComparison.Ordinal));
        var result = ModulePackageLoader.Load(fixture.Root);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("GeneratedBox.Document", Assert.Single(result.Package!.Examples).StartRule);
    }
}
