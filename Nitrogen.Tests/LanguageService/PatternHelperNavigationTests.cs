using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class PatternHelperNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-pattern-navigation-").FullName;
    const string Grammar = """
        syntax module M {
          syntax Program=E ";";
          token Text="\"" (!"\"" .)* "\"";
          syntax function F(Value:E):E=match Value {
            | (Left:E, Right:E) <[ $Left + $Right ]> => <[ f($Right, $Left, "$Left") ]>;
            | (Left:E) <[ f($Left) ]> => <[ $Left ]>;
            | _ => <[ $Value ]>;
          };
          extensible syntax E { | Atom="x" | String=Text | Add=E "+" E precedence 10 left | Call="f" "(" Args:(E; ",")* ")" | Wrap=X:E "wrap" precedence 30 rewrites F(X) }
        }
        """;
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    static DocumentPosition At(string text, string marker, int shift=0) => new LineMap(text).PositionOf(text.IndexOf(marker, StringComparison.Ordinal)+shift);
    NitrogenLanguageService Service(string text, string name="main.ngr")
    {
        File.WriteAllText(Path.Combine(_root, name), text);
        if (name.EndsWith(".ngr",StringComparison.Ordinal))
            File.WriteAllText(Path.Combine(_root,"nitrogen.json"), "{\"languages\":[{\"name\":\"M\",\"extensions\":[\".m\"],\"start\":\"M.Program\",\"grammars\":[\""+name+"\"]}]}");
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri(name),1,text);
        return service;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selector_arm_captures_categories_and_keywords_navigate_with_original_offsets(bool signed)
    {
        string text = signed ? "// header\n\n#!nitrogen grammar\nlanguage M { start M.Program; extensions \".m\"; }\n" + Grammar : Grammar;
        string name = signed ? "main.syntax" : "main.ngr";
        using var service = Service(text,name);
        Assert.Empty(service.Diagnostics(Uri(name)));
        Assert.Equal(At(text,"Value:E"), Assert.Single(service.Definition(Uri(name),At(text,"match Value",6))).Range.Start);
        Assert.Equal(At(text,"Left:E"), Assert.Single(service.Definition(Uri(name),At(text,"$Left",1))).Range.Start);
        var second = Assert.Single(service.Definition(Uri(name),At(text,"f($Left) ]>",3)));
        Assert.Equal(At(text,"| (Left:E)",3),second.Range.Start);
        Assert.NotEmpty(service.Definition(Uri(name),At(text,"f($Right")));
        Assert.NotEmpty(service.Definition(Uri(name),At(text,"Right:E",6)));
    }

    [Fact]
    public void Rename_updates_one_arm_and_ignores_opaque_dollar_text()
    {
        using var service = Service(Grammar);
        var edits = Assert.Single(service.Rename(Uri("main.ngr"),At(Grammar,"Left:E"),"First")).Value;
        Assert.Equal(3,edits.Count);
        Assert.DoesNotContain(edits,edit => edit.Range.Start==At(Grammar,"\"$Left\"",2));
        Assert.Equal(3,service.References(Uri("main.ngr"),At(Grammar,"Left:E"),true).Count);
        Assert.Throws<RenameRefusedException>(()=>service.Rename(Uri("main.ngr"),At(Grammar,"Left:E"),"Value"));
    }

    [Fact]
    public void Parameter_rename_includes_selector_and_fallback_but_not_arm_locals()
    {
        using var service = Service(Grammar);
        var edits=Assert.Single(service.Rename(Uri("main.ngr"),At(Grammar,"Value:E"),"Input")).Value;
        Assert.Equal(3,edits.Count);
    }

    [Fact]
    public void Arm_quote_completion_exposes_only_its_scope()
    {
        using var service = Service(Grammar);
        var items=service.Completion(Uri("main.ngr"),At(Grammar,"$Left +",3));
        Assert.Contains(items,item=>item.Label=="Left");
        Assert.DoesNotContain(items,item=>item.Label=="Value");
        var results=service.Completion(Uri("main.ngr"),At(Grammar,"f($Right",8));
        Assert.Contains(results,item=>item.Label=="Right");
    }

    [Fact]
    public void Editing_an_unused_arm_refreshes_diagnostics_in_place()
    {
        using var service=Service(Grammar);
        string invalid=Grammar.Replace("<[ f($Left) ]>","<[ invalid ]>");
        service.Change(Uri("main.ngr"),2,invalid);
        Assert.Contains(service.Diagnostics(Uri("main.ngr")),error=>error.Code=="NQP0001");
        service.Change(Uri("main.ngr"),3,Grammar);
        Assert.DoesNotContain(service.Diagnostics(Uri("main.ngr")),error=>error.Code=="NQP0001");
    }


    [Fact]
    public void Selector_and_capture_category_completion_use_typed_scope()
    {
        using var service=Service(Grammar);
        Assert.Contains(service.Completion(Uri("main.ngr"),At(Grammar,"match Value",8)),item=>item.Label=="Value");
        Assert.Contains(service.Completion(Uri("main.ngr"),At(Grammar,"Right:E",7)),item=>item.Label=="E");
        string incomplete=Grammar.Replace("Right:E","Right:");
        service.Change(Uri("main.ngr"),2,incomplete);
        Assert.Contains(service.Completion(Uri("main.ngr"),At(incomplete,"Right:",6)),item=>item.Label=="E");
    }

    [Fact]
    public void Match_helpers_and_subpatterns_cannot_be_extracted_as_inline_return_quotes()
    {
        using var service=Service(Grammar);
        var caret=At(Grammar,"$Left",1);
        var quote=service.PrepareQuoteHelperExtraction(Uri("main.ngr"),new(caret,caret));
        Assert.Null(quote.Id);
        Assert.Contains("match",quote.UnavailableReason!,StringComparison.OrdinalIgnoreCase);
        var helperStart=At(Grammar,"syntax function F");
        var helperEnd=At(Grammar,"extensible syntax");
        var whole=service.PrepareExtraction(Uri("main.ngr"),new(helperStart,helperEnd));
        Assert.NotNull(whole.UnavailableReason);
    }


    [Fact]
    public void Capture_rename_cannot_change_surrounding_parsed_pattern_structure()
    {
        const string grammar="""
            syntax module M {
              syntax Program=E;
              syntax function F(Value:E):E=match Value {
                | (Left:E) <[ f($Left) ]> => <[ $Left ]>;
                | _ => <[ $Value ]>;
              };
              extensible syntax E {
                | Atom="x"
                | One="f" "(" &"$Left" Value:E ")"
                | Two="f" "(" !"$Left" Value:E ")"
              }
            }
            """;
        using var service=Service(grammar);
        Assert.Throws<RenameRefusedException>(()=>service.Rename(Uri("main.ngr"),At(grammar,"Left:E"),"First"));
    }

    public void Dispose()=>Directory.Delete(_root,true);
}
