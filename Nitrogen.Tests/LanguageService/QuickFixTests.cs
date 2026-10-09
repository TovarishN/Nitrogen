using System.Text.Json;
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

    [Fact]
    public void Ordinary_csharp_gets_no_fixes_even_beside_a_fixable_string()
    {
        using var service = Service();
        string uri = Uri("C.cs");
        const string host = "class C { const string D = /*lang=datecalc*/ \"2026-02-30;\"; int M() => Math.Max(1, 2); }";
        service.Open(uri, 1, host);
        int max = host.IndexOf("Math.Max", StringComparison.Ordinal);

        Assert.Empty(service.QuickFixes(uri, new DocumentRange(new DocumentPosition(0, max), new DocumentPosition(0, max))));
        Assert.Empty(service.QuickFixes(uri, new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 0))));
    }

    [Fact]
    public async Task The_server_answers_code_actions_with_quick_fixes()
    {
        string doc = Uri("a.datecalc");
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri + "\",\"capabilities\":{}}}";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":\"2026-02-30;\"}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/codeAction\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\"},\"range\":{\"start\":{\"line\":0,\"character\":2},\"end\":{\"line\":0,\"character\":2}},\"context\":{\"diagnostics\":[]}}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service, initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            open, request, """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var kinds = messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("codeActionProvider").GetProperty("codeActionKinds");
        Assert.Equal("quickfix", Assert.Single(kinds.EnumerateArray()).GetString());
        var action = Assert.Single(messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5)
            .GetProperty("result").EnumerateArray());
        Assert.Equal("Change to 2026-02-28", action.GetProperty("title").GetString());
        Assert.Equal("quickfix", action.GetProperty("kind").GetString());
        Assert.Equal("DC0001", Assert.Single(action.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        var edit = Assert.Single(action.GetProperty("edit").GetProperty("changes").GetProperty(doc).EnumerateArray());
        Assert.Equal("2026-02-28", edit.GetProperty("newText").GetString());
        Assert.Equal(10, edit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
    }
}
