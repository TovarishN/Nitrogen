using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Definition, references, highlights and hover on the Scopes test language (issue 238).</summary>
public sealed class NavigationTests : IDisposable
{
    internal const string A = "unit a { let x = 1; block { let x = 2; let y = x + x; } let z = x; let p = pi; }";
    internal const string UriA = "file:///w/a.scopes";
    internal const string UriB = "file:///w/b.scopes";

    readonly NitrogenLanguageService _service = new(LanguageServiceTests.ScopesRegistry());

    public NavigationTests()
    {
        _service.Open(UriA, 1, A);
        _service.Open(UriB, 1, "unit b { use a; }");
    }

    /// <summary>The position of the <paramref name="skip"/>-th occurrence (from 0) of <paramref name="needle"/> in <see cref="A"/>.</summary>
    internal static DocumentPosition At(string needle, int skip = 0)
    {
        int index = -1;
        for (int i = 0; i <= skip; i++) index = A.IndexOf(needle, index + 1, StringComparison.Ordinal);
        return new DocumentPosition(0, index);
    }

    static DocumentRange Name(DocumentPosition start, int length) => new(start, start with { Character = start.Character + length });

    [Fact]
    public void Definition_of_a_reference_a_declaration_and_a_builtin()
    {
        var inner = Assert.Single(_service.Definition(UriA, At("x + x")));
        Assert.Equal(new DocumentLocation(UriA, Name(At("x = 2"), 1)), inner);
        Assert.Equal(inner, Assert.Single(_service.Definition(UriA, At("x = 2"))));
        Assert.Empty(_service.Definition(UriA, At("pi")));      // built-ins have no location
        Assert.Empty(_service.Definition(UriA, At("{")));
        Assert.Equal(new DocumentLocation(UriA, Name(At("a"), 1)), Assert.Single(_service.Definition(UriB, new(0, 13))));
    }

    [Fact]
    public void References_with_and_without_the_declaration()
    {
        var withDeclaration = _service.References(UriA, At("x = 2"), includeDeclaration: true);
        Assert.Equal(new[] { At("x = 2"), At("x", 2), At("x", 3) }, withDeclaration.Select(l => l.Range.Start));
        Assert.Equal(2, _service.References(UriA, At("x = 2"), includeDeclaration: false).Count);

        var unit = _service.References(UriA, At("a"), includeDeclaration: true);
        Assert.Equal(new[] { UriA, UriB }, unit.Select(l => l.Uri));
    }

    [Fact]
    public void Highlights_mark_the_declaration_as_written()
    {
        var highlights = _service.Highlights(UriA, At("x + x"));
        Assert.Equal(new[] { HighlightKind.Write, HighlightKind.Read, HighlightKind.Read }, highlights.Select(h => h.Kind));
        Assert.Equal(At("x = 2"), highlights[0].Range.Start);
    }

    [Fact]
    public void Hover_says_what_a_name_is()
    {
        var reference = _service.Hover(UriA, At("x + x"))!;
        Assert.Equal("`value x` — declared in a.scopes line 1", reference.Markdown);
        Assert.Equal(Name(At("x + x"), 1), reference.Range);
        Assert.Equal("`value pi` — built-in", _service.Hover(UriA, At("pi"))!.Markdown);
        Assert.Null(_service.Hover(UriA, At("let")));
    }

    public void Dispose() => _service.Dispose();
}
