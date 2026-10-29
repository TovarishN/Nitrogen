using Xunit;
namespace Nitrogen.Tests;
// Generic synthetic grammar, NOT a C# parser. Marker a/n controls the same payload x.
public unsafe class GrammarContextTests
{
    sealed class ProbeModule : SyntaxModule, IGrammarContextModule
    {
        internal static readonly ProbeModule Instance = new();
        internal readonly GrammarContextDecl Flag;
        internal readonly GrammarContextDecl Other;
        public IReadOnlyList<GrammarContextDecl> ContextReferences => [];
        public IReadOnlyList<GrammarContextDecl> ContextDeclarations => [Flag,Other];
        internal readonly ExtensionPointDecl Expr;
        internal readonly ExtensionPointDecl EnabledOnly;
        ProbeModule() : base("GrammarContextRuntime") { Flag=new(this,"Flag");Other=new(this,"Other");Expr = new(this,"Expr"); EnabledOnly = new(this,"EnabledOnly"); }
        public override string GetKindName(int kind) => kind switch {1=>"File",2=>"Disabled",3=>"Enabled",4=>"Group",_=>"Other"};
        public override void Register(ExtensionRegistry r)
        {
            r.Declare(Expr);r.Declare(EnabledOnly);
            r.AddPrefix(Expr,new("Disabled",KindBase|2,0,&Disabled));
            r.AddPrefix(Expr,new("Enabled",KindBase|3,0,&Enabled));
            r.AddPrefix(Expr,new("Group",KindBase|4,0,&Group));
            r.AddPrefix(EnabledOnly,new("Enabled",KindBase|3,0,&Enabled));
        }
    }
    static readonly Language L = new LanguageBuilder().Add(ProbeModule.Instance).Build();
    static readonly Rule Start = new("GrammarContextRuntime.File", &File);
    static bool Expr(ref ParserState s) => s.ParseExtensible(ProbeModule.Instance.Expr,0);
    static bool Only(ref ParserState s) => s.ParseExtensible(ProbeModule.Instance.EnabledOnly,0);
    static bool Disabled(ref ParserState s) => !s.TestContext(ProbeModule.Instance.Flag) && s.MatchLiteral("x");
    static bool Enabled(ref ParserState s) => s.TestContext(ProbeModule.Instance.Flag) && s.MatchLiteral("x");
    static bool Group(ref ParserState s)
    {
        if(!s.MatchLiteral("["))return false;bool enabled=s.Current=='a';
        return s.MatchLiteral(enabled?"a":"n") && s.ParseWithContext(ProbeModule.Instance.Flag,enabled,&Expr) && s.MatchLiteral("]");
    }
    static bool File(ref ParserState s)
    {
        var mark=s.Mark();s.Open(ProbeModule.Instance.KindBase|1);
        while(!s.AtEnd)
        {
            bool enabled=s.Current=='a';
            if(!s.MatchLiteral(enabled?"a":"n") || !s.ParseWithContext(ProbeModule.Instance.Flag,enabled,&Expr)){s.Reset(mark);return false;}
        }
        s.Close();return true;
    }
    static ParserState State(string text,BuildArena arena) => new(text,arena,new MemoTable(),&StandardTrivia.WhitespaceAndComments){Language=L};
    [Fact] public void Memo_separates_success_at_same_position()
    {
        var a=new BuildArena();var s=State("x",a);var mark=s.Mark();
        Assert.True(s.ParseWithContext(ProbeModule.Instance.Flag,false,&Expr));Assert.Equal(ProbeModule.Instance.KindBase|2,a.Kind[a.ChildStack[0]]);
        s.Reset(mark);Assert.True(s.ParseWithContext(ProbeModule.Instance.Flag,true,&Expr));Assert.Equal(ProbeModule.Instance.KindBase|3,a.Kind[a.ChildStack[0]]);
        Assert.Equal(0UL,s.GrammarContext);
    }
    [Fact] public void Memo_separates_failure_at_same_position()
    {
        var a=new BuildArena();var s=State("x",a);var mark=s.Mark();
        Assert.False(s.ParseWithContext(ProbeModule.Instance.Flag,false,&Only));s.Reset(mark);Assert.True(s.ParseWithContext(ProbeModule.Instance.Flag,true,&Only));
    }
    [Theory][InlineData("nxax","axax")][InlineData("axnx","nxnx")]
    public void Header_only_edit_matches_full_parse_and_reuses_unaffected_context(string old,string next)
    {
        using var first=L.ParseIncremental(old,Start);Assert.True(first.Success&&!first.HasErrors);
        using var second=L.Reparse(first,next,[new(0,1,1)]);using var full=L.Parse(next,Start);
        Assert.Equal(Shape(full),Shape(second));Assert.True(second.Stats.ReusedNodes>0);
        using var third=L.Reparse(second,old,[new(0,1,1)]);using var restored=L.Parse(old,Start);Assert.Equal(Shape(restored),Shape(third));
    }
    [Fact] public void Moved_graft_carries_descendant_context_across_multiple_reparses()
    {
        using var first=L.ParseIncremental("n[ax]nx",Start);
        using var moved=L.Reparse(first,"nxn[ax]nx",[new(0,0,2)]);using var full=L.Parse("nxn[ax]nx",Start);Assert.Equal(Shape(full),Shape(moved));Assert.True(moved.Stats.ReusedNodes>0);
        using var changed=L.Reparse(moved,"nxn[nx]nx",[new(4,1,1)]);using var expected=L.Parse("nxn[nx]nx",Start);Assert.Equal(Shape(expected),Shape(changed));
    }
    [Fact] public void Memo_growth_retains_all_context_bits_and_default_entries()
    {
        var memo=new MemoTable();
        for(int i=0;i<1200;i++){memo.Set(7,i,10,20,0,0);memo.Set(7,i,30,40,0,1UL<<63);}
        for(int i=0;i<1200;i++)
        {
            Assert.True(memo.TryGet(7,i,out int a,out _,out _,0));Assert.Equal(10,a);
            Assert.True(memo.TryGet(7,i,out int b,out _,out _,1UL<<63));Assert.Equal(30,b);
            Assert.False(memo.TryGet(7,i,out _,out _,out _,1));
        }
    }
    sealed class FlagsModule : SyntaxModule, IGrammarContextModule
    {
        readonly GrammarContextDecl[] _flags;
        readonly GrammarContextDecl[] _references;
        public FlagsModule(string name,int count,GrammarContextDecl[]? references=null):base(name)
        {_flags=Enumerable.Range(0,count).Select(i=>new GrammarContextDecl(this,"F"+i.ToString("D2"))).ToArray();_references=references??[];}
        public IReadOnlyList<GrammarContextDecl> ContextDeclarations=>_flags;
        public IReadOnlyList<GrammarContextDecl> ContextReferences=>_references;
        public override string GetKindName(int k)=>"unused";
        public override void Register(ExtensionRegistry r){}
    }
    [Fact]public void Sixty_four_flags_have_distinct_masks_and_the_sixty_fifth_fails_composition()
    {
        var module=new FlagsModule("Flags64",64);var language=new LanguageBuilder().Add(module).Build();
        Assert.Equal(64,module.ContextDeclarations.Select(language.ContextMask).Distinct().Count());
        Assert.Contains(1UL<<63,module.ContextDeclarations.Select(language.ContextMask));
        var error=Assert.Throws<LanguageCompositionException>(()=>new LanguageBuilder().Add(new FlagsModule("Flags65",65)).Build());Assert.Contains("64",error.Message);
    }
    [Fact]public void A_missing_flag_owner_fails_before_any_parse()
    {
        var owner=new FlagsModule("Owner",1);var consumer=new FlagsModule("Consumer",0,[owner.ContextDeclarations[0]]);
        Assert.Throws<LanguageCompositionException>(()=>new LanguageBuilder().Add(consumer).Build());
        new LanguageBuilder().Add(consumer).Add(owner).Build();
    }
    [Fact]public void Duplicate_qualified_flag_names_fail_composition()
    {Assert.Throws<LanguageCompositionException>(()=>new LanguageBuilder().Add(new FlagsModule("Same",1)).Add(new FlagsModule("Same",1)).Build());}
    [Fact]public void Context_masks_do_not_depend_on_selected_module_registration_order()
    {
        var a=new FlagsModule("A",1);var b=new FlagsModule("B",1);
        var ab=new LanguageBuilder().Add(a).Add(b).Build();var ba=new LanguageBuilder().Add(b).Add(a).Build();
        Assert.Equal(ab.ContextMask(a.ContextDeclarations[0]),ba.ContextMask(a.ContextDeclarations[0]));
        Assert.Equal(ab.ContextMask(b.ContextDeclarations[0]),ba.ContextMask(b.ContextDeclarations[0]));
    }
    [Fact]public void Strict_and_recovery_passes_agree_without_context_leaks()
    {
        using var strict=L.Parse("axnx",Start);using var recovering=L.ParseRecovering("axnx",Start);
        Assert.False(recovering.HasErrors);Assert.Equal(Shape(strict),Shape(recovering));
        using var invalid=L.Parse("n[ax",Start);Assert.True(invalid.HasErrors);
        using var after=L.Parse("nx",Start);Assert.True(after.Success&&!after.HasErrors);
    }
    static string Shape(ParseResult p) => string.Join("|",Enumerable.Range(0,p.Tree.NodeCount).Select(i=>$"{p.Tree.Kind(i)}:{p.Tree.Span(i)}:{p.Tree.Parent(i)}"));
    [Fact] public void Nested_override_restores_caller_after_success_failure_and_exception()
    {
        var s=State("x",new());Assert.True(s.ParseWithContext(ProbeModule.Instance.Flag,true,&Nested));Assert.Equal(0UL,s.GrammarContext);
        try{s.ParseWithContext(ProbeModule.Instance.Flag,true,&Throw);Assert.Fail("expected exception");}catch(InvalidOperationException){}Assert.Equal(0UL,s.GrammarContext);
    }
    static bool Nested(ref ParserState s)
    {
        Assert.Equal(1UL,s.GrammarContext);Assert.False(s.ParseWithContext(ProbeModule.Instance.Flag,false,&False));Assert.Equal(1UL,s.GrammarContext);
        Assert.True(s.ParseWithContext(ProbeModule.Instance.Other,true,&Both));Assert.Equal(1UL,s.GrammarContext);return true;
    }
    static bool Both(ref ParserState s){Assert.Equal(3UL,s.GrammarContext);return true;}
    static bool False(ref ParserState s){Assert.Equal(0UL,s.GrammarContext);return false;}
    static bool Throw(ref ParserState s)=>throw new InvalidOperationException();
    [Fact] public void Separate_and_parallel_parses_start_with_false_context()
    {
        Parallel.For(0,24,i=>{using var p=L.Parse(i%2==0?"axnx":"nxax",Start);Assert.True(p.Success&&!p.HasErrors);});
    }
}
