using Nitrogen.Binding;
using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    IReadOnlyList<string> GrammarPathsFor(LanguageEntry language)
    {
        if (_grammarLanguages.FirstOrDefault(l => l.Entry == language) is { } configured) return configured.Files();
        var signature = _signatureEntries.FirstOrDefault(e => e.Value == language).Key;
        return signature is not null && _signatureDefinitions.TryGetValue(signature, out var definition)
            ? definition.Grammars.Select(g => g.Path).ToArray() : [];
    }

    IReadOnlyList<DocumentLocation> BuiltinDefinitions(Document document, Project project, Symbol symbol)
    {
        if (project.BuiltinDeclaration(symbol) is not { } origin) return [];
        var grammars = GrammarPathsFor(document.Language);
        string? scope = origin.Declaration.ScopeKind == 0 ? null : origin.Module.GetKindName(origin.Declaration.ScopeKind);
        var targets = new List<DocumentLocation>();
        foreach (string path in grammars)
        {
            if (ReadNavigationText(path) is not { } text) continue;
            var file = GrammarParser.Parse(DocumentSignatures.Read(text).MaskedText).File;
            if (file is null) continue;
            foreach (var module in file.Modules.Where(m => m.Name == origin.Module.Name))
                foreach (var builtin in module.Builtins.Where(b => b.Kind.Name == symbol.Kind && b.Scope?.Name == scope))
                    foreach (var name in builtin.Names.Where(n => n.Name == symbol.Name))
                        targets.Add(new(new Uri(path).AbsoluteUri, new LineMap(text).RangeOf(new(name.Span.Start, name.Span.Length))));
        }
        return targets;
    }
}
