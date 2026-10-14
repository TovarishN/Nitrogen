using Nitrogen.Grammar;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class PatternHelperValidationTests
{
    const string Grammar = """
        syntax module M {
          syntax Program=E;
          syntax Other="other";
          syntax function F(Value:E):E=match Value {
            | (Left:E, Right:E) <[ $Left + $Right ]> => <[ $Right + $Left ]>;
            | <[ x ]> => <[ x ]>;
            | _ => <[ $Value ]>;
          };
          extensible syntax E { | Atom="x" | Add=E "+" E precedence 10 left | Wrap=X:E "wrap" precedence 30 rewrites F(X) }
        }
        """;

    [Fact]
    public void Every_unused_arm_is_validated_without_running_configured_CSharp()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("match.ngr", Grammar.Replace("| <[ x ]> => <[ x ]>;", "| <[ x ]> => <[ invalid ]>;"));
        workspace.Sources["Helper.cs"] = "public static class Helper { public static Nitrogen.Semantic.SemanticModule Module => throw new System.Exception(\"must not execute\"); }";
        var errors = workspace.ValidateCompilation();
        Assert.Contains(errors, error => error.IsError && error.Code == "NQ0001" && error.Path == "match.ngr" && error.Line == 6);
        Assert.DoesNotContain(errors, error => error.Message.Contains("must not execute"));
    }

    [Fact]
    public void Valid_match_helpers_emit_and_validate_all_metadata()
    {
        var emitted = GrammarCompiler.Compile([new GrammarInput("match.ngr", Grammar, "Demo")]);
        Assert.False(emitted.HasErrors, string.Join("\n", emitted.Diagnostics));
        var workspace = new GrammarWorkspace { CompositionName="M", StartRule="M.Program" };
        workspace.SetGrammar("match.ngr", Grammar);
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        var helper = Assert.Single(snapshot.Language!.Modules.Single(module => module.Name == "M").QuoteHelpers);
        Assert.NotNull(helper.Match);
        Assert.Equal("Value", helper.Match.Selector);
        Assert.Equal(2, helper.Match.Arms.Count);
        Assert.Equal("match.ngr", helper.Match.Arms[0].PatternSource.Path);
    }

    [Theory]
    [InlineData("match Value", "match Missing", "NGR0148")]
    [InlineData("Value:E", "Value:E*", "NGR0148")]
    [InlineData("Left:E, Right:E", "Value:E, Right:E", "NGR0149")]
    [InlineData("Left:E, Right:E", "Left:E, Left:E", "NGR0149")]
    public void Invalid_selector_or_capture_scope_is_static_error(string before, string after, string code)
    {
        var result = GrammarCompiler.Compile([new GrammarInput("match.ngr", Grammar.Replace(before, after), "Demo")]);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Diagnostic.Code == code);
        Assert.Empty(result.Sources);
    }

    [Theory]
    [InlineData("<[ $Left + $Right ]>", "<[ invalid ]>", "NQP0001")]
    [InlineData("Left:E, Right:E", "Unused:E, Left:E, Right:E", "NQP0002")]
    [InlineData("| <[ x ]> => <[ x ]>;", "| <[ invalid ]> => <[ x ]>;", "NQP0001")]
    [InlineData("| <[ x ]> => <[ x ]>;", "| <[ x ]> => <[ $Left ]>;", "NGR0135")]
    public void Composed_validation_checks_every_pattern_and_capture_contract(string before, string after, string code)
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("match.ngr", Grammar.Replace(before, after));
        Assert.Contains(workspace.ValidateCompilation(), error => error.IsError && error.Code == code);
    }

    [Fact]
    public void An_unused_helper_still_reports_an_invalid_pattern()
    {
        var workspace = new GrammarWorkspace();
        string text = Grammar.Replace(" | Wrap=X:E \"wrap\" precedence 30 rewrites F(X)", "")
            .Replace("| <[ x ]> => <[ x ]>;", "| <[ invalid ]> => <[ x ]>;");
        workspace.SetGrammar("unused.ngr", text);
        Assert.Contains(workspace.ValidateCompilation(), error => error.Code == "NQP0001" && error.Path == "unused.ngr");
    }

    [Fact]
    public void Grammar_diagnostic_codes_remain_unique()
    {
        var codes = typeof(GrammarCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!).ToArray();
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }
}
