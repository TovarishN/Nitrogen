using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Hovering an expression of a language with an evaluation profile shows its value under the summary.</summary>
public sealed class HoverValueTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-hover-").FullName;

    public HoverValueTests()
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
        return service;
    }

    /// <summary>The position of <paramref name="text"/>'s first occurrence, plus <paramref name="shift"/> characters.</summary>
    static DocumentPosition At(string source, string text, int shift = 0)
    {
        int offset = source.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{text}' is not in the text");
        return new LineMap(source).PositionOf(offset + shift);
    }

    /// <summary>The hover markdown at <paramref name="text"/> + <paramref name="shift"/> in a document holding <paramref name="source"/>.</summary>
    string Hover(NitrogenLanguageService service, string name, string source, string text, int shift)
    {
        string uri = Uri(name);
        service.Open(uri, 1, source);
        return service.Hover(uri, At(source, text, shift))?.Markdown ?? "";
    }

    string Sample => File.ReadAllText(Path.Combine(_root, "sample.datecalc"));

    [Fact]
    public void A_subexpression_shows_its_value()
    {
        using var service = Service();
        string hover = Hover(service, "a.datecalc", Sample, "3 * sprint", 2); // at '*'

        Assert.Contains("`DateCalc.Times`", hover, StringComparison.Ordinal);
        Assert.EndsWith("\n\n= 42 days", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_literal_shows_the_date_it_names()
    {
        using var service = Service();
        Assert.EndsWith("\n\n= 2026-10-05 Mon", Hover(service, "a.datecalc", Sample, "2026-10-05;", 3), StringComparison.Ordinal);
    }

    [Fact]
    public void A_builtin_name_shows_its_value()
    {
        using var service = Service();
        Assert.Contains("\n\n= 3.1415927", Hover(service, "a.datecalc", Sample, "pi", 1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_let_name_keeps_its_declaration_hover_without_a_value()
    {
        using var service = Service();
        string hover = Hover(service, "a.datecalc", Sample, "start + sprint", "start + ".Length + 1);

        Assert.Contains("declared in", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n= ", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_literal_has_no_second_value_line()
    {
        using var service = Service();
        string hover = Hover(service, "a.datecalc", Sample, "3 * sprint", 0);

        Assert.Contains("= 3", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n= ", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_expression_shows_why()
    {
        using var service = Service();
        Assert.Contains("\n\n= ⚠ NP0005: ", Hover(service, "a.datecalc", "9999-12-31 + 1 days;", "+", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_on_another_line_does_not_hide_the_value()
    {
        using var service = Service();
        Assert.EndsWith("\n\n= 3", Hover(service, "a.datecalc", "1 + 2;\n2026-10-05 + 2026-10-06;", "+", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_language_without_a_profile_shows_no_value()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = 1; let z = y; }");
        string hover = service.Hover("file:///w/a.scopes", At("unit a { let y = 1; let z = y; }", "y; }"))?.Markdown ?? "";

        Assert.DoesNotContain("\n\n= ", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tagged_csharp_string_shows_values_on_hover()
    {
        using var service = Service();
        const string host = "class C { const string D = /*lang=datecalc*/ \"let s = 2 weeks; 3 * s;\"; }";

        Assert.EndsWith("\n\n= 42 days", Hover(service, "C.cs", host, "3 * s", 2), StringComparison.Ordinal);
    }
}
