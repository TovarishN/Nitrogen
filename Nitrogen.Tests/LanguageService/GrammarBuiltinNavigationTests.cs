using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarBuiltinNavigationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("nitrogen-builtins-").FullName;
    const string Grammar = """
        syntax module Controls {
          symbols { component }
          builtin component { button text }
          token Name = ['a'..'z' 'A'..'Z']+;
          syntax Document = Items:Control*;
          syntax Control = Name:Name "{" "}" references? component Name;
        }
        """;
    public GrammarBuiltinNavigationTests()
    {
        File.WriteAllText(Path.Combine(root, "Controls.ngr"), Grammar);
        File.WriteAllText(Path.Combine(root, "nitrogen.json"), """
            {"languages":[{"name":"Controls","extensions":[".nui"],"grammars":["Controls.ngr"],"start":"Controls.Document","tokens":{"component":"class"}}]}
            """);
    }
    string Uri(string file) => new System.Uri(Path.Combine(root, file)).AbsoluteUri;
    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(root);
        service.Open(Uri("main.nui"), 1, "button {} text {} nope {}");
        return service;
    }
    [Fact]
    public void Configured_builtin_controls_receive_default_library_semantic_tokens()
    {
        using var service = Service();
        var tokens = service.SemanticTokens(Uri("main.nui"));
        Assert.Contains(tokens, t => t.Start == new DocumentPosition(0, 0) && t.Length == 6 && t.Type == TokenType.Class && t.Modifiers == TokenModifiers.DefaultLibrary);
        Assert.DoesNotContain(tokens, t => t.Start == new DocumentPosition(0, 18));
    }
    [Fact]
    public void Builtin_definition_navigates_to_its_name_in_a_closed_grammar()
    {
        using var service = Service();
        var target = Assert.Single(service.Definition(Uri("main.nui"), new(0, 2)));
        Assert.Equal(Uri("Controls.ngr"), target.Uri);
        Assert.Equal(new LineMap(Grammar).RangeOf(new(Grammar.IndexOf("button", StringComparison.Ordinal), 6)), target.Range);
    }
    [Fact]
    public void An_unsaved_grammar_moves_the_definition_target()
    {
        using var service = Service();
        string changed = "\n\n" + Grammar;
        service.Open(Uri("Controls.ngr"), 1, Grammar);
        service.Change(Uri("Controls.ngr"), 2, changed);
        var target = Assert.Single(service.Definition(Uri("main.nui"), new(0, 2)));
        Assert.Equal(new LineMap(changed).RangeOf(new(changed.IndexOf("button", StringComparison.Ordinal), 6)), target.Range);
    }
    [Fact]
    public void Authored_components_keep_their_own_definition_when_shadowing_a_builtin()
    {
        File.WriteAllText(Path.Combine(root, "Controls.ngr"), Grammar.Replace(
            "syntax Document = Items:Control*;", "syntax Document = Items:(Declaration / Control)*; syntax Declaration = \"component\" Name:Name \";\" declares component Name export;"));
        using var service = Service();
        service.Change(Uri("main.nui"), 2, "component button; button {}");
        var target = Assert.Single(service.Definition(Uri("main.nui"), new(0, 20)));
        Assert.Equal(Uri("main.nui"), target.Uri);
        Assert.Equal(new DocumentRange(new(0, 10), new(0, 16)), target.Range);
    }
    [Theory]
    [InlineData("first", "First")]
    [InlineData("second", "Second")]
    public void Scoped_builtins_navigate_only_to_the_selected_scope(string keyword, string scope)
    {
        const string scoped = """
            syntax module Controls {
              symbols { component }
              builtin component in First { button }
              builtin component in Second { button }
              token Name = ['a'..'z']+;
              syntax Document = First / Second;
              syntax First = "first" "{" Item:Control "}" scope;
              syntax Second = "second" "{" Item:Control "}" scope;
              syntax Control = Name:Name "{" "}" references component Name;
            }
            """;
        File.WriteAllText(Path.Combine(root, "Controls.ngr"), scoped);
        using var service = Service();
        service.Change(Uri("main.nui"), 2, keyword + " { button {} }");
        var target = Assert.Single(service.Definition(Uri("main.nui"), new(0, keyword.Length + 4)));
        int start = scoped.IndexOf("button", scoped.IndexOf("in " + scope, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(Uri("Controls.ngr"), target.Uri);
        Assert.Equal(new LineMap(scoped).RangeOf(new(start, 6)), target.Range);
    }
    [Fact]
    public void An_unknown_control_has_no_definition()
    {
        using var service = Service();
        Assert.Empty(service.Definition(Uri("main.nui"), new(0, 19)));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
