using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public class OutlineTests
{
    static string Dump(IEnumerable<OutlineSymbol> symbols) =>
        string.Join(" ", symbols.Select(s => s.Children.Count == 0
            ? $"{s.Kind}:{s.Name}"
            : $"{s.Kind}:{s.Name}({Dump(s.Children)})"));

    [Fact]
    public void Declarations_nest_by_their_declaring_nodes()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let x = 1; block { let y = 2; } }\nunit b { }");
        var outline = service.DocumentSymbols("file:///w/a.scopes");
        Assert.Equal("unit:a(value:x value:y) unit:b", Dump(outline));

        var a = outline[0];
        Assert.Equal(OutlineKind.Class, a.Outline);
        Assert.Equal(new DocumentRange(new(0, 0), new(0, 42)), a.Range);          // the whole unit
        Assert.Equal(new DocumentRange(new(0, 5), new(0, 6)), a.SelectionRange);  // its name
        Assert.Equal(new DocumentRange(new(1, 5), new(1, 6)), outline[1].SelectionRange);
    }
}
