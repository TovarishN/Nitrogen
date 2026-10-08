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
}
