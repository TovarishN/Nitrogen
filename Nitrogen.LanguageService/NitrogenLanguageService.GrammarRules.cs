using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    void CheckGrammarRename(string uri, DocumentPosition position)
    {
        if (GrammarRuleAt(uri, position) is not { } grammar) return;
        var name = NameAt(uri, position);
        var supported = name is { Symbols.Count: 1 } bound
            ? bound.Project.ReferencesTo(bound.Symbols[0]).Select(LocationOf).ToHashSet()
            : new HashSet<DocumentLocation>();
        if (grammar.References.Any(reference => !supported.Contains(reference)))
            throw new RenameRefusedException("Renaming a grammar rule with imported or qualified references is not yet supported. Update its resolved uses together.");
    }

    static string FunctionKey(string module, string name) => "function " + module + "." + name;

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
        var functions = files.SelectMany(f => f.File!.Modules).GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.First().Functions.Select(f => f.Name.Name)), StringComparer.Ordinal);
        var occurrences = new List<GrammarRuleOccurrence>();
        foreach (var file in files)
            foreach (var module in file.File!.Modules)
            {
                // A function's parameters, lets and loop variables: name → identity, declared where they are bound.
                var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                string scopeKey = "";
                foreach (var function in module.Functions)
                {
                    scopeKey = FunctionKey(module.Name, function.Name.Name);
                    occurrences.Add(new(scopeKey, file.Path, function.Name.Span, true));
                    variables.Clear();
                    foreach (var parameter in function.Parameters) Bind(parameter.Name);
                    foreach (var let in function.Lets)
                    {
                        VisitValue(let.Value, null);
                        Bind(let.Name);
                    }
                    Visit(function.Body, null);
                }
                variables.Clear();
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
                    if (expression is ReferenceExpr reference && variables.TryGetValue(reference.Name, out string? bound))
                        occurrences.Add(new(bound, file.Path, reference.Span, false));
                    else if (expression is ReferenceExpr rule && analysis.Resolve(rule.Name, rule.Span, module, implicitModule) is { } target)
                        occurrences.Add(new(target.Key, file.Path, rule.Span, false));
                    // Grammar functions (spec: grammar functions): calls, and rule names among their values.
                    switch (expression)
                    {
                        case GrammarCallExpr call:
                            Called(call.Name);
                            foreach (var argument in call.Arguments) VisitValue(argument, implicitModule);
                            break;
                        case IfExpr conditional:
                            VisitValue(conditional.Condition, implicitModule);
                            break;
                        case ComprehensionExpr comprehension:
                            VisitValue(comprehension.From, implicitModule);
                            if (comprehension.To is { } to) VisitValue(to, implicitModule);
                            Bind(comprehension.Variable);
                            VisitBlock(comprehension.Block, implicitModule, visitBody: false);
                            break;
                        case ValueClassExpr valueClass:
                            foreach (var item in valueClass.Items) VisitItem(item, implicitModule);
                            break;
                    }
                    foreach (var child in GrammarExtraction.Children(expression)) Visit(child, implicitModule);
                }

                void VisitBlock(FunctionBlock block, string? implicitModule, bool visitBody)
                {
                    foreach (var let in block.Lets)
                    {
                        VisitValue(let.Value, implicitModule);
                        Bind(let.Name);
                    }
                    if (block.Where is { } where) VisitValue(where, implicitModule);
                    if (visitBody) Visit(block.Body, implicitModule);
                }

                void VisitItem(ValueClassItem item, string? implicitModule)
                {
                    VisitValue(item.First, implicitModule);
                    if (item.Last is { } last) VisitValue(last, implicitModule);
                }

                void VisitValue(ValueExpr value, string? implicitModule)
                {
                    switch (value)
                    {
                        case NameValueExpr name when variables.TryGetValue(name.Name, out string? bound):
                            occurrences.Add(new(bound, file.Path, name.Span, false));
                            break;
                        case NameValueExpr name when analysis.Resolve(name.Name, name.Span, module, implicitModule) is { } target:
                            occurrences.Add(new(target.Key, file.Path, name.Span, false));
                            break;
                        case CallValueExpr call:
                            Called(call.Name);
                            foreach (var argument in call.Arguments) VisitValue(argument, implicitModule);
                            break;
                        case UnaryValueExpr unary:
                            VisitValue(unary.Operand, implicitModule);
                            break;
                        case BinaryValueExpr binary:
                            VisitValue(binary.Left, implicitModule);
                            VisitValue(binary.Right, implicitModule);
                            break;
                        case IfValueExpr conditional:
                            VisitValue(conditional.Condition, implicitModule);
                            VisitValue(conditional.Then, implicitModule);
                            VisitValue(conditional.Else, implicitModule);
                            break;
                        case ClassValueExpr classValue:
                            foreach (var item in classValue.Items) VisitItem(item, implicitModule);
                            break;
                        case RuleValueExpr rule:
                            VisitBlock(rule.Block, implicitModule, visitBody: true);
                            break;
                    }
                }

                void Bind(NameDecl name)
                {
                    string key = scopeKey + " " + name.Name + "@" + name.Span.Start;
                    variables[name.Name] = key;
                    occurrences.Add(new(key, file.Path, name.Span, true));
                }

                void Called(NameDecl name)
                {
                    int dot = name.Name.LastIndexOf('.');
                    string? owner = dot >= 0
                        ? name.Name.Substring(0, dot)
                        : new[] { module.Name }.Concat(module.Usings.Select(u => u.Module))
                            .FirstOrDefault(m => functions.TryGetValue(m, out var declared) && declared.Contains(name.Name));
                    string local = dot >= 0 ? name.Name.Substring(dot + 1) : name.Name;
                    if (owner is not null && functions.TryGetValue(owner, out var names) && names.Contains(local))
                        occurrences.Add(new(FunctionKey(owner, local), file.Path, name.Span, false));
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
