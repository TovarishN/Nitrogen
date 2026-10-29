namespace Nitrogen;

public sealed unsafe partial class Language
{
    readonly Dictionary<GrammarContextDecl, ulong> _grammarContexts = new();

    void ComposeContexts(SyntaxModule[] modules)
    {
        var declarations = modules.OfType<IGrammarContextModule>().SelectMany(contexts => contexts.ContextDeclarations.Select(flag => (Module: (SyntaxModule)contexts, Flag: flag)))
            .OrderBy(entry => entry.Flag.QualifiedName, StringComparer.Ordinal).ToArray();
        if (declarations.Length > 64) throw new LanguageCompositionException("A language supports at most 64 Boolean grammar contexts.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, flag) in declarations)
        {
            if (!ReferenceEquals(flag.Owner, module)) throw new LanguageCompositionException($"Module '{module.Name}' cannot declare context '{flag}'.");
            if (!names.Add(flag.QualifiedName)) throw new LanguageCompositionException($"Duplicate grammar context '{flag}'.");
            _grammarContexts.Add(flag, 1UL << _grammarContexts.Count);
        }
        foreach (var module in modules)
            foreach (var flag in (module as IGrammarContextModule)?.ContextReferences ?? Array.Empty<GrammarContextDecl>())
                if (!_grammarContexts.ContainsKey(flag))
                    throw new LanguageCompositionException($"Module '{module.Name}' needs grammar context '{flag}', whose declaration is not selected.");
    }

    internal ulong ContextMask(GrammarContextDecl context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _grammarContexts.TryGetValue(context, out ulong mask) ? mask
            : throw new InvalidOperationException($"Grammar context '{context}' is not part of this language.");
    }
}
