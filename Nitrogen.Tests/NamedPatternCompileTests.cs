using Nitrogen.Semantic;
using Xunit;
using static Nitrogen.Tests.NamedPatternTestModule;

namespace Nitrogen.Tests;

public sealed class NamedPatternCompileTests
{
    static PatternCompilation Compile(Language language, string text, params (string Name, QuoteHole Hole)[] captures) =>
        language.CompilePattern(Expr, text, captures.ToDictionary(c => c.Name, c => c.Hole));

    static IReadOnlyList<CompositionDiagnostic> CompositionErrors(params NamedPatternSpec[] specs)
    {
        Assert.False(TryCompose(out _, out var diagnostics, specs));
        return diagnostics;
    }

    [Fact]
    public void Pattern_capture_takes_definition_category()
    {
        var language = Compose(Spec("One", Alt("1")));
        using var compiled = Compile(language, "f($x)", ("x", QuoteHole.Pattern("P.One")));
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
    }

    [Fact]
    public void Unknown_pattern_reports_NQP0007()
    {
        var language = Compose(Spec("One", Alt("1")));
        using var compiled = Compile(language, "f($x)", ("x", QuoteHole.Pattern("P.Missing")));
        Assert.False(compiled.Success);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "NQP0007" && d.Hole == "x");
    }

    [Fact]
    public void Direct_non_consuming_cycle_reports_NQP0008()
    {
        var errors = CompositionErrors(Spec("A", Alt("$x", ("x", QuoteHole.Pattern("P.A")))));
        var cycle = Assert.Single(errors, d => d.Code == "NQP0008");
        Assert.Contains("P.A -> P.A", cycle.Message);
    }

    [Fact]
    public void Indirect_cycle_through_descendant_reports_NQP0008()
    {
        var errors = CompositionErrors(
            Spec("A", Alt("$x", ("x", QuoteHole.DescendantOf("P.B")))),
            Spec("B", Alt("$y", ("y", QuoteHole.Pattern("P.A")))));
        var cycle = Assert.Single(errors, d => d.Code == "NQP0008");
        Assert.Contains("P.A -> P.B -> P.A", cycle.Message);
    }

    [Fact]
    public void Consuming_recursion_is_accepted()
    {
        var language = Compose(Spec("Nested", Alt("f($n)", ("n", QuoteHole.Pattern("P.Nested"))), Alt("1")));
        using var compiled = Compile(language, "$x", ("x", QuoteHole.Pattern("P.Nested")));
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
    }

    [Fact]
    public void Repeated_descendant_capture_reports_NQP0009()
    {
        var language = Compose(Spec("One", Alt("1")));
        using var compiled = Compile(language, "f($s, $s)", ("s", QuoteHole.DescendantOf("P.One")));
        Assert.Contains(compiled.Diagnostics, d => d.Code == "NQP0009" && d.Hole == "s");
    }

    [Fact]
    public void Descendant_list_reports_NQP0009()
    {
        var language = Compose(Spec("One", Alt("1")));
        using var compiled = Compile(language, "f(..$s)", ("s", QuoteHole.DescendantOf("P.One") with { IsList = true }));
        Assert.Contains(compiled.Diagnostics, d => d.Code == "NQP0009" && d.Hole == "s");
    }

    [Fact]
    public void Pattern_category_must_fit_hole_site()
    {
        var language = Compose(Spec("One", Alt("1")));
        using var compiled = language.CompilePattern("QuoteLists.Program", "$x;",
            new Dictionary<string, QuoteHole> { ["x"] = QuoteHole.Pattern("P.One") });
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
    }

    [Fact]
    public void Composition_surfaces_named_pattern_errors()
    {
        var errors = CompositionErrors(Spec("A", Alt("$x", ("x", QuoteHole.Pattern("P.A")))));
        var cycle = Assert.Single(errors, d => d.Code == "NQP0008");
        Assert.Equal("patterns.test", cycle.Location?.Path);
    }

    [Fact]
    public void Invalid_alternative_text_fails_composition()
    {
        var errors = CompositionErrors(Spec("Bad", Alt("f(")));
        Assert.Contains(errors, d => d.Code == "NQP0001");
    }
}
