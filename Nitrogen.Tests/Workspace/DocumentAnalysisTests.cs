using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DocumentAnalysisTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nitrogen-analysis-tests"));
    const string Grammar = """
        #!nitrogen grammar
        language Calc { start M.Program; sources "./Checks.cs"; extensions ".calc"; }
        syntax module M {
            token Word = ['a'..'z']+;
            syntax Program = Name:Word { check QC0001 CheckRules.Allowed(Name.Text) : "blocked" at Name; }
        }
        """;
    static string PathOf(string name) => Path.Combine(Root, name);
    static InputSnapshot Inputs(params (string Name, string Text)[] files) => new(Root,
        files.Append(("Calc.syntax", Grammar)).Append(("Checks.cs", "public static class CheckRules { public static bool Allowed(string text) => text != \"bad\"; }"))
            .ToDictionary(f => PathOf(f.Item1), f => new DocumentInput(PathOf(f.Item1), f.Item2, 1)), true);
    static DocumentInput Document(string name, string text, long version = 1) => new(PathOf(name), text, version);

    [Fact]
    public async Task Signed_program_runs_real_helper_checks_and_maps_header_offsets()
    {
        const string text = "#!nitrogen language \"./Calc.syntax\"\r\nbad";
        using var session = new DocumentAnalysisSession();
        var result = await session.AnalyzeAsync(Document("sample.txt", text), Inputs(), CancellationToken.None);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "QC0001");
        Assert.Equal(new AnalysisLocation(PathOf("sample.txt"), text.IndexOf("bad", StringComparison.Ordinal), 3), diagnostic.Location);
        Assert.Contains("Calc", diagnostic.Source);
    }

    [Fact]
    public async Task Signature_and_glob_select_one_contribution()
    {
        const string text = "// #!nitrogen augment \"./Calc.syntax\"\nbad";
        var inputs = Inputs(("nitrogen.json", """
            { "augmentations":[{"language":"./Calc.syntax","files":["**/*.cs"]}] }
            """));
        using var session = new DocumentAnalysisSession();
        var result = await session.AnalyzeAsync(Document("Host.cs", text), inputs, CancellationToken.None);
        Assert.Single(result.Diagnostics, d => d.Code == "QC0001");
    }

    [Fact]
    public async Task Regions_map_each_body_and_ignore_surrounding_native_source()
    {
        const string region = "/* #!nitrogen region \"./Calc.syntax\"\r\nbad\r\n#!nitrogen end\r\n*/";
        string text = "// 😀\r\nclass Host {}\r\n" + region + "\r\n" + region;
        using var session = new DocumentAnalysisSession();
        var result = await session.AnalyzeAsync(Document("Host.cs", text), Inputs(), CancellationToken.None);
        var checks = result.Diagnostics.Where(d => d.Code == "QC0001").ToArray();
        Assert.Equal(2, checks.Length);
        Assert.Equal(text.IndexOf("bad", StringComparison.Ordinal), checks[0].Location.Start);
        Assert.Equal(text.LastIndexOf("bad", StringComparison.Ordinal), checks[1].Location.Start);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code.StartsWith("NP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Regions_selector_does_not_parse_ordinary_comments()
    {
        using var session = new DocumentAnalysisSession();
        var result = await session.AnalyzeAsync(Document("Host.cs", "// #!nitrogen augment \"./Calc.syntax\" regions\n/* bad */\nclass Host {}"), Inputs(), CancellationToken.None);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Excluded_closed_native_files_are_unselected()
    {
        var inputs = Inputs(("nitrogen.json", """
            { "augmentations":[{"language":"./Calc.syntax","files":["**/*.cs"],"exclude":["**/obj/**"]}] }
            """));
        using var session = new DocumentAnalysisSession();
        Assert.Empty((await session.AnalyzeAsync(Document("obj/Host.cs", "bad"), inputs, CancellationToken.None)).Diagnostics);
    }

    [Fact]
    public async Task Grammar_and_helper_edits_invalidate_cached_checks()
    {
        using var session = new DocumentAnalysisSession();
        var inputs = Inputs();
        Assert.Contains((await session.AnalyzeAsync(Document("a.calc", "#!nitrogen language \"./Calc.syntax\"\nbad"), inputs, CancellationToken.None)).Diagnostics, d => d.Code == "QC0001");
        var files = inputs.Files.ToDictionary(p => p.Key, p => p.Value);
        files[PathOf("Checks.cs")] = new(PathOf("Checks.cs"), "public static class CheckRules { public static bool Allowed(string text) => true; }", 2);
        Assert.Empty((await session.AnalyzeAsync(Document("a.calc", "#!nitrogen language \"./Calc.syntax\"\nbad", 2), inputs with { Files = files }, CancellationToken.None)).Diagnostics);
    }

    [Fact]
    public async Task Broken_grammar_keeps_last_good_analysis_with_visible_status()
    {
        using var session = new DocumentAnalysisSession();
        var document = Document("sample.txt", "#!nitrogen language \"./Calc.syntax\"\nbad");
        var inputs = Inputs();
        Assert.Contains((await session.AnalyzeAsync(document, inputs, CancellationToken.None)).Diagnostics, d => d.Code == "QC0001");
        var files = inputs.Files.ToDictionary(p => p.Key, p => p.Value);
        files[PathOf("Calc.syntax")] = new(PathOf("Calc.syntax"), Grammar + " invalid", 2);
        var diagnostics = (await session.AnalyzeAsync(document with { Version = 2 }, inputs with { Files = files }, CancellationToken.None)).Diagnostics;
        Assert.Contains(diagnostics, d => d.Code == "QC0001");
        Assert.Contains(diagnostics, d => d.Code == "NGR0405");
        Assert.Contains(diagnostics, d => d.IsError && d.Location.Path == PathOf("Calc.syntax"));
    }

    [Fact]
    public async Task Regions_do_not_share_bindings()
    {
        const string grammar = """
            #!nitrogen grammar
            language Bindings { start Scopes.Program; }
            syntax module Scopes {
                symbols { value }
                token Identifier = ['a'..'z']+ except "let" "use";
                syntax Program = Items:Statement*;
                syntax Statement = Declare / Reference;
                syntax Declare = "let" Name:Identifier ";" declares value Name;
                syntax Reference = "use" Name:Identifier ";" references value Name;
            }
            """;
        const string text = "/* #!nitrogen region \"./Bindings.syntax\"\nlet x;\n#!nitrogen end\n*/\n/* #!nitrogen region \"./Bindings.syntax\"\nuse x;\n#!nitrogen end\n*/";
        using var session = new DocumentAnalysisSession();
        var diagnostics = (await session.AnalyzeAsync(Document("Host.cs", text), Inputs(("Bindings.syntax", grammar)), CancellationToken.None)).Diagnostics;
        Assert.Contains(diagnostics, d => d.Code == "NB0001" && d.Location.Start == text.LastIndexOf("x;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_augmentation_languages_keep_independent_sources()
    {
        const string text = "// #!nitrogen augment \"./Calc.syntax\"\n// #!nitrogen augment \"./Other.syntax\"\nbad";
        using var session = new DocumentAnalysisSession();
        var diagnostics = (await session.AnalyzeAsync(Document("Host.cs", text), Inputs(("Other.syntax", Grammar.Replace("language Calc", "language Other"))), CancellationToken.None)).Diagnostics;
        Assert.Equal(new[] { "nitrogen:Calc", "nitrogen:Other" }, diagnostics.Where(d => d.Code == "QC0001").Select(d => d.Source).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Malformed_signature_does_not_fall_back_to_extension()
    {
        using var session = new DocumentAnalysisSession();
        var result = await session.AnalyzeAsync(Document("a.calc", "#!nitrogen language\nbad"), Inputs(), CancellationToken.None);
        Assert.Contains(result.Diagnostics, d => d.Code == "NGR0401");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "QC0001");
    }
}
