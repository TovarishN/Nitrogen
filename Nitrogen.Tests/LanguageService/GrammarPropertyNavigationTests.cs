using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarPropertyNavigationTests
{
    [Theory]
    [InlineData("Type")]
    [InlineData("Source")]
    public void DateCalc_reference_lowering_properties_navigate_to_their_declarations(string name)
    {
        string grammar = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", "DateCalc.ngr"));
        const string uri = "file:///navigation/DateCalc.ngr";
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(uri, 1, grammar);
        var lines = new LineMap(grammar);
        int clause = grammar.IndexOf("lowers reference type Type initializer Source", StringComparison.Ordinal);
        int offset = grammar.IndexOf(name, clause, StringComparison.Ordinal);
        var position = lines.PositionOf(offset);
        var target = Assert.Single(service.Definition(uri, position));
        int declaration = grammar.IndexOf(" " + name + " :", grammar.IndexOf("extensible syntax Expr", StringComparison.Ordinal), StringComparison.Ordinal) + 1;
        Assert.Equal(new DocumentLocation(uri, lines.RangeOf(new TextSpan(declaration, name.Length))), target);
        Assert.Contains(service.References(uri, target.Range.Start, includeDeclaration: false),
            location => location.Range.Start == position);
        Assert.Contains(service.Highlights(uri, position), highlight => highlight.Range.Start == target.Range.Start);
    }
}
