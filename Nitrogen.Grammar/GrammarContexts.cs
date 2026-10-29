namespace Nitrogen.Grammar;

internal sealed record GrammarContextSymbol(ModuleDecl Module, NameDecl Name, int Index);

internal sealed partial class GrammarAnalysis
{
    public GrammarContextSymbol? ResolveContext(NameDecl name, ModuleDecl from, string? implicitModule, GrammarReport? report = null)
    {
        GrammarContextSymbol? In(ModuleDecl module,string local)
        {
            for(int i=0;i<module.Contexts.Count;i++)if(module.Contexts[i].Name==local)return new(module,module.Contexts[i],i);
            return null;
        }
        int dot=name.Name.LastIndexOf('.');
        if(dot>=0)
        {
            string owner=name.Name.Substring(0,dot),local=name.Name.Substring(dot+1);
            if(_byName.TryGetValue(owner,out var module)&&In(module,local) is {} result)return result;
            report?.Invoke(GrammarCodes.UnknownContext,$"unknown context '{name.Name}'",name.Span,from);return null;
        }
        if(In(from,name.Name) is {} own)return own;
        var candidates=ImportedModules(from,implicitModule).Select(imported=>In(_byName[imported],name.Name)).Where(c=>c is not null).ToArray();
        if(candidates.Length==1)return candidates[0];
        report?.Invoke(GrammarCodes.UnknownContext,candidates.Length>1?$"ambiguous context '{name.Name}'":$"unknown context '{name.Name}'",name.Span,from);
        return null;
    }

    public IEnumerable<(NameDecl Name,GrammarContextSymbol Symbol)> ContextOccurrences(ModuleDecl module)
    {
        var result=new List<(NameDecl,GrammarContextSymbol)>();
        void Visit(Expr expr,string? implicitModule)
        {
            NameDecl? name=expr switch {ContextTestExpr c=>c.Context,ContextScopeExpr c=>c.Context,_=>null};
            if(name is not null && ResolveContext(name,module,implicitModule) is {} symbol)result.Add((name,symbol));
            foreach(var child in GrammarExtraction.Children(expr))Visit(child,implicitModule);
        }
        foreach(var rule in module.Rules)
            switch(rule)
            {
                case SyntaxRule syntax:Visit(syntax.Body,null);break;
                case ExtensibleRule point:foreach(var alternative in point.Alternatives)Visit(alternative.Body,module.Name);break;
            }
        foreach(var extend in module.Extends)
            foreach(var alternative in extend.Alternatives)Visit(alternative.Body,Resolve(extend.Target,extend.Span,module,null)?.Module.Name);
        foreach(var function in module.Functions)Visit(function.Body,null);
        return result;
    }

    public IEnumerable<GrammarContextSymbol> ContextReferences(ModuleDecl module)
    {
        var found=new Dictionary<string,GrammarContextSymbol>(StringComparer.Ordinal);
        void Visit(Expr expr,string? implicitModule)
        {
            NameDecl? name=expr switch {ContextTestExpr c=>c.Context,ContextScopeExpr c=>c.Context,_=>null};
            if(name is not null && ResolveContext(name,module,implicitModule) is {} symbol)
                found[symbol.Module.Name+"."+symbol.Name.Name]=symbol;
            foreach(var child in GrammarExtraction.Children(expr))Visit(child,implicitModule);
        }
        foreach(var rule in module.Rules)
            switch(rule)
            {
                case SyntaxRule syntax:Visit(syntax.Body,null);break;
                case ExtensibleRule point:foreach(var alternative in point.Alternatives)Visit(alternative.Body,module.Name);break;
            }
        foreach(var extend in module.Extends)
        {
            string owner=Resolve(extend.Target,extend.Span,module,null)?.Module.Name??module.Name;
            foreach(var alternative in extend.Alternatives)Visit(alternative.Body,owner);
        }
        return found.Values.OrderBy(f=>f.Module.Name+"."+f.Name.Name,StringComparer.Ordinal);
    }
}

internal static class GrammarContextSyntax
{
    // This is an opt-in shape rule for the new context syntax. Legacy choices retain their
    // nominal groups. Only branches with exactly the same material fields may share an owner.
    public static ChoiceExpr? SharedChoice(Expr body)
    {
        if (Unwrap(body) is not ChoiceExpr choice || choice.Alternatives.Count < 2 || !ContainsContext(choice)) return null;
        string shape = GrammarDumper.Dump(Material(choice.Alternatives[0]));
        return choice.Alternatives.Skip(1).All(a => GrammarDumper.Dump(Material(a)) == shape) ? choice : null;
    }

    public static IEnumerable<Expr> FlatElements(Expr expr)
    {
        expr=Unwrap(expr);
        if(expr is SequenceExpr sequence)
            return sequence.Items.SelectMany(FlatElements);
        return new[]{expr};
    }

    static bool ContainsContext(Expr expr) => expr is ContextScopeExpr or ContextTestExpr ||
        GrammarExtraction.Children(expr).Any(ContainsContext);

    static Expr Material(Expr expr) => expr switch
    {
        ContextScopeExpr c => Material(c.Inner),
        LabeledExpr l => l with { Inner = Material(l.Inner) },
        SequenceExpr s => s with { Items = s.Items.Where(e => !NoNode(e)).Select(Material).ToArray() },
        ChoiceExpr c => c with { Alternatives = c.Alternatives.Select(Material).ToArray() },
        RepeatExpr r => r with { Inner = Material(r.Inner) },
        SeparatedListExpr l => l with { Item = Material(l.Item), Separator = Material(l.Separator) },
        _ => expr,
    };

    public static Expr Unwrap(Expr expr) => expr is ContextScopeExpr context ? Unwrap(context.Inner) : expr;

    public static bool NoNode(Expr expr) => expr switch
    {
        LabeledExpr l=>NoNode(l.Inner),ContextScopeExpr c=>NoNode(c.Inner),PredicateExpr or ContextTestExpr=>true,_=>false,
    };
}
