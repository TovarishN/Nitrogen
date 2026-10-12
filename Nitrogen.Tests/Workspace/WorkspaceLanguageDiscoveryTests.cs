using Nitrogen.Grammar;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class WorkspaceLanguageDiscoveryTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "nitrogen-discovery");
    static string PathOf(string path) => Path.Combine(Root, path);
    static string Grammar(string name = "Calc") => "#!nitrogen grammar\nlanguage " + name + " { start M.R; } syntax module M { syntax R = \"ok\"; }";
    static InputSnapshot Inputs(params (string Path, string Text)[] files) => new(Root, files.ToDictionary(f => PathOf(f.Path), f => new DocumentInput(PathOf(f.Path), f.Text, 1)), true);

    [Fact]
    public void A_unique_signed_root_resolves_by_declared_name()
    {
        var resolver = new LanguageDefinitionResolver();
        var result = resolver.Resolve(new("Calc", false), PathOf("sample.txt"), Inputs(("Calc.syntax", Grammar())));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(PathOf("Calc.syntax"), result.Definition?.Id);
    }

    [Fact]
    public void Ambiguous_names_list_all_candidates_in_deterministic_order()
    {
        var inputs = Inputs(("z.syntax", Grammar()), ("a.syntax", Grammar()));
        var result = new LanguageDefinitionResolver().Resolve(new("Calc", false), PathOf("sample.txt"), inputs);
        Assert.Null(result.Definition);
        string message = Assert.Single(result.Diagnostics).Message;
        Assert.Contains(PathOf("a.syntax"), message);
        Assert.Contains(PathOf("z.syntax"), message);
        Assert.True(message.IndexOf(PathOf("a.syntax"), StringComparison.Ordinal) < message.IndexOf(PathOf("z.syntax"), StringComparison.Ordinal));
        Assert.NotNull(new LanguageDefinitionResolver().Resolve(new("./a.syntax", true), PathOf("sample.txt"), inputs).Definition);
    }

    [Fact]
    public void Index_tracks_add_rename_delete_and_ignores_unsigned_or_malformed_declarations()
    {
        var index = new WorkspaceLanguageIndex();
        index.Update(Inputs(("Calc.syntax", Grammar()), ("unsigned.txt", Grammar().Replace("#!nitrogen grammar", "")), ("bad.syntax", "#!nitrogen grammar\nlanguage Calc {}")));
        Assert.Equal([PathOf("Calc.syntax")], index.Find("Calc"));
        Assert.NotEmpty(index.Diagnostics);
        index.Update(Inputs(("renamed.syntax", Grammar("Calendar"))));
        Assert.Empty(index.Find("Calc"));
        Assert.Equal([PathOf("renamed.syntax")], index.Find("Calendar"));
        index.Update(Inputs());
        Assert.Empty(index.Find("Calendar"));
    }

    [Fact]
    public void External_explicit_dependencies_are_not_name_discovered_but_registered_inputs_are()
    {
        string external = Path.GetFullPath(Path.Combine(Root, "..", "external.syntax"));
        var files = new Dictionary<string, DocumentInput> { [external] = new(external, Grammar(), 1) };
        var inputs = new InputSnapshot(Root, files, false);
        var resolver = new LanguageDefinitionResolver();
        Assert.Null(resolver.Resolve(new("Calc", false), PathOf("sample.txt"), inputs).Definition);
        Assert.NotNull(resolver.Resolve(new(external, true), PathOf("sample.txt"), inputs).Definition);
        Assert.NotNull(resolver.Resolve(new("Calc", false), PathOf("sample.txt"), inputs with { RegisteredInputsOnly = true }).Definition);
    }

    [Fact]
    public void Resolver_observes_new_snapshots_and_missing_names_are_explicit()
    {
        var resolver = new LanguageDefinitionResolver();
        Assert.NotNull(resolver.Resolve(new("Calc", false), PathOf("a.txt"), Inputs(("a.syntax", Grammar()))).Definition);
        var missing = resolver.Resolve(new("Calc", false), PathOf("a.txt"), Inputs(("a.syntax", Grammar("Calendar"))));
        Assert.Null(missing.Definition);
        Assert.Contains("Calc", Assert.Single(missing.Diagnostics).Message);
    }

    [Fact]
    public async Task Unchanged_name_keeps_last_good_analysis_after_a_grammar_body_error()
    {
        using var session = new DocumentAnalysisSession();
        var document = new DocumentInput(PathOf("sample.txt"), "#!nitrogen language Calc\nbad", 1);
        var valid = await session.AnalyzeAsync(document, Inputs(("a.syntax", Grammar())), CancellationToken.None);
        Assert.Contains(valid.Diagnostics, d => d.IsError && d.Location.Path == document.Path);
        var broken = await session.AnalyzeAsync(document with { Version = 2 }, Inputs(("a.syntax", Grammar() + " invalid")), CancellationToken.None);
        Assert.Contains(broken.Diagnostics, d => d.Code == "NGR0405");
        Assert.Contains(broken.Diagnostics, d => d.Location.Path == document.Path && d.Code != "NGR0402");
        var renamed = await session.AnalyzeAsync(document with { Version = 3 }, Inputs(("a.syntax", Grammar("Calendar"))), CancellationToken.None);
        Assert.DoesNotContain(renamed.Diagnostics, d => d.Code == "NGR0405");
        Assert.Empty(renamed.Classification!.Selections);
    }
}
