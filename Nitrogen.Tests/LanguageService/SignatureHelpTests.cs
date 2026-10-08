using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Signature help: the overloads of the call around the cursor, from a language's hook or a template's declaration.</summary>
public sealed class SignatureHelpTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-signatures-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(string folder)
    {
        string root = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, folder)))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(root);
        return service;
    }

    /// <summary>Signature help with the cursor at the end of <paramref name="text"/>.</summary>
    ServiceSignatureHelp? HelpAtEnd(NitrogenLanguageService service, string folder, string name, string text)
    {
        string uri = new Uri(Path.Combine(_root, folder, name)).AbsoluteUri;
        service.Open(uri, 1, text);
        return service.SignatureHelp(uri, new LineMap(text).PositionOf(text.Length));
    }

    static string Active(ServiceSignatureHelp help) => help.Signatures[help.ActiveSignature].Label;

    static string ActiveParameter(ServiceSignatureHelp help)
    {
        var signature = help.Signatures[help.ActiveSignature];
        var (start, end) = signature.Parameters[help.ActiveParameter];
        return signature.Label[start..end];
    }

    [Fact]
    public void A_second_argument_highlights_the_two_parameter_overload()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "round(2.5, ")!;

        Assert.Equal(2, help.Signatures.Count);
        Assert.Equal("round(x: Core.Scalar, digits: Core.Scalar) → Core.Scalar", Active(help));
        Assert.Equal("digits: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void An_open_call_highlights_the_first_overload_that_fits()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "round(")!;

        Assert.Equal("round(x: Core.Scalar) → Core.Scalar", Active(help));
        Assert.Equal("x: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void The_types_already_typed_choose_the_overload()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "min(2026-10-05, ")!;

        Assert.Equal("min(a: DateCalc.Date, b: DateCalc.Date) → DateCalc.Date", Active(help));
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public void A_nested_call_belongs_to_its_own_arguments()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "round(min(1, 2), ")!;

        Assert.StartsWith("round(", Active(help), StringComparison.Ordinal);
        Assert.Equal(1, help.ActiveParameter);
    }

    [Theory]
    [InlineData("round(2.5, 3)")]          // after the closing ')'
    [InlineData("1 + 2")]                   // no call
    [InlineData("round(2.5;\n1 + ")]        // a broken earlier statement doesn't leak
    [InlineData("frobnicate(")]             // no signatures for the name
    public void Outside_a_known_call_there_is_no_help(string text)
    {
        using var service = Service("DateCalcLanguage");
        Assert.Null(HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", text));
    }

    [Fact]
    public void A_template_call_shows_the_parameters_its_definition_declares()
    {
        using var service = Service("GeometryLanguage");
        var help = HelpAtEnd(service, "GeometryLanguage", "a.geom", "def slab(w: Scalar, h: Scalar, d: Scalar) = box w h d;\nmake slab(1, ")!;

        var signature = Assert.Single(help.Signatures);
        Assert.StartsWith("slab(w: Core.Scalar, h: Core.Scalar, d: Core.Scalar)", signature.Label, StringComparison.Ordinal);
        Assert.Equal("h: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void A_tagged_csharp_string_gets_signature_help()
    {
        using var service = Service("DateCalcLanguage");
        const string host = "class C { const string D = /*lang=datecalc*/ \"round(2.5, ";
        var help = HelpAtEnd(service, "DateCalcLanguage", "C.cs", host)!;

        Assert.Equal("digits: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void A_language_without_signatures_gives_no_help()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = f(");
        Assert.Null(service.SignatureHelp("file:///w/a.scopes", new DocumentPosition(0, 19)));
    }

    [Fact]
    public async Task The_server_answers_signature_help()
    {
        string root = Directory.CreateDirectory(Path.Combine(_root, "DateCalcLanguage")).FullName;
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        string doc = new Uri(Path.Combine(root, "a.datecalc")).AbsoluteUri;
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new Uri(root).AbsoluteUri + "\",\"capabilities\":{}}}";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":\"round(2.5, \"}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\"},\"position\":{\"line\":0,\"character\":11}}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service, initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            open, request, """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var triggers = messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("signatureHelpProvider").GetProperty("triggerCharacters");
        Assert.Equal(["(", ","], triggers.EnumerateArray().Select(t => t.GetString()));
        var help = messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5).GetProperty("result");
        Assert.Equal(2, help.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, help.GetProperty("activeParameter").GetInt32());
        var active = help.GetProperty("signatures")[help.GetProperty("activeSignature").GetInt32()];
        Assert.Equal("round(x: Core.Scalar, digits: Core.Scalar) → Core.Scalar", active.GetProperty("label").GetString());
        var digits = active.GetProperty("parameters")[1].GetProperty("label");
        Assert.Equal("digits: Core.Scalar", active.GetProperty("label").GetString()![digits[0].GetInt32()..digits[1].GetInt32()]);
    }
}
