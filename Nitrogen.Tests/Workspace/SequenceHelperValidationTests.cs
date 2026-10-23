using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SequenceHelperValidationTests
{
    const string Grammar = """
        syntax module M {
          syntax Program = E;
          syntax function Select(V:E):E = match V {
            | (X:E, Y:E) <[ f(..$_, $X, ..$_, $Y, ..$_) ]> => <[ f($X, $Y) ]>;
            | _ => <[ $V ]>;
          };
          extensible syntax E {
            | Atom = "x"
            | Call = "f" "(" Items:(E; ",")* ")"
            | Select = "select" "(" V:E ")" rewrites Select(V)
          }
        }
        """;

    [Fact]
    public void Unused_sequence_helper_patterns_are_validated_through_the_composed_parser()
    {
        var workspace = new GrammarWorkspace { CompositionName = "M", StartRule = "M.Program" };
        workspace.SetGrammar("m.ngr", Grammar);
        Assert.DoesNotContain(workspace.ValidateCompilation(), d => d.IsError);
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("; ", snapshot.Diagnostics));
        Assert.Empty(snapshot.Language!.ValidateQuoteHelpers());
    }

    [Fact]
    public void Reserved_capture_names_have_a_grammar_location()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("m.ngr", Grammar.Replace("(X:E, Y:E)", "(_:E, X:E, Y:E)"));
        Assert.Contains(workspace.ValidateCompilation(), d => d.Code == "NGR0149" && d.Path == "m.ngr");
    }

    [Theory]
    [InlineData("$Missing")]
    [InlineData("..$_")]
    public void Wildcards_do_not_introduce_result_bindings(string result)
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("m.ngr", Grammar.Replace("=> <[ f($X, $Y) ]>", "=> <[ " + result + " ]>"));
        Assert.Contains(workspace.ValidateCompilation(), d => d.IsError);
    }
}
