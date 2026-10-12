using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignatureReviewRegressionTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-signature-review-").FullName;
    const string Names = """
        #!nitrogen grammar
        language Names { start Names.Program; extensions ".names"; }
        syntax module Names {
          symbols { value }
          token Id = ['a'..'z']+ except "let" "use";
          syntax Program = Items:Statement*;
          syntax Statement = Declare / Reference;
          syntax Declare = "let" Name:Id ";" declares value Name in file export;
          syntax Reference = "use" Name:Id ";" references value Name;
        }
        """;
    string FileOf(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    static string UriOf(string path) => new Uri(path).AbsoluteUri;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_and_registered_build_checks_share_primary_exports(bool manifest)
    {
        string grammar = FileOf("Names.syntax", Names);
        string defs = FileOf("defs.txt", "#!nitrogen language Names\nlet item;");
        string use = FileOf("use.txt", "#!nitrogen language Names\nuse item;");
        string input = FileOf("inputs.json", JsonSerializer.Serialize(new { root = _root, files = new[] { grammar, defs, use }, documents = new[] { use, defs } }));
        var output = new StringWriter();
        int result = await NitrogenCli.RunAsync(manifest ? ["check", "--inputs", input] : ["check", _root], output, CancellationToken.None);
        Assert.True(result == 0, output.ToString());
    }

    [Fact]
    public async Task Batch_check_reports_an_unknown_start_without_throwing()
    {
        FileOf("Names.syntax", Names.Replace("start Names.Program", "start Names.Missing", StringComparison.Ordinal));
        string sample = FileOf("use.txt", "#!nitrogen language Names\nuse item;");
        var output = new StringWriter();
        Assert.Equal(1, await NitrogenCli.RunAsync(["check", sample], output, CancellationToken.None));
        Assert.Contains("unknown start rule", output.ToString());
    }

    [Fact]
    public async Task Standalone_external_region_loads_its_grammar_and_transitive_helper()
    {
        FileOf("rules/Calc.syntax", "#!nitrogen grammar\nlanguage Calc { start M.R; sources \"./Check.cs\"; } syntax module M { syntax R = Value:\"ok\" { check QC0001 Checks.Allowed(Value.Text) : \"blocked\" at Value; } }");
        FileOf("rules/Check.cs", "public static class Checks { public static bool Allowed(string text) => true; }");
        string host = FileOf("host/Host.cs", "/* #!nitrogen region \"../rules/Calc.syntax\"\nok\n#!nitrogen end\n*/");
        var output = new StringWriter();
        Assert.True(await NitrogenCli.RunAsync(["check", host], output, CancellationToken.None) == 0, output.ToString());
        string manifest = FileOf("host/inputs.json", JsonSerializer.Serialize(new { root = Path.GetDirectoryName(host), files = new[] { host }, documents = new[] { host } }));
        Assert.Equal(1, await NitrogenCli.RunAsync(["check", "--inputs", manifest], output, CancellationToken.None));
    }

    [Fact]
    public void Declared_extensions_analyze_open_and_closed_unsigned_documents()
    {
        FileOf("Names.syntax", Names);
        string closed = FileOf("closed.names", "use missing;");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        Assert.Contains(service.Diagnostics(UriOf(closed)), d => d.Code == "NB0001");
        string open = UriOf(Path.Combine(_root, "open.names"));
        service.Open(open, 1, "use missing;");
        Assert.Contains(service.Diagnostics(open), d => d.Code == "NB0001");
        service.Change(open, 2, "let item; use item;");
        Assert.Equal(open, Assert.Single(service.Definition(open, new(0, 15))).Uri);
    }

    [Fact]
    public void Generated_owner_keeps_unsigned_extension_files_while_main_owns_explicit_signatures()
    {
        FileOf("Names.syntax", Names);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.SkipEmbedded(["Names", "names"]);
        service.ConfigureWorkspace(_root);
        string uri = UriOf(Path.Combine(_root, "open.names"));
        service.Open(uri, 1, "use missing;");
        Assert.Empty(service.Diagnostics(uri));
        service.Change(uri, 2, "#!nitrogen language Names\nuse missing;");
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NB0001");
    }

    [Theory]
    [InlineData("@attribute [System.ComponentModel.Description(\"<!-- #!nitrogen region Calc -->\")]")]
    [InlineData("@page \"/<!-- #!nitrogen region Calc -->\"")]
    [InlineData("@attribute [System.ComponentModel.Description(\n\"<!-- #!nitrogen region Calc -->\")] ")]
    public async Task Razor_directive_literals_do_not_request_region_analysis(string text)
    {
        string path = FileOf("Page.razor", text);
        var inputs = WorkspaceInputReader.Read(_root);
        Assert.Empty(RegionExtractor.Extract(inputs.Files[path], new LanguageDefinitionResolver(), inputs).Diagnostics);
        using var session = new DocumentAnalysisSession();
        Assert.Empty((await session.AnalyzeAsync(inputs.Files[path], inputs, CancellationToken.None)).Diagnostics);
    }

    [Fact]
    public void Independent_augmentation_sources_keep_identical_diagnostics()
    {
        FileOf("A.syntax", "#!nitrogen grammar\nlanguage A { start A.R; } syntax module A { syntax R = \"ok\"; }");
        FileOf("B.syntax", "#!nitrogen grammar\nlanguage B { start B.R; } syntax module B { syntax R = \"ok\"; }");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string uri = UriOf(Path.Combine(_root, "Host.cs"));
        service.Open(uri, 1, "// #!nitrogen augment A\n// #!nitrogen augment B\nbad");
        Assert.Equal(new[] { "nitrogen:A", "nitrogen:B" }, service.Diagnostics(uri).Select(d => d.Source).Distinct().Order());
    }

    static int CacheCount(NitrogenLanguageService service, string field) =>
        ((IDictionary)typeof(NitrogenLanguageService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).Count;

    [Fact]
    public void Reloading_and_detaching_a_signed_language_retires_old_binding_caches()
    {
        string grammar = FileOf("Names.syntax", Names);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string uri = UriOf(Path.Combine(_root, "sample.txt"));
        service.Open(uri, 1, "#!nitrogen language Names\nuse missing;");
        service.Diagnostics(uri); // Populate semantic caches too.
        int baseline = CacheCount(service, "_projects");
        var retiredContexts = new List<WeakReference>();
        for (int i = 0; i < 3; i++)
        {
            retiredContexts.Add(ContextOf(service, grammar));
            File.WriteAllText(grammar, Names + new string(' ', i + 1));
            service.FileChanged(grammar);
            service.Diagnostics(uri);
            Assert.Equal(baseline, CacheCount(service, "_projects"));
            Assert.InRange(CacheCount(service, "_semantics"), 0, baseline);
            Assert.InRange(CacheCount(service, "_info"), 0, baseline);
        }
        for (int i = 0; i < 20 && retiredContexts.Any(context => context.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.All(retiredContexts, context => Assert.False(context.IsAlive));
        File.Delete(grammar);
        service.FileChanged(grammar);
        Assert.Equal(0, CacheCount(service, "_signatureEntries"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ContextOf(NitrogenLanguageService service, string grammar)
    {
        var session = (DocumentAnalysisSession)typeof(NitrogenLanguageService).GetField("_documentAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        return session.SnapshotFor(grammar)!.LoadContextReference();
    }

    [Fact]
    public void Untitled_program_resolves_from_one_workspace_root_and_navigates()
    {
        FileOf("Names.syntax", Names);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        const string uri = "untitled:Untitled-1";
        service.Open(uri, 1, "#!nitrogen language \"./Names.syntax\"\nlet item; use item;");
        Assert.True(service.IsOpen(uri));
        Assert.Empty(service.Diagnostics(uri));
        Assert.Equal(uri, Assert.Single(service.Definition(uri, new(1, 15))).Uri);
        service.Change(uri, 2, "#!nitrogen language Names\nuse missing;");
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NB0001");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Untitled_relative_reference_requires_one_resolution_root(int count)
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.SetSignatureRoots(Enumerable.Range(0, count).Select(i => Directory.CreateDirectory(Path.Combine(_root, i.ToString())).FullName));
        const string uri = "untitled:Untitled-1";
        service.Open(uri, 1, "#!nitrogen language \"./Names.syntax\"\nok");
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NGR0402" && d.Message.Contains("save", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cached_compilation_warnings_survive_refresh_and_unrelated_editor_changes()
    {
        string grammar = FileOf("Calc.syntax", "#!nitrogen grammar\nlanguage Calc { start M.R; } syntax module M { syntax R = \"ok\" rewrites <[ missing ]>; }");
        var inputs = WorkspaceInputReader.Read(_root);
        using var session = new DocumentAnalysisSession();
        var input = inputs.Files[grammar];
        Assert.Contains((await session.AnalyzeAsync(input, inputs, CancellationToken.None)).Diagnostics, d => d.Code == "NGR0137");
        Assert.Contains((await session.AnalyzeAsync(input, inputs, CancellationToken.None)).Diagnostics, d => d.Code == "NGR0137");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string uri = UriOf(grammar);
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NGR0137");
        service.Open(UriOf(Path.Combine(_root, "unrelated.txt")), 1, "plain text");
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NGR0137");
    }
}
