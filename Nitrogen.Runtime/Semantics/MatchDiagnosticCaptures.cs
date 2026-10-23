namespace Nitrogen.Semantics;

/// <summary>Borrowed, typed semantic views of captures while a match diagnostic is formatted.</summary>
public static class MatchDiagnosticCaptures
{
    public static T Node<T>(FileSemantics file, IReadOnlyDictionary<string, QuoteArgument> captures, string name)
        where T : struct, ISemanticView<T> => View<T>(file, ((QuoteArgument.Scalar)captures[name]).Value);

    public static IReadOnlyList<T> List<T>(FileSemantics file, IReadOnlyDictionary<string, QuoteArgument> captures, string name)
        where T : struct, ISemanticView<T> => Array.AsReadOnly(((QuoteArgument.Many)captures[name]).Values
            .Select(value => View<T>(file, value)).ToArray());

    static T View<T>(FileSemantics file, QuoteNode value) where T : struct, ISemanticView<T>
    {
        if (!ReferenceEquals(file.Tree, value.Tree)) throw new InvalidOperationException("A diagnostic capture belongs to another syntax tree.");
        return T.Create(file, value.Node);
    }
}
