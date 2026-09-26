using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Rename across the project, and every refusal (issue 238).</summary>
public sealed class RenameTests : IDisposable
{
    const string A = NavigationTests.A;
    const string B = "unit b { use a; }";

    readonly NitrogenLanguageService _service = new(LanguageServiceTests.ScopesRegistry());

    public RenameTests()
    {
        _service.Open(NavigationTests.UriA, 1, A);
        _service.Open(NavigationTests.UriB, 1, B);
    }

    static DocumentPosition At(string needle, int skip = 0) => NavigationTests.At(needle, skip);

    /// <summary>Applies edits to a text, last first, so earlier offsets stay valid.</summary>
    internal static string Apply(string text, IEnumerable<DocumentEdit> edits)
    {
        var map = new LineMap(text);
        foreach (var edit in edits.OrderByDescending(e => map.OffsetOf(e.Range.Start)))
            text = text[..map.OffsetOf(edit.Range.Start)] + edit.NewText + text[map.OffsetOf(edit.Range.End)..];
        return text;
    }

    [Fact]
    public void Renaming_the_inner_x_touches_its_declaration_and_references_only()
    {
        var edits = _service.Rename(NavigationTests.UriA, At("x + x"), "w");
        Assert.Equal(new[] { NavigationTests.UriA }, edits.Keys);
        Assert.Equal("unit a { let x = 1; block { let w = 2; let y = w + w; } let z = x; let p = pi; }",
            Apply(A, edits[NavigationTests.UriA]));
    }

    [Fact]
    public void Renaming_a_unit_edits_every_document_that_uses_it()
    {
        var edits = _service.Rename(NavigationTests.UriB, new(0, 13), "alpha");
        Assert.Equal("unit b { use alpha; }", Apply(B, edits[NavigationTests.UriB]));
        string renamed = Apply(A, edits[NavigationTests.UriA]);
        Assert.StartsWith("unit alpha {", renamed);

        _service.Change(NavigationTests.UriA, 2, renamed);
        _service.Change(NavigationTests.UriB, 2, Apply(B, edits[NavigationTests.UriB]));
        Assert.Empty(_service.Diagnostics(NavigationTests.UriA));
        Assert.Empty(_service.Diagnostics(NavigationTests.UriB));
    }

    [Fact]
    public void Prepare_gives_the_name_under_the_cursor()
    {
        Assert.Equal(new DocumentRange(At("x = 2"), At("x = 2") with { Character = At("x = 2").Character + 1 }),
            _service.PrepareRename(NavigationTests.UriA, At("x = 2")));
    }

    [Theory]
    [InlineData("pi", 0, "p2", "built in")]
    [InlineData("{", 0, "q", "Nothing to rename")]
    [InlineData("x = 2", 0, "let", "not a valid value name")]
    [InlineData("x = 2", 0, "y", "already names a value")]
    [InlineData("x = 1", 0, "z", "already names a value")]
    [InlineData("x = 1", 0, "", "not a valid value name")]
    public void Refusals_say_why(string needle, int skip, string newName, string reason)
    {
        var refusal = Assert.Throws<RenameRefusedException>(() => _service.Rename(NavigationTests.UriA, At(needle, skip), newName));
        Assert.Contains(reason, refusal.Message);
    }

    [Fact]
    public void An_unresolved_name_cannot_be_renamed()
    {
        _service.Change(NavigationTests.UriA, 2, "unit a { let y = q; }");
        var refusal = Assert.Throws<RenameRefusedException>(() => _service.PrepareRename(NavigationTests.UriA, new(0, 17)));
        Assert.Contains("not declared", refusal.Message);
    }

    public void Dispose() => _service.Dispose();
}
