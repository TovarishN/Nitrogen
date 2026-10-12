using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Grammar;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguageDefinitionTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nitrogen-definition-tests"));
    static string PathOf(string name) => Path.Combine(Root, name);
    static InputSnapshot Inputs(params (string Path, string Text)[] files) => new(Root,
        files.ToDictionary(f => PathOf(f.Path), f => new DocumentInput(PathOf(f.Path), f.Text, 1)), true);
    const string Simple = "#!nitrogen grammar\nlanguage L { start M.R; } syntax module M { syntax R = \"x\"; }";

    [Fact]
    public void Missing_root_is_diagnostic_without_disk_fallback()
    {
        var result = new LanguageDefinitionResolver().Resolve(new LanguageReference("./Missing.syntax", true),
            PathOf("sample.txt"), Inputs());
        Assert.Null(result.Definition);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("Missing.syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void Includes_root_dependencies_helpers_and_default_namespace()
    {
        var input = Inputs(("root.syntax", """
            #!nitrogen grammar
            language L { start M.R; grammars "./common.syntax"; sources "./Helper.cs"; usings System.Linq; }
            syntax module M { syntax R = C.T; }
            """), ("common.syntax", "syntax module C { token T = \"x\"; }"), ("Helper.cs", "public static class Helper {}"));
        var result = LanguageDefinitionLoader.Load(PathOf("root.syntax"), input);
        Assert.Empty(result.Diagnostics);
        var definition = Assert.IsType<LanguageDefinition>(result.Definition);
        Assert.Equal("L.Syntax", definition.GeneratedNamespace);
        Assert.Equal(2, definition.Grammars.Count);
        Assert.Equal(PathOf("Helper.cs"), Assert.Single(definition.Sources).Path);
        Assert.Equal("System.Linq", Assert.Single(definition.Usings));
        Assert.DoesNotContain("#!nitrogen", definition.Grammars.Single(g => g.Path == PathOf("root.syntax")).Text);
    }

    [Fact]
    public void Normalized_paths_deduplicate_inputs_and_latest_snapshot_wins()
    {
        var input = Inputs(("root.syntax", Simple.Replace("start M.R;", "start M.R; grammars \"./a/../common.syntax\", \"./common.syntax\";")),
            ("common.syntax", "syntax module C { syntax T = \"edited\"; }"));
        var result = LanguageDefinitionLoader.Load(PathOf("a/../root.syntax"), input);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Definition!.Grammars.Count);
        Assert.Contains("edited", result.Definition.Grammars.Single(g => g.Path == PathOf("common.syntax")).Text);
    }

    [Theory]
    [InlineData("grammars \"./missing.syntax\";")]
    [InlineData("sources \"./Missing.cs\";")]
    [InlineData("grammars \"./root.syntax\";")]
    public void Missing_dependencies_and_cycles_fail_resolution(string setting)
    {
        var result = LanguageDefinitionLoader.Load(PathOf("root.syntax"), Inputs(("root.syntax", Simple.Replace("start M.R;", "start M.R; " + setting))));
        Assert.Null(result.Definition);
        Assert.Contains(result.Diagnostics, d => d.IsError);
    }

    [Fact]
    public void Transitive_cycle_has_a_local_diagnostic()
    {
        var inputs = Inputs(("root.syntax", Simple.Replace("start M.R;", "start M.R; grammars \"./child.syntax\";")),
            ("child.syntax", Simple.Replace("start M.R;", "start M.R; grammars \"./root.syntax\";")));
        var result = LanguageDefinitionLoader.Load(PathOf("root.syntax"), inputs);
        Assert.Null(result.Definition);
        Assert.Contains(result.Diagnostics, d => d.Location.Path == PathOf("child.syntax") && d.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bare_names_resolve_a_unique_signed_declaration()
    {
        var result = new LanguageDefinitionResolver().Resolve(new LanguageReference("L", false), PathOf("sample.txt"), Inputs(("root.syntax", Simple)));
        Assert.Equal(PathOf("root.syntax"), result.Definition?.Id);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Legacy_json_keeps_its_default_namespace()
    {
        using var json = JsonDocument.Parse("""
            { "name":"L", "start":"M.R", "extensions":[".l"], "grammars":["root.ngr"] }
            """);
        var result = LanguageDefinitionLoader.LoadLegacy(json.RootElement, PathOf("nitrogen.json"),
            Inputs(("root.ngr", "syntax module M { syntax R = \"x\"; }")));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(GrammarWorkspace.Namespace, result.Definition!.GeneratedNamespace);
        Assert.Equal(".l", Assert.Single(result.Definition.Extensions));
    }

    [Fact]
    public void Conflicting_json_and_declaration_do_not_silently_override()
    {
        using var json = JsonDocument.Parse("""
            { "name":"L", "start":"M.Other", "extensions":[".l"], "grammars":["root.syntax"] }
            """);
        var result = LanguageDefinitionLoader.LoadLegacy(json.RootElement, PathOf("nitrogen.json"), Inputs(("root.syntax", Simple)));
        Assert.Null(result.Definition);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Augment_only_configuration_matches_globs_and_excludes()
    {
        var config = AugmentationConfiguration.Parse(PathOf("nitrogen.json"), """
            { "augmentations": [ { "language": "./Rules.syntax", "files": ["**/*.cs"], "exclude": ["**/bin/**"], "input": "regions" } ] }
            """);
        Assert.Empty(config.Diagnostics);
        Assert.Single(config.Matching(PathOf("Host.cs")));
        Assert.Single(config.Matching(PathOf("sub/Host.cs")));
        Assert.Empty(config.Matching(PathOf("sub/bin/Host.cs")));
        Assert.Empty(config.Matching(Path.GetFullPath(Path.Combine(Root, "..", "Outside.cs"))));
        Assert.Equal(AnalysisInputMode.Regions, Assert.Single(config.Rules).Input);
    }

    [Fact]
    public void Service_accepts_augmentation_only_workspace_configuration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nitrogen-augment-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "nitrogen.json"), "{\"augmentations\":[]}");
            using var service = new NitrogenLanguageService(LspCommand.Registry());
            service.ConfigureWorkspace(directory);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{\"augmentations\":[{\"language\":\"Rules\",\"files\":[\"**/*.cs\"],\"input\":\"bad\"}]}")]
    [InlineData("{\"augmentations\":[{\"language\":\"Rules\",\"files\":[]}]}")]
    [InlineData("{\"augmentations\":{}}")]
    [InlineData("{")]
    public void Invalid_configuration_is_a_diagnostic(string text) =>
        Assert.NotEmpty(AugmentationConfiguration.Parse(PathOf("nitrogen.json"), text).Diagnostics);
}
