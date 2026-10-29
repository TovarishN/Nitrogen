using Nitrogen.Grammar;
namespace Nitrogen.LanguageService;
public sealed partial class NitrogenLanguageService
{
    IReadOnlyList<CompletionItem> GrammarContextCompletion(Document document,int offset)
    {
        if(!IsGrammarDocument(document.Uri))return [];
        int start=offset;string text=document.Text;
        while(start>0&&(char.IsLetterOrDigit(text[start-1])||text[start-1] is '_' or '.'))start--;
        var parsed=GrammarParser.Parse(DocumentSignatures.Read(text[..start]+"ContextCompletion"+text[offset..]).MaskedText).File;
        if(parsed is null)return [];
        bool AtName(Expr expr)=> (expr is ContextTestExpr test&&test.Context.Span.Start==start)
            || (expr is ContextScopeExpr scoped&&scoped.Context.Span.Start==start)
            || GrammarExtraction.Children(expr).Any(AtName);
        bool HasSite(ModuleDecl module)=>module.Rules.Any(rule=>rule switch
            {SyntaxRule syntax=>AtName(syntax.Body),ExtensibleRule point=>point.Alternatives.Any(a=>AtName(a.Body)),_=>false})
            ||module.Extends.Any(e=>e.Alternatives.Any(a=>AtName(a.Body)))||module.Functions.Any(f=>AtName(f.Body));
        if(!parsed.Modules.Any(HasSite))return [];
        var owner=parsed.Modules.FirstOrDefault(m=>m.Span.Start<=start&&start<m.Span.End);if(owner is null)return [];
        var modules=parsed.Modules.ToList();
        if(System.Uri.TryCreate(document.Uri,UriKind.Absolute,out var uri)&&uri.IsFile&&NavigationScopeFor(uri.LocalPath) is {} scope)
            foreach(string path in scope.Grammars.Where(p=>p!=uri.LocalPath))
                if(ReadNavigationText(path) is {} other&&GrammarParser.Parse(DocumentSignatures.Read(other).MaskedText).File is {} file)modules.AddRange(file.Modules);
        string written=text[start..offset];int dot=written.LastIndexOf('.');string prefix=written[(dot+1)..];
        var visible=owner.Usings.Select(u=>u.Module).Prepend(owner.Name).ToHashSet(StringComparer.Ordinal);
        var candidates=modules.Where(m=>dot>=0?m.Name==written[..dot]:visible.Contains(m.Name))
            .SelectMany(m=>m.Contexts.Select(c=>(Module:m.Name,Name:c.Name))).Where(c=>c.Name.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
        var items=new List<CompletionItem>();
        foreach(var group in candidates.GroupBy(c=>c.Name))
        {
            var local=group.Where(c=>c.Module==owner.Name).ToArray();
            foreach(var c in dot<0&&local.Length>0?local:group.ToArray())
            {
                string label=dot<0&&local.Length==0&&group.Count()>1?c.Module+"."+c.Name:c.Name;
                var replace=new DocumentRange(document.Lines.PositionOf(start+dot+1),document.Lines.PositionOf(offset));
                items.Add(new(label,CompletionKind.Variable,$"context {c.Module}.{c.Name}: Boolean (default false)",replace));
            }
        }
        return items;
    }

    static void CheckContextRename(GrammarOccurrences scope,GrammarRuleOccurrence target,List<GrammarRuleOccurrence> uses,string newName)
    {
        string qualified=target.Identity["context ".Length..];int dot=qualified.LastIndexOf('.');string owner=qualified[..dot];
        var modules=scope.Files.SelectMany(f=>f.File.Modules).ToArray();var analysis=new GrammarAnalysis(modules);
        if(modules.Where(m=>m.Name==owner).SelectMany(m=>m.Contexts).Any(c=>c.Name==newName&&newName!=NameText(scope,target)))
            throw new RenameRefusedException($"'{newName}' already names a grammar context in module '{owner}'.");
        foreach(var use in uses.Where(u=>!u.IsDefinition))
        {
            if(LastSegment(scope,use).Start!=use.Span.Start)continue;
            var from=scope.Files.Single(f=>f.Path==use.Path).File.Modules.First(m=>m.Span.Start<=use.Span.Start&&use.Span.End<=m.Span.End);
            bool ambiguous=false;
            var bound=analysis.ResolveContext(new(newName,use.Span),from,null,(code,message,span,module)=>ambiguous|=message.Contains("ambiguous",StringComparison.Ordinal));
            if(ambiguous||bound is not null&&"context "+bound.Module.Name+"."+bound.Name.Name!=target.Identity)
                throw new RenameRefusedException($"'{newName}' would capture or obscure a grammar context reference.");
        }
    }
}
