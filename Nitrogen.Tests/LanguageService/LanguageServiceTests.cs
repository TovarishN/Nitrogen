using Nitrogen.LanguageService;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The language service's documents and diagnostics on the Scopes test language (issue 238).</summary>
public class LanguageServiceTests
{
    internal static readonly Presentation ScopesPresentation = new(new Dictionary<string, SymbolStyle>
    {
        ["unit"] = new(TokenType.Class, OutlineKind.Class),
        ["value"] = new(TokenType.Variable, OutlineKind.Variable),
        ["func"] = new(TokenType.Function, OutlineKind.Function),
    });

    internal static LanguageRegistry ScopesRegistry()
    {
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("scopes", FileBindingTests.Scopes,
            new Dictionary<string, Rule> { [".scopes"] = ScopesModule.File }, ScopesPresentation));
        return registry;
    }

    [Fact]
    public void An_unmapped_kind_is_a_variable()
    {
        Assert.Equal(new SymbolStyle(TokenType.Class, OutlineKind.Class), ScopesPresentation.StyleOf("unit"));
        Assert.Equal(SymbolStyle.Default, ScopesPresentation.StyleOf("nothing"));
        Assert.Equal(new SymbolStyle(TokenType.Variable, OutlineKind.Variable), SymbolStyle.Default);
    }

    [Fact]
    public void A_file_type_nobody_registered_is_not_served()
    {
        using var service = new NitrogenLanguageService(ScopesRegistry());
        Assert.Empty(service.Open("file:///w/a.txt", 1, "anything"));
        Assert.False(service.IsOpen("file:///w/a.txt"));
        Assert.Empty(service.Diagnostics("file:///w/a.txt"));
    }

    [Fact]
    public void Parse_and_binding_diagnostics_come_with_ranges()
    {
        using var service = new NitrogenLanguageService(ScopesRegistry());
        const string uri = "file:///w/a.scopes";
        Assert.Equal(new[] { uri }, service.Open(uri, 1, "unit a {\n  let x = ;\n  let y = q;\n}"));
        var diagnostics = service.Diagnostics(uri);
        Assert.Contains(diagnostics, d => d.Code == "NB0001" && d.Severity == ServiceSeverity.Error
            && d.Range == new DocumentRange(new(2, 10), new(2, 11)));
        Assert.Contains(diagnostics, d => d.Code != "NB0001" && d.Range.Start.Line == 1);
        Assert.Equal(1, service.VersionOf(uri));

        service.Change(uri, 2, "unit a {\n  let x = 1;\n  let y = x;\n}");
        Assert.Empty(service.Diagnostics(uri));
        Assert.Equal(2, service.VersionOf(uri));
    }

    [Fact]
    public void Opening_or_closing_a_document_updates_the_others_of_its_language()
    {
        using var service = new NitrogenLanguageService(ScopesRegistry());
        service.Open("file:///w/b.scopes", 1, "unit b { use a; }");
        Assert.Equal(new[] { "NB0001" }, service.Diagnostics("file:///w/b.scopes").Select(d => d.Code));

        var affected = service.Open("file:///w/a.scopes", 1, "unit a { }");
        Assert.Equal(new[] { "file:///w/a.scopes", "file:///w/b.scopes" }, affected.Order());
        Assert.Empty(service.Diagnostics("file:///w/b.scopes"));

        Assert.Equal(new[] { "file:///w/b.scopes" }, service.Close("file:///w/a.scopes"));
        Assert.False(service.IsOpen("file:///w/a.scopes"));
        Assert.Single(service.Diagnostics("file:///w/b.scopes"));
    }
}
