using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Commit points (issue 235, spec §4) on grammars small enough to check by hand.</summary>
public class CommitAnalysisTests
{
    internal static CommitAnalysis Analyze(params string[] texts)
    {
        var modules = new List<ModuleDecl>();
        foreach (string text in texts)
        {
            var parsed = GrammarParser.Parse(text);
            Assert.True(parsed.Success, parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].ToString() : "");
            modules.AddRange(parsed.File!.Modules);
        }
        Assert.Empty(GrammarValidator.Validate(modules));
        return new CommitAnalysis(new GrammarAnalysis(modules));
    }

    internal static string Names(CommitAnalysis analysis, string owner) =>
        string.Join(" ", analysis.Sites.Where(s => s.Owner == owner).Select(s => s.Name));

    static RecoverySite Site(CommitAnalysis analysis, string name) => analysis.Sites.Single(s => s.Name == name);

    const string Items = """
        syntax module M
        {
          token Id = ['a'..'z']+;
          syntax File = Items:Item*;
          syntax Item = "item" Name:Id "{" "}";
        }
        """;

    [Fact]
    public void Everything_after_a_pinning_keyword_is_committed()
    {
        var analysis = Analyze(Items);
        Assert.Equal("Item:1 Item:2 Item:3", Names(analysis, "Item"));
        Assert.All(analysis.Sites, s => Assert.Equal(CommitKind.Static, s.Kind));
        Assert.Equal("", Names(analysis, "File"));
    }

    [Fact]
    public void Insert_is_what_follows_and_sync_is_the_element_and_everything_after_it()
    {
        var analysis = Analyze(Items);
        Assert.Equal("{\"{\"}", Site(analysis, "Item:1").Insert.ToString());
        Assert.Equal("{\"item\", $}", Site(analysis, "Item:3").Insert.ToString());
        Assert.Equal("{Id} @1, {\"{\"} @2, {\"}\"} @3", string.Join(", ", Site(analysis, "Item:1").Sync));
    }

    const string Bindings = """
        syntax module M
        {
          token Id = ['a'..'z']+;
          syntax File = Items:(Binding / Item)*;
          syntax Binding = Name:Id "=" Value:Id;
          syntax Item = "item" Name:Id "{" "}";
        }
        """;

    [Fact]
    public void A_prefix_a_later_alternative_can_also_start_is_not_committed()
    {
        var analysis = Analyze(Bindings);
        Assert.Equal("", Names(analysis, "Binding")); // `item` is an Id too: Item may still match
        Assert.Equal("Item:1 Item:2 Item:3", Names(analysis, "Item"));
    }

    [Fact]
    public void A_reserved_word_does_not_overlap_the_token_that_excludes_it()
    {
        var analysis = Analyze(Bindings.Replace("['a'..'z']+;", "['a'..'z']+ except \"item\";"));
        Assert.Equal("Binding:1 Binding:2", Names(analysis, "Binding"));
    }

    [Fact]
    public void Alternatives_sharing_a_prefix_commit_only_in_the_last()
    {
        var analysis = Analyze("""
            syntax module M
            {
              token Id = ['a'..'z']+;
              syntax File = Items:Stmt*;
              syntax Stmt = Assign / Call;
              syntax Assign = "let" Name:Id "=" Value:Id;
              syntax Call = "let" Name:Id "(" ")";
            }
            """);
        Assert.Equal("", Names(analysis, "Assign"));
        Assert.Equal("Call:1 Call:2 Call:3", Names(analysis, "Call"));
    }

    [Fact]
    public void An_optional_group_commits_when_what_follows_it_cannot_start_the_same_way()
    {
        var analysis = Analyze("""
            syntax module M
            {
              token Id = ['a'..'z']+;
              syntax File = Items:Decl*;
              syntax Decl = "decl" Name:Id Init:("=" Id)? ";";
            }
            """);
        // Decl:2 is the optional group itself: nullable, so never a site.
        Assert.Equal("Decl:1 Decl:3 Decl/Init:1", Names(analysis, "Decl"));
        Assert.Equal("{\";\", \"=\"}", Site(analysis, "Decl:1").Insert.ToString());
    }

    [Fact]
    public void The_element_after_a_list_syncs_on_the_list_item_to_re_enter_it()
    {
        var analysis = Analyze("""
            syntax module M
            {
              token Id = ['a'..'z']+;
              syntax File = Items:Block*;
              syntax Block = "{" Stmts:(Id; ";")* "}";
            }
            """);
        Assert.Equal("Block:2", Names(analysis, "Block"));
        var close = Site(analysis, "Block:2");
        Assert.Equal("{\"}\"} @2, {Id} @1", string.Join(", ", close.Sync));
        Assert.Equal("{\"{\", $}", close.Insert.ToString());
    }

    [Fact]
    public void Extension_alternatives_commit_only_as_composed()
    {
        var analysis = Analyze("""
            syntax module M
            {
              token Id = ['a'..'z']+;
              syntax Stmt = "print" Value:Expr ";";
              extensible syntax Expr
              {
                | Num   = Id
                | Paren = "(" Expr ")"
                | Add   = Expr "+" Expr precedence 10
              }
            }
            """);
        Assert.Equal("Stmt:1 Stmt:2", Names(analysis, "Stmt"));
        Assert.Equal("Expr.Paren:1~ Expr.Paren:2~", Names(analysis, "Expr.Paren"));
        Assert.Equal("Expr.Add:2~", Names(analysis, "Expr.Add")); // the operator itself is never a site
        Assert.Equal("", Names(analysis, "Expr.Num"));
    }

    [Fact]
    public void A_postfix_operator_another_alternative_shares_is_left_to_composition()
    {
        var analysis = Analyze("""
            syntax module M
            {
              token Id = ['a'..'z']+;
              syntax Stmt = "print" Value:Expr ";";
              extensible syntax Expr
              {
                | Num    = Id
                | Add    = Expr "+" Expr precedence 10
                | Concat = Expr "+" "+" Expr precedence 5
              }
            }
            """);
        // Both operators start with "+": the runtime's composition check disables both at once
        // (issue 235, Plan 2b), so the analysis commits each alternative on its own.
        Assert.Equal("Expr.Add:2~", Names(analysis, "Expr.Add"));
        Assert.Equal("Expr.Concat:2~ Expr.Concat:3~", Names(analysis, "Expr.Concat"));
    }

    const string Statements = """
        syntax module M
        {
          token Id = ['a'..'z']+;
          syntax File = Items:Stmt*;
          syntax Stmt = "print" Value:Expr ";";
          extensible syntax Expr
          {
            | Num   = Id
            | Paren = "(" Expr ")"
            | Add   = Expr "+" Expr precedence 10
          }
        }
        """;

    [Fact]
    public void A_call_whose_failure_fails_the_parse_carries_both_recovery_bits()
    {
        var analysis = Analyze(Statements);
        // The calls inside Paren and Add depend on their own alternative, so they carry none.
        Assert.Equal("Stmt:1=3", string.Join(" ", analysis.Calls.Select(c => $"{c.Site}={c.Flags}")));
    }

    [Fact]
    public void An_alternative_ends_where_the_points_callers_continue()
    {
        var analysis = Analyze(Statements);
        Assert.Equal("{\")\", \"+\", \";\"}", Site(analysis, "Expr.Paren:2~").Insert.ToString());
    }

    [Fact]
    public void Recovery_sets_list_an_extension_points_known_first_tokens()
    {
        var analysis = Analyze(Statements);
        // The commit queries treat Expr as "anything"; a skip can stop only at something testable.
        Assert.Equal("{\"(\", Id} @1, {\")\"} @2", string.Join(", ", Site(analysis, "Expr.Paren:1~").Sync));
    }

    [Fact]
    public void Keyword_literals_overlap_only_when_equal_and_other_literals_by_prefix()
    {
        var analysis = Analyze(Items);
        var skill = Lookahead.Of(Terminal.Literal("skill"));
        Assert.False(analysis.Overlaps(skill, Lookahead.Of(Terminal.Literal("skills"))));
        Assert.True(analysis.Overlaps(Lookahead.Of(Terminal.Literal(">")), Lookahead.Of(Terminal.Literal(">="))));
        Assert.True(analysis.Overlaps(skill, Lookahead.Of(Terminal.Token("M.Id"))));
        Assert.False(analysis.Overlaps(Lookahead.Of(Terminal.Literal("{")), Lookahead.Of(Terminal.Token("M.Id"))));
        Assert.True(analysis.Overlaps(skill, Lookahead.Of(Terminal.Any)));
        Assert.False(analysis.Overlaps(skill, Lookahead.Of(Terminal.End)));
    }
}
