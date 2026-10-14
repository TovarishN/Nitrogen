using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class WorkspaceQuoteHelperValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unused_malformed_helper_is_an_error(bool load)
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("m.ngr", "syntax module M { syntax E=\"x\"; syntax function F():E=<[ not_an_E ]>; }");
        var errors = load ? workspace.Compile().Diagnostics : workspace.ValidateCompilation();
        Assert.Contains(errors, error => error.IsError && error.Code == "NQ0001" && error.Path == "m.ngr");
    }

    [Fact]
    public void Validation_checks_quotes_without_executing_configured_CSharp()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("m.ngr", "syntax module M { syntax E=\"x\"; syntax function F():E=<[ bad ]>; }");
        workspace.Sources["Helper.cs"] = "public static class Helper { public static Nitrogen.Semantic.SemanticModule Module => throw new System.Exception(\"must not execute\"); }";
        var diagnostics = workspace.ValidateCompilation();
        Assert.Contains(diagnostics, error => error.Code == "NQ0001");
        Assert.DoesNotContain(diagnostics, error => error.Message.Contains("must not execute"));
    }

    [Fact]
    public void An_unused_declaration_producing_helper_is_an_error()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("m.ngr", "syntax module M { symbols { value } token Id=['a'..'z']+; syntax D=\"let\" Name:Id declares value Name; syntax function F():D=<[ let x ]>; }");
        Assert.Contains(workspace.ValidateCompilation(), error => error.IsError && error.Code == "NQ0006");
    }
}
