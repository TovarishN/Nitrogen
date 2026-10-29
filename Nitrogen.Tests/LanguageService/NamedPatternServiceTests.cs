using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class NamedPatternServiceTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-named-pattern-service-").FullName;
    const string Grammar = """
        syntax module M {
          syntax Program=E ";";
          extensible syntax E {
            | Atom="x"
            | Call="f" "(" Args:(E; ",")* ")" {
                match (s: ..Lit) <[ f(..$_, $s, ..$_) ]> => warning MP1001 : "m";
              }
          }
          pattern Lit : E = <[ x ]>;
          syntax function F(Value:E):E=match Value {
            | (s: M.Lit) <[ f($s) ]> => <[ $s ]>;
            | _ => <[ $Value ]>;
          };
        }
        """;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    static DocumentPosition At(string text, string marker, int shift = 0) => new LineMap(text).PositionOf(text.IndexOf(marker, StringComparison.Ordinal) + shift);

    NitrogenLanguageService Service(string text)
    {
        File.WriteAllText(Path.Combine(_root, "main.ngr"), text);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), "{\"languages\":[{\"name\":\"M\",\"extensions\":[\".m\"],\"start\":\"M.Program\",\"grammars\":[\"main.ngr\"]}]}");
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("main.ngr"), 1, text);
        return service;
    }

    [Fact]
    public void Grammar_is_clean()
    {
        using var service = Service(Grammar);
        Assert.Empty(service.Diagnostics(Uri("main.ngr")));
    }

    [Theory]
    [InlineData("..Lit", 2)]
    [InlineData("M.Lit", 2)]
    public void Capture_category_navigates_to_pattern_declaration(string reference, int shift)
    {
        using var service = Service(Grammar);
        var target = Assert.Single(service.Definition(Uri("main.ngr"), At(Grammar, reference, shift)));
        Assert.Equal(At(Grammar, "Lit : E"), target.Range.Start);
    }

    [Fact]
    public void References_list_declaration_and_both_uses()
    {
        using var service = Service(Grammar);
        Assert.Equal(3, service.References(Uri("main.ngr"), At(Grammar, "Lit : E"), true).Count);
    }

    [Fact]
    public void Rename_updates_declaration_and_uses_keeping_qualifiers()
    {
        using var service = Service(Grammar);
        Assert.Equal(At(Grammar, "Lit : E"), service.PrepareRename(Uri("main.ngr"), At(Grammar, "Lit : E")).Start);
        var edits = Assert.Single(service.Rename(Uri("main.ngr"), At(Grammar, "..Lit", 2), "Literal")).Value;
        Assert.Equal(3, edits.Count);
        Assert.Contains(edits, edit => edit.Range.Start == At(Grammar, "M.Lit", 2));
        Assert.All(edits, edit => Assert.Equal("Literal", edit.NewText));
    }

    [Fact]
    public void Hover_shows_pattern_signature()
    {
        using var service = Service(Grammar);
        var hover = service.Hover(Uri("main.ngr"), At(Grammar, "..Lit", 3));
        Assert.NotNull(hover);
        Assert.Contains("pattern M.Lit : M.E", hover!.Markdown);
        Assert.Contains("1 alternative", hover.Markdown);
    }

    [Fact]
    public void New_keywords_are_highlighted_only_in_their_positions()
    {
        string text = Grammar.Replace("<[ f(..$_, $s, ..$_) ]>", "<[ f(..$_, $s, ..$_) ]> inside <[ f(..$_) ]>")
            .Replace("syntax Program=E", "syntax Program=pattern:E");
        using var service = Service(text);
        Assert.Empty(service.Diagnostics(Uri("main.ngr")));
        var keywords = service.SemanticTokens(Uri("main.ngr")).Where(t => t.Type == TokenType.Keyword).Select(t => t.Start).ToHashSet();
        Assert.Contains(At(text, "pattern Lit"), keywords);
        Assert.Contains(At(text, "inside <["), keywords);
        Assert.DoesNotContain(At(text, "pattern:E"), keywords);
    }

    [Fact]
    public void Arm_capture_category_completion_offers_patterns()
    {
        string text = Grammar.Replace("(s: M.Lit)", "(s: Li)");
        using var service = Service(text);
        Assert.Contains(service.Completion(Uri("main.ngr"), At(text, "Li)", 2)), item => item.Label == "Lit" && item.Detail == "named pattern");
    }

    [Fact]
    public void Completion_after_descendant_marker_offers_patterns()
    {
        string text = Grammar.Replace("(s: ..Lit)", "(s: ..L)");
        using var service = Service(text);
        Assert.Contains(service.Completion(Uri("main.ngr"), At(text, "..L)", 3)), item => item.Label == "Lit");
    }
}
