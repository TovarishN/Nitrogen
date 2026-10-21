using Nitrogen.Binding;
using Nitrogen.Grammar;
using Nitrogen.Ngr;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class MatchDiagnosticTests
{
    const string Grammar = """
        syntax module M {
          token Number = ['0'..'9']+;
          syntax Doc = Value:Num;
          syntax Num = Value:Number lowers literal Core.Scalar Value {
            match <[ 0 ]> => warning MT1001 : "zero is discouraged";
          }
        }
        """;

    static WorkspaceSnapshot Compile(string grammar)
    {
        var workspace = new GrammarWorkspace(builder => builder.AddSemantic(new SemanticModule("Test", [], [], [])))
            { CompositionName = "M", StartRule = "M.Doc" };
        workspace.SetGrammar("match-diagnostic.ngr", grammar);
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    [Fact]
    public void Bootstrap_and_self_hosted_parsers_agree_on_diagnostic_matches()
    {
        var bootstrap = GrammarParser.Parse(Grammar);
        var selfHosted = NgrParser.Parse(Grammar);
        Assert.Empty(bootstrap.Diagnostics);
        Assert.Empty(selfHosted.Diagnostics);
        Assert.Equal(bootstrap.File, selfHosted.File);
    }

    [Fact]
    public void Match_remains_a_valid_semantic_property_name()
    {
        string grammar = Grammar.Replace("match <[", "out match: bool = false; match = true;\n    match <[");
        var bootstrap = GrammarParser.Parse(grammar);
        var selfHosted = NgrParser.Parse(grammar);
        Assert.True(bootstrap.Success);
        Assert.True(selfHosted.Success);
        Assert.Equal(bootstrap.File, selfHosted.File);
    }

    [Theory]
    [InlineData(SemanticCheckScope.WholeFile)]
    [InlineData(SemanticCheckScope.SubtreeAndAncestors)]
    public void Matching_warning_keeps_lowering_admissible(SemanticCheckScope scope)
    {
        using var snapshot = Compile(Grammar);
        using var parsed = snapshot.Parse("0", "M.Doc");
        var project = new Project(snapshot.Language!);
        project.Set("input", parsed.Tree);
        var file = new ProjectSemantics(project)["input"];
        int node = Enumerable.Range(0, parsed.Tree.NodeCount).Single(n =>
            snapshot.Language!.GetKindName(parsed.Tree.Kind(n)) == "Num");
        var lowered = HirLowering.LowerSelected(file, new HashSet<int> { parsed.Tree.Kind(node) }, Guid.NewGuid(), scope);
        Assert.Empty(lowered.Diagnostics);
        Assert.Single(lowered.Roots);
        var warning = Assert.Single(file.Diagnostics());
        Assert.Equal("MT1001", warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void Error_match_blocks_lowering_and_nonmatching_node_is_clean()
    {
        using var snapshot = Compile(Grammar.Replace("=> warning", "=> error"));
        foreach (string text in new[] { "0", "1" })
        {
            using var parsed = snapshot.Parse(text, "M.Doc");
            var project = new Project(snapshot.Language!);
            project.Set("input", parsed.Tree);
            var file = new ProjectSemantics(project)["input"];
            int node = Enumerable.Range(0, parsed.Tree.NodeCount).Single(n =>
                snapshot.Language!.GetKindName(parsed.Tree.Kind(n)) == "Num");
            var lowered = HirLowering.LowerSelected(file, new HashSet<int> { parsed.Tree.Kind(node) }, Guid.NewGuid());
            if (text == "0")
            {
                Assert.Equal(DiagnosticSeverity.Error, Assert.Single(file.Diagnostics()).Severity);
                Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
            }
            else
            {
                Assert.Empty(file.Diagnostics());
                Assert.Empty(lowered.Diagnostics);
            }
        }
    }

    [Theory]
    [InlineData("match <[ invalid ]>", "NQP0001")]
    [InlineData("match (Unused: Num) <[ 0 ]>", "NQP0002")]
    [InlineData("match (X: Number) <[ $X ]>", "NGR0146")]
    [InlineData("match (X: Num, X: Num) <[ $X ]>", "NGR0149")]
    public void Invalid_pattern_is_rejected_during_language_validation(string match, string code)
    {
        var workspace = new GrammarWorkspace { CompositionName = "M", StartRule = "M.Doc" };
        workspace.SetGrammar("match-diagnostic.ngr", Grammar.Replace("match <[ 0 ]>", match));
        Assert.Contains(workspace.ValidateCompilation(), diagnostic => diagnostic.IsError && diagnostic.Code == code && diagnostic.Path == "match-diagnostic.ngr");
    }

    [Fact]
    public void Pattern_validation_never_executes_configured_helper_initializers()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("match-diagnostic.ngr", Grammar.Replace("<[ 0 ]>", "<[ invalid ]>")
            .Replace("\"zero is discouraged\"", "Helper.Message"));
        workspace.Sources["Helper.cs"] = """
            public static class Helper {
              static Helper() { throw new System.Exception("must not execute"); }
              public static string Message => "message";
            }
            """;
        var diagnostics = workspace.ValidateCompilation();
        Assert.Contains(diagnostics, d => d.IsError && d.Code == "NQP0001" && d.Line == 5);
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("must not execute"));
    }

    [Fact]
    public void Rewrite_expansion_preserves_warning_severity()
    {
        const string grammar = """
            syntax module M {
              token Number = ['0'..'9']+;
              syntax Doc = E;
              extensible syntax E {
                | Num = Value:Number lowers literal Core.Scalar Value {
                    match <[ 0 ]> => warning MT1001 : "zero is discouraged";
                  }
                | Wrapped = "wrapped" rewrites <[ 0 ]>
              }
            }
            """;
        using var snapshot = Compile(grammar);
        using var parsed = snapshot.Parse("wrapped", "M.Doc");
        var project = new Project(snapshot.Language!);
        project.Set("input", parsed.Tree);
        var file = new ProjectSemantics(project)["input"];
        var warning = Assert.Single(file.Diagnostics());
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(new TextSpan(0, 7), warning.Span);
        var result = HirLowering.Lower(file, snapshot.Language!.SemanticCatalog, Guid.NewGuid());
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Roots);
    }
}
