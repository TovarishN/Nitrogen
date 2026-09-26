using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Typing in the language service (issue 239): diagnostics, hover, completion, .ngr properties.</summary>
public class LanguageServiceTypingTests
{
    const string Uri = "file:///w/a.skill";

    static NitrogenLanguageService Open(string text, string uri = Uri)
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(uri, 1, text);
        return service;
    }

    static DocumentPosition At(string text, string needle, int delta = 0) =>
        new LineMap(text).PositionOf(text.IndexOf(needle, StringComparison.Ordinal) + delta);

    [Fact]
    public void Type_errors_are_published_with_the_other_diagnostics()
    {
        string text = MotionTypingTests.Skill(MotionTypingTests.Track("true"));
        using var service = Open(text);
        var diagnostic = Assert.Single(service.Diagnostics(Uri));
        Assert.Equal(("MT0001", ServiceSeverity.Error), (diagnostic.Code, diagnostic.Severity));
        Assert.Equal(At(text, "track biped").Line, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void Hover_shows_the_type_of_a_name_and_of_an_expression()
    {
        string text = MotionTypingTests.Skill(MotionTypingTests.Track("gain * 2 + 0.5deg"));
        using var service = Open(text);
        Assert.Contains("`value gain` : float — declared in a.skill", service.Hover(Uri, At(text, "gain * 2"))!.Markdown);
        Assert.Equal("`float angle`", service.Hover(Uri, At(text, "+ 0.5deg"))!.Markdown);

        string timed = MotionTypingTests.Skill(MotionTypingTests.Track("time"));
        using var other = Open(timed);
        Assert.Contains("`value time` : float time — built-in", other.Hover(Uri, At(timed, "time during"))!.Markdown);
    }

    [Fact]
    public void Completion_puts_the_expected_enums_values_first()
    {
        string text = MotionTypingTests.Skill(MotionTypingTests.Track("gain") + "\n    complete when mode == ");
        using var service = Open(text);
        var items = service.Completion(Uri, At(text, "== ", 3));
        // After '==' recovery inserts a Missing expression: the names of the left side's type are offered.
        Assert.Equal(new[] { "mode", "run", "walk" }, items.Where(i => i.Kind != CompletionKind.Keyword).Select(i => i.Label).Order());
        Assert.Equal(items.Select(i => i.SortText).Order(StringComparer.Ordinal), items.Select(i => i.SortText));
    }

    [Fact]
    public void A_nested_argument_name_completes_from_the_called_skill()
    {
        string text = MotionQualifiedTests.Child + MotionQualifiedTests.Parent("argument ga = 2");
        using var service = Open(text);
        Assert.Equal(new[] { "gain" }, service.Completion(Uri, At(text, "ga = 2", 2)).Select(i => i.Label));
    }

    [Fact]
    public void A_grammars_properties_are_declared_outlined_and_found()
    {
        const string uri = "file:///w/m.ngr";
        const string text = "syntax module M\n{\n  token Id = ['a'..'z']+;\n  extensible syntax E\n  {\n    out Type : int = 0;\n    | Num = Id { Type = 1; }\n  }\n}\n";
        using var service = Open(text, uri);
        Assert.Empty(service.Diagnostics(uri));
        var definition = Assert.Single(service.Definition(uri, At(text, "Type = 1")));
        Assert.Equal(At(text, "Type :"), definition.Range.Start);
        var rule = service.DocumentSymbols(uri).Single().Children.Single(c => c.Name == "E");
        Assert.Contains(rule.Children, c => c.Name == "Type" && c.Outline == OutlineKind.Property);
    }

    [Fact]
    public void Hover_shows_a_constant_after_its_type()
    {
        string text = MotionTypingTests.Skill(MotionTypingTests.Track("(1 + 2) * 0.5 + gain"));
        using var service = Open(text);
        Assert.Equal("`float = 1.5`", service.Hover(Uri, At(text, "* 0.5"))!.Markdown);
        Assert.Equal("`float`", service.Hover(Uri, At(text, "+ gain"))!.Markdown);
    }
}
