using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    // Grammar-defined words have no bound symbol. Their parsed parent identifies the rule;
    // searching by spelling would confuse identical keywords in different productions.
    IReadOnlyList<DocumentLocation> GrammarKeywordDefinitions(string uri, DocumentPosition position)
    {
        if (IsGrammarDocument(uri) || !_documents.TryGetValue(uri, out var document)) return [];
        var tree = document.Parsed.Tree;
        int offset = document.Lines.OffsetOf(position), owner = -1;
        for (int node = 0; node < tree.NodeCount; node++)
        {
            var span = tree.Span(node);
            if (span.Start > offset || span.End <= offset || tree.Kind(node) != SyntaxKinds.Literal) continue;
            if (!SemanticTokenBuilder.IsWord(document.Text.AsSpan(span.Start, span.Length))) return [];
            owner = tree.Parent(node);
            break;
        }
        while (owner >= 0 && SyntaxKinds.ModuleOf(tree.Kind(owner)) == 0) owner = tree.Parent(owner);
        if (owner < 0) return [];
        int kind = tree.Kind(owner);
        var syntaxModule = document.Language.Language.Modules.FirstOrDefault(m => m.Id == SyntaxKinds.ModuleOf(kind));
        if (syntaxModule is null) return [];
        string ruleName = syntaxModule.GetKindName(SyntaxKinds.LocalOf(kind));
        var targets = new List<DocumentLocation>();
        foreach (string path in GrammarPathsFor(document.Language))
        {
            if (ReadNavigationText(path) is not { } text) continue;
            var file = GrammarParser.Parse(DocumentSignatures.Read(text).MaskedText).File;
            if (file is null) continue;
            foreach (var module in file.Modules.Where(m => m.Name == syntaxModule.Name))
            {
                foreach (var rule in module.Rules)
                {
                    if (rule is SyntaxRule syntax && rule.Name == ruleName) Add(rule.Span, syntax.Body.Span.Start);
                    if (rule is ExtensibleRule extensible)
                        foreach (var alternative in extensible.Alternatives.Where(a => a.Name == ruleName)) Add(alternative.Span, alternative.Body.Span.Start);
                }
                foreach (var extend in module.Extends)
                    foreach (var alternative in extend.Alternatives.Where(a => a.Name == ruleName)) Add(alternative.Span, alternative.Body.Span.Start);
            }
            void Add(GrammarSpan span, int bodyStart)
            {
                var name = GrammarLexer.Tokenize(text.Substring(span.Start, bodyStart - span.Start)).LastOrDefault(t => t.Value == ruleName);
                if (name.Value == ruleName)
                    targets.Add(new(new Uri(path).AbsoluteUri, new LineMap(text).RangeOf(new(span.Start + name.Start, name.Length))));
            }
        }
        return targets.Distinct().ToArray();
    }
}
