using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Each DateCalc statement's value, as the editor shows it after the statement.</summary>
public sealed class ValueHintTests : IDisposable
{
    static readonly DocumentRange Whole = new(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-hints-").FullName;

    public ValueHintTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("DateCalc.ngr"), 1, File.ReadAllText(Path.Combine(_root, "DateCalc.ngr")));
        Assert.Empty(service.Diagnostics(Uri("DateCalc.ngr")));
        return service;
    }

    IReadOnlyList<ValueHint> Hints(NitrogenLanguageService service, string text, DocumentRange? range = null)
    {
        string uri = Uri("a.datecalc");
        service.Open(uri, 1, text);
        return service.ValueHints(uri, range ?? Whole);
    }

    [Fact]
    public void The_sample_shows_each_statement_value_at_its_end()
    {
        using var service = Service();
        string text = File.ReadAllText(Path.Combine(_root, "sample.datecalc"));
        var hints = Hints(service, text);

        Assert.Equal(
            ["= 2026-10-05 Mon", "= 14 days", "= 2026-10-19 Mon", "= Monday", "= 81", "= 14", "= 6.28", "= 2026-12-25 Fri"],
            hints.Select(h => h.Label));
        string[] lines = text.Split('\n');
        Assert.Equal(Enumerable.Range(1, 8).Select(line => new DocumentPosition(line, lines[line].TrimEnd().Length)),
            hints.Select(h => h.At));
        Assert.All(hints, h => Assert.False(h.IsError));
        Assert.All(hints, h => Assert.Null(h.Tooltip));
    }

    [Fact]
    public void Only_hints_within_the_range_are_returned()
    {
        using var service = Service();
        var hints = Hints(service, File.ReadAllText(Path.Combine(_root, "sample.datecalc")),
            new DocumentRange(new DocumentPosition(2, 0), new DocumentPosition(3, int.MaxValue)));

        Assert.Equal(["= 14 days", "= 2026-10-19 Mon"], hints.Select(h => h.Label));
    }

    [Fact]
    public void A_document_with_an_error_shows_no_values()
    {
        using var service = Service();
        Assert.Empty(Hints(service, "let a = 2026-10-05 + 2026-10-06;\n1 + 1;"));
    }

    [Fact]
    public void A_failing_statement_shows_a_warning_and_later_ones_still_run()
    {
        using var service = Service();
        var hints = Hints(service, "9999-12-31 + 1 days;\n1 + 1;");

        Assert.Equal(2, hints.Count);
        Assert.True(hints[0].IsError);
        Assert.Equal("= ⚠", hints[0].Label);
        Assert.StartsWith("NP0005: ", hints[0].Tooltip, StringComparison.Ordinal);
        Assert.Contains("DateCalc.Later", hints[0].Tooltip, StringComparison.Ordinal);
        Assert.Equal("= 2", hints[1].Label);
    }

    [Fact]
    public void The_budget_stops_after_the_first_statement_once_spent()
    {
        using var service = Service();
        service.EvaluationBudget = TimeSpan.Zero;

        var hint = Assert.Single(Hints(service, File.ReadAllText(Path.Combine(_root, "sample.datecalc"))));
        Assert.Equal("= 2026-10-05 Mon", hint.Label);
    }

    [Fact]
    public void A_range_at_the_end_of_a_long_file_gets_its_values_without_evaluating_the_rest()
    {
        using var service = Service();
        service.EvaluationBudget = TimeSpan.Zero; // one statement per request
        string text = string.Concat(Enumerable.Range(0, 2000).Select(i => $"{i} + 1;\n"));

        var end = new DocumentRange(new DocumentPosition(1999, 0), new DocumentPosition(2000, 0));
        Assert.Equal("= 2000", Assert.Single(Hints(service, text, end)).Label);
        Assert.Equal(1, service.EvaluatedStatements);
    }

    [Fact]
    public void A_range_asked_again_is_not_evaluated_again_and_a_later_request_goes_on()
    {
        using var service = Service();
        service.EvaluationBudget = TimeSpan.Zero;
        var first = new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(2, 0));
        Assert.Single(Hints(service, "1 + 1;\n2 + 2;\n3 + 3;", first)); // the budget stops after the first
        Assert.Equal(1, service.EvaluatedStatements);

        Assert.Equal(["= 2", "= 4"], service.ValueHints(Uri("a.datecalc"), first).Select(h => h.Label)); // the cached one, then the next
        Assert.Equal(["= 2", "= 4", "= 6"], service.ValueHints(Uri("a.datecalc"), Whole).Select(h => h.Label));
        Assert.Equal(3, service.EvaluatedStatements);
        service.ValueHints(Uri("a.datecalc"), Whole);
        Assert.Equal(3, service.EvaluatedStatements);
    }

    [Fact]
    public void An_edit_shows_the_new_values()
    {
        using var service = Service();
        Assert.Equal("= 2", Assert.Single(Hints(service, "1 + 1;")).Label);

        service.Change(Uri("a.datecalc"), 2, "2 + 2;");
        Assert.Equal("= 4", Assert.Single(service.ValueHints(Uri("a.datecalc"), Whole)).Label);
    }

    [Fact]
    public void A_language_without_a_profile_shows_no_values()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = 1; }");
        Assert.Empty(service.ValueHints("file:///w/a.scopes", Whole));
    }
}
