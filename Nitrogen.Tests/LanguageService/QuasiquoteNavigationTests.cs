using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuasiquoteNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-quotes-").FullName;
    const string Grammar = """
        syntax module Calc
        {
          token Number = ['0'..'9']+;
          syntax Program = Value:Expr ";";
          extensible syntax Expr
          {
            | Num = Number
            | Weeks = Amount:Expr "weeks" precedence 30
            | Mul = Left:Expr "*" Right:Expr precedence 20 left
            | Fortnights = Count:Expr "fortnights" precedence 30 rewrites <[ $Count * 2 weeks ]>
            | Doubled = Count:Expr "doubled" precedence 30 rewrites <[ $Count * $Count ]>
          }
        }
        """;

    string Uri(string name = "Calc.ngr") => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    static DocumentPosition At(string text, string needle, int shift = 0) =>
        new LineMap(text).PositionOf(text.IndexOf(needle, StringComparison.Ordinal) + shift);

    NitrogenLanguageService Service(bool configured = true)
    {
        File.WriteAllText(Path.Combine(_root, "Calc.ngr"), Grammar);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["*.ngr"], "start": "Calc.Program" } ] }
            """);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(configured ? _root : Path.GetDirectoryName(_root)!);
        service.Open(Uri(), 1, Grammar);
        Assert.DoesNotContain(service.Diagnostics(Uri()), d => d.Severity == ServiceSeverity.Error);
        return service;
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_splice_navigates_to_its_own_field(bool configured)
    {
        using var service = Service(configured);
        var target = Assert.Single(service.Definition(Uri(), At(Grammar, "$Count", 2)));
        Assert.Equal(Uri(), target.Uri);
        Assert.Equal(At(Grammar, "Count:Expr"), target.Range.Start);
        // The identical label in Doubled is a different field.
        target = Assert.Single(service.Definition(Uri(), At(Grammar, "$Count * $Count", 11)));
        Assert.Equal(At(Grammar, "Count:Expr \"doubled\""), target.Range.Start);
    }

    [Theory]
    [InlineData("weeks ]>", "Weeks =")]
    [InlineData("* 2 weeks", "Mul =")]
    public void A_quote_token_navigates_to_the_rule_that_parsed_it(string token, string declaration)
    {
        using var service = Service();
        var target = Assert.Single(service.Definition(Uri(), At(Grammar, token)));
        Assert.Equal(Uri(), target.Uri);
        Assert.Equal(At(Grammar, declaration), target.Range.Start);
    }

    [Fact]
    public void Quote_navigation_uses_unsaved_grammar_positions()
    {
        using var service = Service();
        string edited = "\n\n" + Grammar;
        service.Change(Uri(), 2, edited);
        Assert.Equal(At(edited, "Weeks ="), Assert.Single(service.Definition(Uri(), At(edited, "weeks ]>"))).Range.Start);
    }

    [Fact]
    public void Splice_completion_and_hover_use_the_enclosing_fields()
    {
        using var service = Service();
        string edited = Grammar.Replace("$Count * 2 weeks", "$Co * 2 weeks", StringComparison.Ordinal);
        service.Change(Uri(), 2, edited);
        var item = Assert.Single(service.Completion(Uri(), At(edited, "$Co", 3)), i => i.Label == "Count");
        Assert.Equal(At(edited, "$Co", 1), item.Replace.Start);
        Assert.Equal(At(edited, "$Co", 3), item.Replace.End);
        Assert.DoesNotContain(service.Completion(Uri(), At(edited, "$Co", 3)), i => i.Label == "Amount");
        service.Change(Uri(), 3, Grammar);
        Assert.Contains("Count", service.Hover(Uri(), At(Grammar, "$Count", 2))!.Markdown);
    }

    [Fact]
    public void Quote_keyword_completion_uses_the_template_category_and_holes()
    {
        using var service = Service();
        string edited = Grammar.Replace("$Count * 2 weeks", "$Count * 2 we", StringComparison.Ordinal);
        service.Change(Uri(), 2, edited);
        var item = Assert.Single(service.Completion(Uri(), At(edited, "we ]>", 2)), i => i.Label == "weeks");
        Assert.Equal(At(edited, "we ]>"), item.Replace.Start);
        Assert.Equal(At(edited, "we ]>", 2), item.Replace.End);
    }

    [Fact]
    public void Find_usages_of_a_splice_stays_in_its_own_rule()
    {
        using var service = Service();
        var uses = service.References(Uri(), At(Grammar, "$Count", 2), true);
        Assert.Equal(2, uses.Count);
        Assert.Contains(uses, l => l.Range.Start == At(Grammar, "Count:Expr"));
        Assert.Contains(uses, l => l.Range.Start == At(Grammar, "$Count", 1));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_DateCalc_quote_navigates_to_Count_and_Weeks(bool configured)
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)), true);
        string path = Path.Combine(_root, "DateCalc.ngr");
        string text = File.ReadAllText(path);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(configured ? _root : Path.GetDirectoryName(_root)!);
        service.Open(Uri("DateCalc.ngr"), 1, text);
        Assert.Equal(At(text, "Count: Expr"), Assert.Single(service.Definition(Uri("DateCalc.ngr"), At(text, "$Count", 2))).Range.Start);
        Assert.Equal(At(text, "Weeks   ="), Assert.Single(service.Definition(Uri("DateCalc.ngr"), At(text, "weeks ]>"))).Range.Start);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_imported_category_and_a_closed_grammar_are_used_for_quote_navigation(bool configured)
    {
        const string basis = """
            syntax module Base
            {
              token Number = ['0'..'9']+;
              syntax Program = Expr ";";
              extensible syntax Expr
              {
                | Num = Number
                | Weeks = Amount:Expr "weeks" precedence 30
                | Mul = Left:Expr "*" Right:Expr precedence 20 left
              }
            }
            """;
        const string extension = """
            syntax module Extra
            {
              using Base;
              extend syntax Expr
              {
                | Fortnights = Count:Expr "fortnights" precedence 30 rewrites <[ $Count * 2 weeks ]>
              }
            }
            """;
        File.WriteAllText(Path.Combine(_root, "Base.ngr"), basis);
        File.WriteAllText(Path.Combine(_root, "Extra.ngr"), extension);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["*.ngr"], "start": "Base.Program" } ] }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(configured ? _root : Path.GetDirectoryName(_root)!);
        service.Open(Uri("Extra.ngr"), 1, extension);
        Assert.DoesNotContain(service.Diagnostics(Uri("Extra.ngr")), d => d.Severity == ServiceSeverity.Error);
        var target = Assert.Single(service.Definition(Uri("Extra.ngr"), At(extension, "weeks ]>")));
        Assert.Equal(Uri("Base.ngr"), target.Uri);
        Assert.Equal(At(basis, "Weeks ="), target.Range.Start);
    }

    [Fact]
    public void An_uncompiled_category_rename_does_not_break_quote_requests()
    {
        using var service = Service();
        string edited = Grammar.Replace("extensible syntax Expr", "extensible syntax Renamed", StringComparison.Ordinal);
        service.Change(Uri(), 2, edited);
        Assert.Empty(service.Definition(Uri(), At(edited, "weeks ]>")));
        Assert.Empty(service.Completion(Uri(), At(edited, "weeks ]>", 2)));
    }

    [Fact]
    public void Dollar_text_in_a_string_is_not_a_splice()
    {
        using var service = Service();
        string edited = Grammar.Replace("| Num = Number", "| Num = Number\n    | Text = \"\\\"$Count\\\"\"", StringComparison.Ordinal)
            .Replace("$Count * 2 weeks", "\"$Count\" * $Count", StringComparison.Ordinal);
        service.Change(Uri(), 2, edited);
        Assert.DoesNotContain(service.Diagnostics(Uri()), d => d.Severity == ServiceSeverity.Error);
        var location = Assert.Single(service.Definition(Uri(), At(edited, "rewrites <[ \"$Count", 15)));
        Assert.Equal(At(edited, "Text ="), location.Range.Start);
        Assert.DoesNotContain(service.Completion(Uri(), At(edited, "rewrites <[ \"$Count", 17)), i => i.Label == "Count");
        var uses = service.References(Uri(), At(edited, "* $Count", 3), false);
        Assert.Single(uses);
    }

    [Fact]
    public void Nested_configuration_supplies_sibling_grammars_and_implicit_extension_scope()
    {
        Directory.CreateDirectory(Path.Combine(_root, "base"));
        Directory.CreateDirectory(Path.Combine(_root, "grammar"));
        const string basis = "syntax module Base { token Number = ['0'..'9']+; syntax Program = Expr \";\"; extensible syntax Expr { | Num = Number | Weeks = Amount:Expr \"weeks\" precedence 30 } }";
        const string extra = "syntax module Extra { extend syntax Base.Expr { | Fortnights = Count:Expr \"fortnights\" precedence 30 rewrites <[ $Count weeks ]> } }";
        File.WriteAllText(Path.Combine(_root, "base", "Base.ngr"), basis);
        File.WriteAllText(Path.Combine(_root, "grammar", "Extra.ngr"), extra);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["base/*.ngr", "grammar/*.ngr"], "start": "Base.Program" } ] }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(Path.GetDirectoryName(_root)!);
        service.Open(Uri("grammar/Extra.ngr"), 1, extra);
        var target = Assert.Single(service.Definition(Uri("grammar/Extra.ngr"), At(extra, "weeks ]>")));
        Assert.Equal(Uri("base/Base.ngr"), target.Uri);
        Assert.Equal(At(basis, "Weeks ="), target.Range.Start);
    }

}
