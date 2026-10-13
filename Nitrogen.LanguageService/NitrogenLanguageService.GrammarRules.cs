using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    void CheckGrammarRename(string uri, DocumentPosition position)
    {
        if (GrammarRuleAt(uri, position) is not { } grammar) return;
        foreach (var reference in grammar.References)
        {
            string? text = ReadNavigationText(new Uri(reference.Uri).LocalPath);
            if (text is null) continue;
            var lines = new LineMap(text);
            int start = lines.OffsetOf(reference.Range.Start), end = lines.OffsetOf(reference.Range.End);
            if (text.Substring(start, end - start).Contains('.'))
                throw new RenameRefusedException("Renaming a grammar rule with qualified references is not yet supported. Update its resolved uses together.");
        }
    }

    sealed record GrammarRuleOccurrence(string Identity, string Path, GrammarSpan Span, bool IsDefinition);

    (IReadOnlyList<DocumentLocation> Definitions, IReadOnlyList<DocumentLocation> References)? GrammarRuleAt(string uri, DocumentPosition position)
    {
        if (!IsGrammarDocument(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile ||
            NavigationScopeFor(parsed.LocalPath) is not { } scope || ReadNavigationText(parsed.LocalPath) is not { } current)
            return null;
        var files = scope.Grammars.Select(path => (Path: path, Text: ReadNavigationText(path)))
            .Where(f => f.Text is not null).Select(f => (f.Path, Text: f.Text!, File: GrammarParser.Parse(DocumentSignatures.Read(f.Text!).MaskedText).File))
            .Where(f => f.File is not null).ToArray();
        var analysis = new GrammarAnalysis(files.SelectMany(f => f.File!.Modules));
        var occurrences = new List<GrammarRuleOccurrence>();
        foreach (var file in files)
            foreach (var module in file.File!.Modules)
            {
                foreach (var rule in module.Rules)
                {
                    int headerEnd = rule switch
                    {
                        SyntaxRule syntax => syntax.Body.Span.Start,
                        TokenRule token => token.Body.Span.Start,
                        _ => file.Text.IndexOf('{', rule.Span.Start),
                    };
                    var name = GrammarLexer.Tokenize(file.Text.Substring(rule.Span.Start, headerEnd - rule.Span.Start)).Last(t => t.Value == rule.Name);
                    occurrences.Add(new(module.Name + "." + rule.Name, file.Path, new(rule.Span.Start + name.Start, name.Length), true));
                    switch (rule)
                    {
                        case SyntaxRule syntax: Visit(syntax.Body, null); break;
                        case TokenRule token: Visit(token.Body, null); break;
                        case ExtensibleRule extensible:
                            foreach (var alternative in extensible.Alternatives) Visit(alternative.Body, module.Name);
                            break;
                    }
                }
                foreach (var extend in module.Extends)
                    if (analysis.Resolve(extend.Target, extend.Span, module, null) is { } target)
                    {
                        var name = GrammarLexer.Tokenize(file.Text.Substring(extend.Span.Start,
                            file.Text.IndexOf('{', extend.Span.Start) - extend.Span.Start)).Last(t => t.Value == extend.Target);
                        occurrences.Add(new(target.Key, file.Path, new(extend.Span.Start + name.Start, name.Length), false));
                        foreach (var alternative in extend.Alternatives) Visit(alternative.Body, target.Module.Name);
                    }
                foreach (var builtin in module.Builtins)
                    if (builtin.Scope is { } name && analysis.Resolve(name.Name, name.Span, module, null) is { } target)
                        occurrences.Add(new(target.Key, file.Path, name.Span, false));

                void Visit(Expr expression, string? implicitModule)
                {
                    if (expression is ReferenceExpr reference && analysis.Resolve(reference.Name, reference.Span, module, implicitModule) is { } target)
                        occurrences.Add(new(target.Key, file.Path, reference.Span, false));
                    foreach (var child in GrammarExtraction.Children(expression)) Visit(child, implicitModule);
                }
            }
        int offset = new LineMap(current).OffsetOf(position);
        var found = occurrences.FirstOrDefault(o => o.Path == parsed.LocalPath && o.Span.Start <= offset && offset < o.Span.End);
        if (found is null) return null;
        DocumentLocation Location(GrammarRuleOccurrence occurrence) => new(new Uri(occurrence.Path).AbsoluteUri,
            new LineMap(files.First(f => f.Path == occurrence.Path).Text).RangeOf(new(occurrence.Span.Start, occurrence.Span.Length)));
        return (occurrences.Where(o => o.Identity == found.Identity && o.IsDefinition).Select(Location).Distinct().ToArray(),
            occurrences.Where(o => o.Identity == found.Identity && !o.IsDefinition).Select(Location).Distinct().ToArray());
    }
}
