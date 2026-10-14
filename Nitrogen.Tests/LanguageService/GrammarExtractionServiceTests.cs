using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarExtractionServiceTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-extract-").FullName;
    const string Rule = "syntax Word = \"x\";";
    const string Grammar = "syntax module A { " + Rule + " syntax Program = Word; }";
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service(string pattern = "a.ngr", string? helpers = null)
    {
        File.WriteAllText(Path.Combine(_root, "a.ngr"), Grammar);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), $$"""
            { "languages": [{ "name": "A", "extensions": [".a"], "start": "A.Program", "grammars": ["{{pattern}}"]{{helpers}} }], "custom": 42 }
            """);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("a.ngr"), 1, Grammar);
        return service;
    }
    ExtractionPreparation Prepare(NitrogenLanguageService service, string text = Grammar, string selected = Rule)
    {
        int at = text.IndexOf(selected, StringComparison.Ordinal);
        var map = new LineMap(text);
        return service.PrepareExtraction(Uri("a.ngr"), new(map.PositionOf(at), map.PositionOf(at + selected.Length)));
    }
    [Fact]
    public void Proposes_source_creation_and_configuration_changes_without_writing_files()
    {
        using var service = Service();
        var prepared = Prepare(service);
        Assert.Null(prepared.UnavailableReason);
        var result = service.ResolveExtraction(new(prepared.Id!, Uri("aa.ngr"), "Shared"));
        Assert.Null(result.UnavailableReason);
        Assert.Contains("syntax Word", result.DestinationText);
        Assert.Contains(result.Changes, e => e.Uri == Uri("a.ngr") && e.Version == 1);
        Assert.Contains(result.Changes, e => e.Uri == Uri("nitrogen.json"));
        Assert.False(File.Exists(Path.Combine(_root, "aa.ngr")));
        Assert.Equal(Grammar, File.ReadAllText(Path.Combine(_root, "a.ngr")));
    }
    [Fact]
    public void A_grammar_glob_does_not_need_a_configuration_edit()
    {
        using var service = Service("*.ngr");
        var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("aa.ngr"), "Shared"));
        Assert.Null(result.UnavailableReason);
        Assert.DoesNotContain(result.Changes, c => c.Uri == Uri("nitrogen.json"));
    }
    [Fact]
    public void Rejects_a_stale_source_or_closed_dependency()
    {
        using var service = Service();
        var prepared = Prepare(service);
        service.Change(Uri("a.ngr"), 2, Grammar + "\n");
        Assert.Contains("changed", service.ResolveExtraction(new(prepared.Id!, Uri("aa.ngr"), "Shared")).UnavailableReason!);
        prepared = Prepare(service, Grammar + "\n");
        File.AppendAllText(Path.Combine(_root, "nitrogen.json"), "\n");
        Assert.Contains("changed", service.ResolveExtraction(new(prepared.Id!, Uri("aa.ngr"), "Shared")).UnavailableReason!);
    }
    [Theory]
    [InlineData("close")]
    [InlineData("reopen")]
    [InlineData("open-config")]
    public void Reviewing_unchanged_documents_does_not_invalidate_extraction(string transition)
    {
        using var service = Service();
        var request = new ExtractionRequest(Prepare(service).Id!, Uri("aa.ngr"), "Shared");
        var reviewed = service.ResolveExtraction(request);
        Assert.Null(reviewed.UnavailableReason);
        if (transition == "open-config")
            service.Open(Uri("nitrogen.json"), 1, File.ReadAllText(Path.Combine(_root, "nitrogen.json")));
        else
        {
            service.Close(Uri("a.ngr"));
            if (transition == "reopen")
                service.Open(Uri("a.ngr"), 7, Grammar);
        }
        var confirmed = service.ResolveExtraction(request);
        Assert.Null(confirmed.UnavailableReason);
        Assert.Equal(reviewed.DestinationText, confirmed.DestinationText);
        Assert.Equal(reviewed.Changes.Select(c => c.Uri), confirmed.Changes.Select(c => c.Uri));
        var sourceChange = Assert.Single(confirmed.Changes, c => c.Uri == Uri("a.ngr"));
        Assert.Equal(service.VersionOf(Uri("a.ngr")), sourceChange.Version);
        service.Open(Uri("a.ngr"), 8, Grammar + "\n");
        Assert.Contains("changed", service.ResolveExtraction(request).UnavailableReason!);
    }
    [Theory]
    [InlineData("a.ngr", "edit")]
    [InlineData("nitrogen.json", "edit")]
    [InlineData("nitrogen.json", "remove")]
    [InlineData("new.ngr", "add")]
    public void Stale_extraction_identifies_the_changed_workspace_file(string name, string change)
    {
        using var service = Service();
        var prepared = Prepare(service);
        if (change == "remove")
            File.Delete(Path.Combine(_root, name));
        else if (name == "a.ngr")
            service.Change(Uri(name), 2, Grammar + "\n");
        else
            File.AppendAllText(Path.Combine(_root, name), "\n");
        var result = service.ResolveExtraction(new(prepared.Id!, Uri("aa.ngr"), "Shared"));
        Assert.Contains(name, result.UnavailableReason!);
        Assert.Empty(result.Changes);
    }
    [Fact]
    public void Rejects_existing_and_outside_destinations()
    {
        using var service = Service();
        var prepared = Prepare(service);
        Assert.NotNull(service.ResolveExtraction(new(prepared.Id!, Uri("a.ngr"), "Shared")).UnavailableReason);
        Assert.NotNull(service.ResolveExtraction(new(prepared.Id!, new System.Uri(Path.Combine(Path.GetTempPath(), "outside.ngr")).AbsoluteUri, "Shared")).UnavailableReason);
    }
    [Fact]
    public void Helper_generated_kind_references_block_an_unsafe_move()
    {
        File.WriteAllText(Path.Combine(_root, "Helper.cs"), "public static class Helper { public static int Kind = Nitrogen.Workspace.Grammar.AKinds.Word; }");
        using var service = Service(helpers: ", \"sources\": [\"Helper.cs\"]");
        var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("aa.ngr"), "Shared"));
        Assert.NotNull(result.UnavailableReason);
        Assert.Empty(result.Changes);
    }
    [Fact]
    public void Updates_a_configured_start_rule_and_preserves_unrelated_json_text()
    {
        using var service = Service();
        var result = service.ResolveExtraction(new(Prepare(service, selected: "syntax Program = Word;").Id!, Uri("aa.ngr"), "Shared"));
        Assert.Null(result.UnavailableReason);
        var config = Assert.Single(result.Changes, c => c.Uri == Uri("nitrogen.json"));
        string before = File.ReadAllText(Path.Combine(_root, "nitrogen.json"));
        string after = RenameTests.Apply(before, config.Edits);
        Assert.Contains("\"start\": \"Shared.Program\"", after);
        Assert.Contains("\"custom\": 42", after);
    }
    [Fact]
    public void Rejects_a_destination_caught_by_an_unrelated_language_glob()
    {
        using var service = Service();
        string config = File.ReadAllText(Path.Combine(_root, "nitrogen.json"));
        File.WriteAllText(Path.Combine(_root, "b.ngr"), "syntax module B { syntax Program = \"b\"; }");
        config = config.Replace("}], \"custom\"", "}, { \"name\": \"B\", \"extensions\": [\".b\"], \"start\": \"B.Program\", \"grammars\": [\"b.ngr\", \"**/aa*.ngr\"] }], \"custom\"");
        // A broad glob needs an existing match to be a valid baseline.
        File.WriteAllText(Path.Combine(_root, "aa-existing.ngr"), "syntax module Existing { }");
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), config);
        var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("aa-new.ngr"), "Shared"));
        Assert.Contains("unrelated", result.UnavailableReason!);
    }
    [Fact]
    public void Supports_a_self_declared_language_without_json_configuration()
    {
        const string text = "language Test { start A.Program; extensions \".a\"; }\n" + Grammar;
        File.WriteAllText(Path.Combine(_root, "a.ngr"), text);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("a.ngr"), 1, text);
        var result = service.ResolveExtraction(new(Prepare(service, text).Id!, Uri("aa.ngr"), "Shared"));
        Assert.Null(result.UnavailableReason);
        string updated = RenameTests.Apply(text, Assert.Single(result.Changes, c => c.Uri == Uri("a.ngr")).Edits);
        Assert.Contains("grammars \"aa.ngr\";", updated);
    }
    [Fact]
    public void Rejects_a_symbolic_link_destination_folder()
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = Service();
        string outside = Directory.CreateTempSubdirectory("nitrogen-extract-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
            var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("linked/new.ngr"), "Shared"));
            Assert.Contains("symbolic", result.UnavailableReason!);
        }
        finally { Directory.Delete(outside, true); }
    }
    [Theory]
    [InlineData("a.ngr")]
    [InlineData("nitrogen.json")]
    public void Refuses_editing_a_source_or_configuration_through_a_symbolic_link(string name)
    {
        if (OperatingSystem.IsWindows()) return;
        using var service = Service();
        string outside = Directory.CreateTempSubdirectory("nitrogen-extract-linked-source-").FullName;
        try
        {
            string path = Path.Combine(_root, name), target = Path.Combine(outside, name);
            File.Copy(path, target);
            File.Delete(path);
            File.CreateSymbolicLink(path, target);
            var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("aa.ngr"), "Shared"));
            Assert.Contains("symbolic", result.UnavailableReason!);
            Assert.Empty(result.Changes);
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(target));
        }
        finally { Directory.Delete(outside, true); }
    }
    [Fact]
    public void Refuses_helper_factories_that_compose_only_the_original_module()
    {
        File.WriteAllText(Path.Combine(_root, "Helper.cs"), "public static class Helper { public static Nitrogen.Language Make() => new Nitrogen.LanguageBuilder().Add(Nitrogen.Workspace.Grammar.AModule.Instance).Build(); }");
        using var service = Service(helpers: ", \"sources\": [\"Helper.cs\"]");
        var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("aa.ngr"), "Shared"));
        Assert.Contains("module", result.UnavailableReason!);
    }
    [Fact]
    public void Refuses_moving_a_syntax_rule_used_as_a_quotation_category_string()
    {
        File.WriteAllText(Path.Combine(_root, "Helper.cs"), """
            public static class Helper
            {
                public static object Quote() => Nitrogen.Workspace.Grammar.AComposition.CreateBuilder().Build()
                    .CompileQuote("A.Word", "x", new System.Collections.Generic.Dictionary<string, Nitrogen.QuoteHole>());
            }
            """);
        using var service = Service(helpers: ", \"sources\": [\"Helper.cs\"]");
        var result = service.ResolveExtraction(new(Prepare(service).Id!, Uri("aa.ngr"), "Shared"));
        Assert.Contains("quotation", result.UnavailableReason!);
        Assert.Empty(result.Changes);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
