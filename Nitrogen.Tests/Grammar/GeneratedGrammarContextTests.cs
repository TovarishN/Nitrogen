using Xunit;
using C = Nitrogen.Tests.ScopedContext;
namespace Nitrogen.Tests;
public class GeneratedGrammarContextTests
{
    static readonly Language L=new LanguageBuilder().Add(C.FlagsModule.Instance).Add(C.GrammarContextModule.Instance).Build();
    static IEnumerable<int> Nodes(ParseResult p,string name)=>Enumerable.Range(0,p.Tree.NodeCount).Where(n=>L.GetKindName(p.Tree.Kind(n))==name);
    static string Text(ParseResult p,TextSpan span)=>p.Tree.Text.Substring(span.Start,span.Length);
    [Theory][InlineData("a x n x","x")][InlineData("a [ n x ] n x","[ n x ]")]
    public void Authored_scopes_inherit_nest_and_restore_between_items(string source,string expectedBody)
    {
        using var p=L.Parse(source,C.GrammarContextModule.File);Assert.True(p.Success&&!p.HasErrors);
        Assert.Single(Nodes(p,"Enabled"));Assert.Single(Nodes(p,"Disabled"));
        var asyncNode=SyntaxView.Cast<C.AsyncNode>(p.Tree,Assert.Single(Nodes(p,"Async")));
        C.ExprNode body=asyncNode.Body;Assert.InRange(body.Index,0,p.Tree.NodeCount-1);Assert.Equal(expectedBody,Text(p,body.Span));
    }
    [Fact]public void Scoped_list_and_optional_fields_preserve_their_typed_shape_and_absence()
    {
        using var list=L.Parse("l x x ;",C.GrammarContextModule.List);Assert.True(list.Success&&!list.HasErrors);
        var node=SyntaxView.Cast<C.ListNode>(list.Tree,list.Tree.Root);SyntaxList<C.ExprNode> items=node.Items;Assert.Equal(2,items.Count);
        using var present=L.Parse("m x ;",C.GrammarContextModule.Maybe);Assert.True(present.Success&&!present.HasErrors);
        Optional<C.ExprNode> value=SyntaxView.Cast<C.MaybeNode>(present.Tree,present.Tree.Root).Value;Assert.True(value.HasValue);Assert.Equal("x",Text(present,value.Value.Span));
        using var absent=L.Parse("m ;",C.GrammarContextModule.Maybe);Assert.True(absent.Success&&!absent.HasErrors);Assert.False(SyntaxView.Cast<C.MaybeNode>(absent.Tree,absent.Tree.Root).Value.HasValue);
    }
    [Fact]public void Speculative_scope_restores_before_the_consuming_parse_at_the_same_position()
    {
        using var p=L.Parse("x",C.GrammarContextModule.Speculative);Assert.True(p.Success&&!p.HasErrors);
        Assert.Empty(Nodes(p,"Enabled"));Assert.Single(Nodes(p,"Disabled"));
    }
    [Fact]public void A_scoped_alias_retains_the_child_owner_without_a_wrapper()
    {
        using var p=L.Parse("x",C.GrammarContextModule.ScopedAlias);Assert.True(p.Success&&!p.HasErrors);Assert.Equal("Enabled",L.GetKindName(p.Tree.Kind(p.Tree.Root)));
    }
    [Fact]public void Scoped_postfix_operands_keep_the_authored_precedence()
    {
        using var p=L.Parse("n x + x + x",C.GrammarContextModule.File);Assert.True(p.Success&&!p.HasErrors);
        Assert.Equal(2,Nodes(p,"Add").Count());Assert.Equal(2,Nodes(p,"Enabled").Count());Assert.Single(Nodes(p,"Disabled"));
        var outer=SyntaxView.Cast<C.AddExpr>(p.Tree,Nodes(p,"Add").First());C.ExprNode right=outer.Right;Assert.Equal("x",Text(p,right.Span));
    }
    [Fact]public void Generated_incremental_header_edits_match_full_parse_without_stale_context()
    {
        const string old="a x n x",next="n x n x";using var first=L.ParseIncremental(old,C.GrammarContextModule.File);
        using var second=L.Reparse(first,next,[new(0,1,1)]);using var full=L.Parse(next,C.GrammarContextModule.File);TreeAssert.Same(full,second);
        Assert.Empty(Nodes(second,"Enabled"));Assert.Equal(2,Nodes(second,"Disabled").Count());Assert.True(second.Stats.ReusedNodes>0);
        using var third=L.Reparse(second,old,[new(0,1,1)]);using var restored=L.Parse(old,C.GrammarContextModule.File);TreeAssert.Same(restored,third);
    }
    [Fact]public void Generated_strict_recovery_and_parallel_calls_have_no_context_leaks()
    {
        using var strict=L.Parse("a x n x",C.GrammarContextModule.File);using var recovered=L.ParseRecovering("a x n x",C.GrammarContextModule.File);TreeAssert.Same(strict,recovered);
        Parallel.For(0,16,i=>{using var p=L.Parse(i%2==0?"a x":"n x",C.GrammarContextModule.File);Assert.True(p.Success&&!p.HasErrors);});
    }
    [Fact]public void Generated_references_require_the_selected_flag_owner_even_when_the_entry_does_not_use_it()
    {
        Assert.Throws<LanguageCompositionException>(()=>new LanguageBuilder().Add(C.GrammarContextModule.Instance).Build());
        using var p=L.Parse("z",C.GrammarContextModule.External);Assert.True(p.Success&&!p.HasErrors);
    }
}
