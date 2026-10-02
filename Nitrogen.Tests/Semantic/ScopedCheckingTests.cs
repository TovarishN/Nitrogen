using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.ScopedChecks;
using Nitrogen.Tests.Inferred;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public static class ScopedCheckProbe
{
    [ThreadStatic] public static int Targets;
    [ThreadStatic] public static int Bodies;
    [ThreadStatic] public static int Others;
    public static bool Target(string text) { Targets++; return text != "0"; }
    public static bool Body(string text) { Bodies++; return text != "9"; }
    public static bool Other(string text) { Others++; return text != "0"; }
}

public sealed class ScopedCheckingTests
{
    [Fact]
    public void Selected_lowering_keeps_whole_file_checking_as_the_default()
    {
        ScopedCheckProbe.Targets = ScopedCheckProbe.Bodies = ScopedCheckProbe.Others = 0;
        var language = new LanguageBuilder().Add(ScopedChecksModule.Instance).AddSemantic(new SemanticModule("Test", [], [], [])).Build();
        using var parsed = language.Parse("body 1 other 0", ScopedChecksModule.File);
        var project = new Project(language);
        project.Set("scope.test", parsed.Tree);
        var file = new ProjectSemantics(project)["scope.test"];
        var result = HirLowering.LowerSelected(file, new HashSet<int> { ScopedChecksKinds.Target }, Guid.NewGuid());
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Roots);
        Assert.Equal((1, 1, 1), (ScopedCheckProbe.Targets, ScopedCheckProbe.Bodies, ScopedCheckProbe.Others));
        Assert.Contains(file.Diagnostics(), error => error.Code == "SC0003");
    }

    [Fact]
    public void Scoped_lowering_defers_unrelated_checks_but_final_validation_still_reports_them_once()
    {
        ScopedCheckProbe.Targets = ScopedCheckProbe.Bodies = ScopedCheckProbe.Others = 0;
        var language = new LanguageBuilder().Add(ScopedChecksModule.Instance).AddSemantic(new SemanticModule("Test", [], [], [])).Build();
        using var parsed = language.Parse("body 1 other 0", ScopedChecksModule.File);
        var project = new Project(language);
        project.Set("scope.test", parsed.Tree);
        var file = new ProjectSemantics(project)["scope.test"];
        var result = HirLowering.LowerSelected(file, new HashSet<int> { ScopedChecksKinds.Target }, Guid.NewGuid(),
            SemanticCheckScope.SubtreeAndAncestors);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Roots);
        Assert.Equal((1, 1, 0), (ScopedCheckProbe.Targets, ScopedCheckProbe.Bodies, ScopedCheckProbe.Others));
        int target = result.Roots[0].Origins[0].Node;
        Assert.Empty(file.DiagnosticsForSubtree(target));
        Assert.Equal((1, 1, 0), (ScopedCheckProbe.Targets, ScopedCheckProbe.Bodies, ScopedCheckProbe.Others));
        var diagnostics = file.Diagnostics();
        Assert.Contains(diagnostics, error => error.Code == "SC0003" && error.Span.Start == 13);
        Assert.Contains(diagnostics, error => error.Code == "NT0003" && error.Span.Start == 7);
        Assert.Equal(diagnostics, file.Diagnostics());
        Assert.Equal((1, 1, 1), (ScopedCheckProbe.Targets, ScopedCheckProbe.Bodies, ScopedCheckProbe.Others));
    }

    [Theory]
    [InlineData("body 0 other 1", "SC0001")]
    [InlineData("body 9 other 1", "SC0002")]
    public void Scoped_lowering_keeps_descendant_and_ancestor_diagnostics(string source, string code)
    {
        var language = new LanguageBuilder().Add(ScopedChecksModule.Instance).AddSemantic(new SemanticModule("Test", [], [], [])).Build();
        using var parsed = language.Parse(source, ScopedChecksModule.File);
        var project = new Project(language);
        project.Set("scope.test", parsed.Tree);
        var file = new ProjectSemantics(project)["scope.test"];
        var result = HirLowering.LowerSelected(file, new HashSet<int> { ScopedChecksKinds.Target }, Guid.NewGuid(),
            SemanticCheckScope.SubtreeAndAncestors);
        Assert.Empty(result.Roots);
        Assert.Equal(5, Assert.Single(result.Diagnostics).Origin.Span.Start);
        Assert.Contains(file.Diagnostics(), error => error.Code == code && error.Span.Start == 5);
    }

    [Fact]
    public void Scoped_lowering_keeps_inferred_sequence_errors_on_selected_children()
    {
        var language = new LanguageBuilder().Add(InferredModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", ["Units"], [],
                [InferredSignatures.Scalars, InferredSignatures.Angles, InferredSignatures.NotSequence])).Build();
        using var parsed = language.Parse("items 1,2", InferredModule.File);
        var project = new Project(language);
        project.Set("scope.test", parsed.Tree);
        var result = HirLowering.LowerSelected(new ProjectSemantics(project)["scope.test"],
            new HashSet<int> { InferredKinds.Item }, Guid.NewGuid(), SemanticCheckScope.SubtreeAndAncestors);
        Assert.Single(result.Roots);
        Assert.Equal(8, Assert.Single(result.Diagnostics).Origin.Span.Start);
    }
}
