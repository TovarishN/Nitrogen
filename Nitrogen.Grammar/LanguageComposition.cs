namespace Nitrogen.Grammar;

/// <summary>A configured language's selected syntax inputs and stable generated entry point.</summary>
public sealed record LanguageComposition(string Name, string Start, string Namespace,
    EquatableArray<GrammarInput> Grammars);
