using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarKeywordNavigationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("nitrogen-keywords-").FullName;
    const string Grammar = """
        syntax module Policy {
          syntax Document = Items:(FlagValue / Present / Emit)*;
          syntax FlagValue = "flagValue" "(" ")";
          syntax Present = "present" "(" ")";
          syntax Emit = "emit" "(" Value:(FlagValue / Present) ")";
        }
        """;
    public GrammarKeywordNavigationTests()
    {
        File.WriteAllText(Path.Combine(root, "Policy.ngr"), Grammar);
        File.WriteAllText(Path.Combine(root, "nitrogen.json"), """
            {"languages":[{"name":"Policy","extensions":[".ncmd"],"grammars":["Policy.ngr"],"start":"Policy.Document"}]}
            """);
    }
    string Uri(string name) => new System.Uri(Path.Combine(root, name)).AbsoluteUri;
    NitrogenLanguageService Service(string source)
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(root);
        service.Open(Uri("Serialize.ncmd"), 1, source);
        return service;
    }
    [Theory]
    [InlineData("flagValue", "FlagValue")]
    [InlineData("present", "Present")]
    [InlineData("emit", "Emit")]
    public void Policy_operation_keywords_navigate_to_their_own_grammar_rule(string keyword, string rule)
    {
        const string source = "flagValue() present() emit(flagValue())";
        using var service = Service(source);
        var target = Assert.Single(service.Definition(Uri("Serialize.ncmd"), new(0, source.IndexOf(keyword, StringComparison.Ordinal) + 2)));
        Assert.Equal(Uri("Policy.ngr"), target.Uri);
        Assert.Equal(new LineMap(Grammar).RangeOf(new(Grammar.IndexOf("syntax " + rule, StringComparison.Ordinal) + 7, rule.Length)), target.Range);
    }
    [Fact]
    public void Nested_operations_resolve_to_the_inner_rule()
    {
        using var service = Service("emit(flagValue())");
        var target = Assert.Single(service.Definition(Uri("Serialize.ncmd"), new(0, 8)));
        Assert.Equal(new LineMap(Grammar).PositionOf(Grammar.IndexOf("syntax FlagValue", StringComparison.Ordinal) + 7), target.Range.Start);
    }
    [Fact]
    public void Punctuation_has_no_definition()
    {
        using var service = Service("flagValue()");
        Assert.Empty(service.Definition(Uri("Serialize.ncmd"), new(0, 9)));
    }
    [Fact]
    public void Identical_keywords_in_different_productions_follow_the_parsed_rule()
    {
        const string grammar = """
            syntax module Policy {
              syntax Document = First / Second;
              syntax First = "first" Value:FirstCall;
              syntax Second = "second" Value:SecondCall;
              syntax FirstCall = "check" "(" ")";
              syntax SecondCall = "check" "(" ")";
            }
            """;
        File.WriteAllText(Path.Combine(root, "Policy.ngr"), grammar);
        using var service = Service("second check()");
        var target = Assert.Single(service.Definition(Uri("Serialize.ncmd"), new(0, 9)));
        Assert.Equal(new LineMap(grammar).PositionOf(grammar.IndexOf("syntax SecondCall", StringComparison.Ordinal) + 7), target.Range.Start);
    }
    [Fact]
    public void An_extension_keyword_targets_the_contributing_alternative()
    {
        const string grammar = """
            syntax module Policy {
              syntax Document = Value:Expr;
              extensible syntax Expr { | True = "true" }
            }
            syntax module Extra {
              extend syntax Policy.Expr { | FlagValue = "flagValue" "(" ")" }
            }
            """;
        File.WriteAllText(Path.Combine(root, "Policy.ngr"), grammar);
        using var service = Service("flagValue()");
        var target = Assert.Single(service.Definition(Uri("Serialize.ncmd"), new(0, 3)));
        Assert.Equal(new LineMap(grammar).PositionOf(grammar.IndexOf("FlagValue =", StringComparison.Ordinal)), target.Range.Start);
    }
    [Fact]
    public void Unsaved_grammar_text_supplies_the_navigation_location()
    {
        using var service = Service("flagValue()");
        string changed = "\n\n" + Grammar;
        service.Open(Uri("Policy.ngr"), 1, changed);
        var target = Assert.Single(service.Definition(Uri("Serialize.ncmd"), new(0, 3)));
        Assert.Equal(new LineMap(changed).PositionOf(changed.IndexOf("syntax FlagValue", StringComparison.Ordinal) + 7), target.Range.Start);
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
