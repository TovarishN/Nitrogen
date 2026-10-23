using Nitrogen.Binding;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SemanticsCheckCaptureTests
{
    [Fact]
    public void Ordinary_checks_preserve_condition_location_message_order()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var input = language.ParseTemplate("1", "QuoteLists.Expr", new Dictionary<string, string>());
        var project = new Project(language);
        project.Set("input", input.Tree);
        using var semantics = new ProjectSemantics(project);
        var calls = new List<string>();
        var check = new SemanticsCheck("C1", (_, _) => { calls.Add("condition"); return false; },
            (_, _) => { calls.Add("message"); return "failed"; },
            at: (_, _) => { calls.Add("span"); return new(0, 1); });
        Assert.Equal("failed", check.Evaluate(semantics["input"], input.Tree.Root)!.Message);
        Assert.Equal(new[] { "condition", "span", "message" }, calls);
    }

    [Fact]
    public void Capture_aware_checks_keep_public_message_api_and_borrowed_readonly_views()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var input = language.ParseTemplate("f(1, 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        var project = new Project(language);
        project.Set("input", input.Tree);
        using var semantics = new ProjectSemantics(project);
        var file = semantics["input"];
        var pattern = new SyntaxDiagnosticPattern("QuoteLists.Expr", "f(..$items)",
            new Dictionary<string, QuoteHole> { ["items"] = QuoteHole.List("QuoteLists.Expr") }, new("test", 0, 0));
        int messages = 0;
        var check = new SemanticsCheck("C1", pattern, (s, _, captures) =>
        {
            messages++;
            var items = captures.List<SemanticNode>("items");
            Assert.Same(s, items[0].Semantics);
            Assert.Throws<NotSupportedException>(() => ((IList<SemanticNode>)items).Clear());
            return string.Join("|", items.Select(i => i.Text));
        }, DiagnosticSeverity.Warning);
        Assert.Equal("1|2", check.Evaluate(file, input.Tree.Root)!.Message);
        Assert.Equal(1, messages);
        Assert.False(check.Holds(file, input.Tree.Root));
        Assert.Equal("1|2", check.Message(file, input.Tree.Root));
        Assert.Equal(2, messages);
    }
}
