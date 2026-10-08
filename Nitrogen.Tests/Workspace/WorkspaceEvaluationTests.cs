using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A workspace language takes its evaluation profile from its helper sources, bound to its catalog.</summary>
public class WorkspaceEvaluationTests
{
    const string Sum = """
        syntax module Sum
        {
          token Number = ['0'..'9']+;
          syntax Doc = Item:Add;
          syntax Add = "add" Left:Num Right:Num ";" lowers Sum.Add(Left, Right);
          syntax Num = Text:Number lowers literal Core.Scalar Text;
        }
        """;

    const string Semantics = """
        using Nitrogen.Semantic;

        public static class SumSemantics
        {
            public static readonly OperationSignature Add = new("Sum.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
            public static readonly SemanticModule Module = new("Sum", [], [], [Add]);
        }
        """;

    static string Profile(string type, string operation = "Sum.Add") => $$"""
        using Nitrogen.Semantic;
        using Nitrogen.Workspace.Grammar;

        public static class {{type}}
        {
            public static EvaluationProfile Profile { get; } = new(new HashSet<int> { SumKinds.Add },
                catalog => [new ProjectionHandler(catalog.Operations["{{operation}}"],
                    a => new ProjectedValue(SemanticTypes.Scalar, (float)a[0].Value + (float)a[1].Value))],
                _ => null, value => value.Value.ToString()!);
        }
        """;

    static WorkspaceSnapshot Compile(params string[] sources)
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("sum.ngr", Sum);
        workspace.Sources["SumSemantics.cs"] = Semantics;
        for (int i = 0; i < sources.Length; i++) workspace.Sources[$"Profile{i}.cs"] = sources[i];
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    [Fact]
    public void One_profile_is_bound_to_the_language_catalog_and_projects()
    {
        using var snapshot = Compile(Profile("SumProfile"));
        var evaluation = Assert.IsType<BoundEvaluation>(snapshot.Evaluation);
        Assert.Same(snapshot.Language!.SemanticCatalog, evaluation.Registry.Catalog);
        Assert.Empty(snapshot.Diagnostics);

        using var parsed = snapshot.Parse("add 1 2;", "Sum.Doc");
        var project = new Project(snapshot.Language!);
        project.Set("a.sum", parsed.Tree);
        var lowered = HirLowering.LowerSelected(new ProjectSemantics(project)["a.sum"], evaluation.Profile.StatementKinds, Guid.NewGuid());
        var result = HirProjector.Project(Assert.Single(lowered.Roots), evaluation.Registry);
        Assert.Equal("3", evaluation.Profile.Format(result.Value!));
    }

    [Fact]
    public void Without_a_profile_the_language_has_no_evaluation()
    {
        using var snapshot = Compile();
        Assert.Null(snapshot.Evaluation);
    }

    [Fact]
    public void Two_profiles_are_a_warning_and_neither_is_used()
    {
        using var snapshot = Compile(Profile("First"), Profile("Second"));
        Assert.Null(snapshot.Evaluation);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0003", warning.Code);
        Assert.False(warning.IsError);
    }

    [Fact]
    public void A_profile_that_does_not_bind_is_a_warning()
    {
        using var snapshot = Compile(Profile("SumProfile", "Sum.Missing"));
        Assert.Null(snapshot.Evaluation);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0002", warning.Code);
        Assert.False(warning.IsError);
        Assert.Contains("Sum.Missing", warning.Message, StringComparison.Ordinal);
    }

    static string FixesSource(string type) => $$"""
        using Nitrogen.Semantics;

        public static class {{type}}
        {
            public static DiagnosticFixes Fixes { get; } = new(new Dictionary<string, Func<FixRequest, IEnumerable<QuickFix>>>());
        }
        """;

    [Fact]
    public void One_fix_provider_is_discovered()
    {
        using var snapshot = Compile(FixesSource("SumFixes"));
        Assert.NotNull(snapshot.Fixes);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void Without_a_fix_provider_the_language_has_no_fixes()
    {
        using var snapshot = Compile();
        Assert.Null(snapshot.Fixes);
    }

    [Fact]
    public void Two_fix_providers_are_a_warning_and_neither_is_used()
    {
        using var snapshot = Compile(FixesSource("FirstFixes"), FixesSource("SecondFixes"));
        Assert.Null(snapshot.Fixes);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0004", warning.Code);
        Assert.False(warning.IsError);
    }

    static string CallsSource(string type) => $$"""
        using Nitrogen.Semantic;

        public static class {{type}}
        {
            public static CallSignatures Calls { get; } = new(new Dictionary<string, IReadOnlyList<CallSignature>>());
        }
        """;

    [Fact]
    public void One_call_signature_provider_is_discovered()
    {
        using var snapshot = Compile(CallsSource("SumCalls"));
        Assert.NotNull(snapshot.Calls);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void Two_call_signature_providers_are_a_warning_and_neither_is_used()
    {
        using var snapshot = Compile(CallsSource("FirstCalls"), CallsSource("SecondCalls"));
        Assert.Null(snapshot.Calls);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0005", warning.Code);
        Assert.False(warning.IsError);
    }
}
