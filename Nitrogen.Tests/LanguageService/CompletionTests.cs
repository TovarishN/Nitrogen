using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Names visible at the cursor and keywords the parser expects there (issue 238).</summary>
public class CompletionTests
{
    static IReadOnlyList<CompletionItem> Complete(string text, int offset)
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, text);
        return service.Completion("file:///w/a.scopes", new LineMap(text).PositionOf(offset));
    }

    [Fact]
    public void A_typed_prefix_completes_to_visible_names_of_its_kinds()
    {
        const string text = "unit a { let xa = 1; let xb = 2; let y = x; }";
        var items = Complete(text, text.IndexOf("x;", StringComparison.Ordinal) + 1);
        Assert.Equal(new[] { "xa", "xb" }, items.Select(i => i.Label));
        Assert.Equal(CompletionKind.Variable, items[0].Kind);
        Assert.Equal(text.IndexOf("x;", StringComparison.Ordinal), items[0].Replace.Start.Character);
    }

    [Fact]
    public void A_hole_offers_the_kinds_the_grammar_wants_there()
    {
        const string text = "unit a { use ; } unit b { }";
        var items = Complete(text, text.IndexOf(';'));
        Assert.Equal(new[] { "a", "b" }, items.Select(i => i.Label));
        Assert.All(items, i => Assert.Equal(CompletionKind.Class, i.Kind));
    }

    [Fact]
    public void Keywords_come_from_what_the_parser_expects()
    {
        const string text = "unit a { l";
        var items = Complete(text, text.Length);
        Assert.Equal(new[] { "let" }, items.Select(i => i.Label));
        Assert.Equal(CompletionKind.Keyword, items[0].Kind);
    }

    [Fact]
    public void A_dotted_builtin_completes_from_its_whole_prefix()
    {
        const string text = "unit a { let c = sys.cl; }";
        var items = Complete(text, text.IndexOf("cl;", StringComparison.Ordinal) + 2);
        var clock = Assert.Single(items, i => i.Label == "sys.clock");
        Assert.Equal("value (built-in)", clock.Detail);
        Assert.Equal(text.IndexOf("sys", StringComparison.Ordinal), clock.Replace.Start.Character);
    }
}
