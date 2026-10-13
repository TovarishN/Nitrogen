using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ExtractionValidationTests
{
    [Fact]
    public void Compile_only_validation_does_not_execute_helper_initializers()
    {
        string marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("a.ngr", "syntax module A { syntax Program = \"x\"; }");
        workspace.Sources["Helper.cs"] = $$"""
            public static class Helper
            {
                public static readonly Nitrogen.Semantic.SemanticModule Module = Init();
                static Nitrogen.Semantic.SemanticModule Init()
                {
                    System.IO.File.WriteAllText({{System.Text.Json.JsonSerializer.Serialize(marker)}}, "executed");
                    return new("Helpers", [], [], []);
                }
            }
            """;
        Assert.DoesNotContain(workspace.ValidateCompilation(), d => d.IsError);
        Assert.False(File.Exists(marker));
        Assert.Null(workspace.Current);
    }
    [Fact]
    public void Compile_only_validation_detects_a_broken_generated_api()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("a.ngr", "syntax module A { syntax Program = \"x\"; }");
        workspace.Sources["Helper.cs"] = "public static class Helper { public static int Kind = Nitrogen.Workspace.Grammar.AKinds.Missing; }";
        Assert.Contains(workspace.ValidateCompilation(), d => d.IsError && d.Code.StartsWith("CS"));
    }
}
