using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Workspace symbol search across open, closed and embedded documents and closed grammars.</summary>
public sealed class WorkspaceSymbolTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-symbols-").FullName;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service()
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", WorkspaceIndexTests.Grammar);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.IndexWorkspace(_root);
        return service;
    }

    static List<WorkspaceSymbol> Items(NitrogenLanguageService service, string query = "") =>
        service.WorkspaceSymbols(query).Where(s => s.Kind == "item").ToList();

    [Fact]
    public void Open_and_closed_files_are_searched()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "def beta;\nuse alpha;");

        var items = Items(service);
        Assert.Equal(["alpha", "beta"], items.Select(s => s.Name));
        Assert.Equal(Uri("a.links"), items[0].Location.Uri);
        Assert.Equal(new DocumentLocation(Uri("b.links"), new DocumentRange(new DocumentPosition(0, 4), new DocumentPosition(0, 8))), items[1].Location);
        Assert.All(items, s => Assert.Null(s.Container));
    }

    [Fact]
    public void An_open_file_is_found_once()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("a.links"), 1, "def alpha;");
        Assert.Single(Items(service));
    }

    [Fact]
    public void The_query_matches_a_subsequence_ignoring_case()
    {
        using var service = Service();
        service.Open(Uri("a.links"), 1, "def alphabet;\ndef beta;\ndef gamma;");

        Assert.Equal(["alphabet"], Items(service, "ABT").Select(s => s.Name));
        Assert.Equal(["alphabet", "gamma"], Items(service, "aa").Select(s => s.Name));
        Assert.Empty(Items(service, "tb"));
        Assert.Equal(3, Items(service, "").Count);
    }

    static string Letters(int i) => new([(char)('a' + i / 676 % 26), (char)('a' + i / 26 % 26), (char)('a' + i % 26)]);

    [Fact]
    public void Results_are_ordered_by_name_and_capped()
    {
        using var service = Service();
        // In reverse, so the order must come from sorting.
        service.Open(Uri("a.links"), 1, string.Concat(Enumerable.Range(0, 1_005).Reverse().Select(i => $"def {Letters(i)};\n")));

        var all = service.WorkspaceSymbols("");
        Assert.Equal(1_000, all.Count);
        Assert.Equal(all.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase), all.Select(s => s.Name));
    }

    [Fact]
    public void A_tagged_string_symbol_is_located_in_its_csharp_file()
    {
        using var service = Service();
        const string host = "class C\n{\n    object R = Run(/*lang=links*/ \"def alpha;\");\n}\n";
        service.Open(Uri("C.cs"), 1, host);

        var alpha = Assert.Single(Items(service));
        Assert.Equal(Uri("C.cs"), alpha.Location.Uri);
        int column = host.Split('\n')[2].IndexOf("alpha", StringComparison.Ordinal);
        Assert.Equal(new DocumentRange(new DocumentPosition(2, column), new DocumentPosition(2, column + 5)), alpha.Location.Range);
    }

    [Fact]
    public void Built_ins_are_not_returned()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            if (Path.GetFileName(file) != "sample.datecalc") File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("a.datecalc"), 1, "let start = 2026-10-05;\nweekday(start);");

        var names = service.WorkspaceSymbols("").Where(s => s.Location.Uri == Uri("a.datecalc")).Select(s => s.Name).ToList();
        Assert.Equal(["start"], names);
        Assert.DoesNotContain(service.WorkspaceSymbols("weekday"), s => s.Name == "weekday");
    }
}
