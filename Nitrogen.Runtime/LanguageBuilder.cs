using Nitrogen.Semantic;

namespace Nitrogen;

public sealed unsafe class LanguageBuilder
{
    readonly List<SyntaxModule> _modules = [];
    readonly List<SemanticModule> _semanticModules = [];
    delegate*<ReadOnlySpan<char>, int, int> _trivia = &StandardTrivia.WhitespaceAndComments;
    AsciiSet _triviaStart = StandardTrivia.WhitespaceAndCommentsStart;

    public LanguageBuilder Add(SyntaxModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!_modules.Contains(module)) _modules.Add(module);
        return this;
    }

    public LanguageBuilder AddSemantic(SemanticModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _semanticModules.Add(module);
        return this;
    }

    /// <summary>Custom trivia, called at every position where trivia is skipped.</summary>
    public LanguageBuilder WithTrivia(delegate*<ReadOnlySpan<char>, int, int> trivia) => WithTrivia(trivia, AsciiSet.Any);

    /// <summary>Custom trivia that can only start at <paramref name="startChars"/>; elsewhere skipping is a no-op without the call.</summary>
    public LanguageBuilder WithTrivia(delegate*<ReadOnlySpan<char>, int, int> trivia, AsciiSet startChars)
    {
        _trivia = trivia;
        _triviaStart = startChars;
        return this;
    }

    public Language Build()
    {
        if (!TryBuild(out var language, out var diagnostics))
            throw new SemanticCompositionException(diagnostics);
        return language!;
    }

    public bool TryBuild(out Language? language, out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        language = null;
        var catalog = SemanticCatalog.Compose(_semanticModules, out diagnostics);
        if (catalog is null) return false;
        // A syntax-only language (no semantic modules, as the workspace and CLI build) has no catalog
        // to resolve declarative clauses against; they stay dormant until semantics are supplied.
        var declarative = DeclarativeLowering.Empty;
        SemanticModule? registrations = null;
        if (_semanticModules.Count > 0 &&
            !DeclarativeLowering.TryResolve(_modules, catalog, out declarative, out registrations, out diagnostics))
            return false;
        if (registrations is not null)
        {
            catalog = SemanticCatalog.Compose(_semanticModules.Append(registrations), out diagnostics);
            if (catalog is null) return false;
        }
        language = BuildSyntax(catalog, declarative!);
        return true;
    }

    Language BuildSyntax(SemanticCatalog catalog, DeclarativeLowering declarative)
    {
        var registry = new ExtensionRegistry();
        foreach (var module in _modules)
        {
            registry.Current = module;
            module.Register(registry);
        }
        registry.Current = null;
        var modules = _modules.ToArray();
        return new Language(modules, registry.Freeze(modules), _trivia, _triviaStart, catalog, declarative);
    }
}
