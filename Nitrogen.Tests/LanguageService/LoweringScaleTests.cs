using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Lowering a long file finds what blocks each statement through indexes, not a scan of the file per statement.</summary>
public sealed class LoweringScaleTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-lowering-").FullName;

    public LoweringScaleTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    static string Statements(int count) => string.Concat(Enumerable.Range(0, count).Select(i => $"let v{i} = 2026-10-05 + {i} days;\nweekday(v{i});\n"));

    [Fact]
    public void Only_the_last_statement_of_a_long_file_is_blocked_by_its_unresolved_name()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string text = Statements(2000) + "weekday(missing);";
        service.Open(Uri("long.datecalc"), 1, text);
        service.Open(Uri("clean.datecalc"), 1, Statements(2000));

        var inspection = service.InspectDocument(Uri("long.datecalc"))!;
        var blocked = Assert.Single(inspection.Diagnostics, d => d.Code == "NH0002");
        Assert.Equal(text.LastIndexOf("missing", StringComparison.Ordinal), blocked.Origin.Span.Start);
        Assert.Equal(service.InspectDocument(Uri("clean.datecalc"))!.Roots.Count, inspection.Roots.Count); // every other statement lowered
    }

    [Fact]
    public void A_semantic_error_in_the_middle_blocks_only_its_statement()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string text = Statements(1000) + "2026-10-05 + 2026-10-06;\n" + Statements(1000);
        service.Open(Uri("mixed.datecalc"), 1, text);
        service.Open(Uri("clean.datecalc"), 1, Statements(2000));

        var inspection = service.InspectDocument(Uri("mixed.datecalc"))!;
        Assert.Contains(service.Diagnostics(Uri("mixed.datecalc")), d => d.Severity == ServiceSeverity.Error); // date + date
        Assert.Equal(service.InspectDocument(Uri("clean.datecalc"))!.Roots.Count, inspection.Roots.Count);
    }
}
