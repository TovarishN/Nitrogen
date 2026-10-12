using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class AugmentationLspTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-augment-").FullName;
    const string Grammar = """
        #!nitrogen grammar
        language Rules { start Rules.Document; sources "./Rules.cs"; }
        syntax module Rules {
            token Content = .+;
            syntax Document = Body:Content { check AU0001 Checks.Allowed(Body.Text) : "forbidden marker" at Body; }
        }
        """;
    string UriOf(string name) => new Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service(string? config = null)
    {
        File.WriteAllText(Path.Combine(_root, "Rules.syntax"), Grammar);
        File.WriteAllText(Path.Combine(_root, "Rules.cs"), "public static class Checks { public static bool Allowed(string text) => !text.Contains(\"Forbidden\"); }");
        if (config is not null) File.WriteAllText(Path.Combine(_root, "nitrogen.json"), config);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        Assert.Empty(service.Diagnostics(UriOf("Rules.syntax")));
        return service;
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("Host.cs", "// #!nitrogen augment \"./Rules.syntax\"\nclass Forbidden {}")]
    [InlineData("Page.razor", "<!-- #!nitrogen augment \"./Rules.syntax\" -->\n<h1>Forbidden</h1>")]
    public void Native_documents_get_own_diagnostics_without_primary_features(string file, string text)
    {
        using var service = Service();
        service.Open(UriOf(file), 1, text);
        var diagnostic = Assert.Single(service.Diagnostics(UriOf(file)), d => d.Code == "AU0001");
        Assert.Equal("nitrogen:Rules", diagnostic.Source);
        Assert.Empty(service.Definition(UriOf(file), new DocumentPosition(1, 1)));
        Assert.Empty(service.Format(UriOf(file), new FormattingOptions(2, true)));
    }

    [Fact]
    public void Closed_glob_matched_files_are_checked_and_cleared_when_rule_is_removed()
    {
        File.WriteAllText(Path.Combine(_root, "Host.cs"), "class Forbidden {}");
        using var service = Service("""
            { "augmentations":[{"language":"./Rules.syntax","files":["Host.cs"]}] }
            """);
        Assert.Contains(UriOf("Host.cs"), service.ClosedDiagnosticFiles());
        Assert.Contains(service.Diagnostics(UriOf("Host.cs")), d => d.Code == "AU0001");
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), "{\"augmentations\":[]}");
        service.FileChanged(Path.Combine(_root, "nitrogen.json"));
        Assert.Empty(service.Diagnostics(UriOf("Host.cs")));
    }

    [Fact]
    public void Unsaved_helper_edits_recheck_augmentation_without_restart()
    {
        using var service = Service();
        string uri = UriOf("Host.cs");
        service.Open(uri, 1, "// #!nitrogen augment \"./Rules.syntax\"\nclass Forbidden {}");
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "AU0001");
        service.Open(UriOf("Rules.cs"), 1, "public static class Checks { public static bool Allowed(string text) => true; }");
        Assert.Empty(service.Diagnostics(uri));
        service.Close(UriOf("Rules.cs"));
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "AU0001");
    }
}
