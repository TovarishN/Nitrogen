using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>DateCalc's quick fixes in the editor: proposed by its fixers, offered only when they work.</summary>
public sealed class QuickFixTests : IDisposable
{
    static readonly DocumentRange Line0 = new(new DocumentPosition(0, 0), new DocumentPosition(0, int.MaxValue));

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-fixes-").FullName;

    public QuickFixTests()
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

    IReadOnlyList<ServiceFix> Fixes(NitrogenLanguageService service, string text, DocumentRange? range = null)
    {
        string uri = Uri("a.datecalc");
        service.Open(uri, 1, text);
        return service.QuickFixes(uri, range ?? Line0);
    }

    static string Apply(string text, ServiceFix fix)
    {
        var lines = new LineMap(text);
        foreach (var edit in fix.Edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
        {
            int start = lines.OffsetOf(edit.Range.Start), end = lines.OffsetOf(edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }
        return text;
    }

    [Theory]
    [InlineData("let d = 2026-02-30;", "Change to 2026-02-28", "let d = 2026-02-28;")]
    [InlineData("2026-13-05;", "Change to 2026-12-05", "2026-12-05;")]
    [InlineData("2026-12-25 + 2026-10-05;", "Use '-'", "2026-12-25 - 2026-10-05;")]
    public void An_error_gets_a_fix_that_removes_it(string text, string title, string fixedText)
    {
        using var service = Service();
        var fix = Assert.Single(Fixes(service, text));

        Assert.Equal(title, fix.Title);
        Assert.Equal(fixedText, Apply(text, fix));
        Assert.Equal(fix.Title.StartsWith("Use", StringComparison.Ordinal) ? "DC0002" : "DC0001", fix.Diagnostic.Code);
    }

    [Theory]
    [InlineData("2 weeks + 2026-10-05;")]          // '-' doesn't apply either
    [InlineData("(1 + 2026-10-05) + 2026-10-06;")] // nor here
    public void A_proposed_fix_that_does_not_remove_the_error_is_not_offered(string text)
    {
        using var service = Service();
        Assert.Empty(Fixes(service, text));
    }

    [Fact]
    public void Only_errors_in_the_range_get_fixes()
    {
        using var service = Service();
        Assert.Empty(Fixes(service, "1 + 1;\nlet d = 2026-02-30;"));
    }

    [Fact]
    public void A_language_without_fixes_offers_none()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = q; }");
        Assert.Empty(service.QuickFixes("file:///w/a.scopes", Line0));
    }

    [Fact]
    public void A_tagged_csharp_string_gets_the_fix_at_csharp_positions()
    {
        using var service = Service();
        string uri = Uri("C.cs");
        const string host = "class C { const string D = /*lang=datecalc*/ \"2026-02-30;\"; }";
        service.Open(uri, 1, host);
        int date = host.IndexOf("2026-02-30", StringComparison.Ordinal);

        var fix = Assert.Single(service.QuickFixes(uri, new DocumentRange(new DocumentPosition(0, date + 2), new DocumentPosition(0, date + 2))));

        Assert.Equal("Change to 2026-02-28", fix.Title);
        Assert.Equal(host.Replace("2026-02-30", "2026-02-28"), Apply(host, fix));
        Assert.Equal(new DocumentPosition(0, date), fix.Diagnostic.Range.Start);
    }
}
