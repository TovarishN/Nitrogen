namespace Nitrogen.Semantic;

public sealed record CallParameter(string Name, SemanticType Type);

/// <summary>One overload of a callable name: its parameters, its result, and an optional one-line summary.</summary>
public sealed record CallSignature(IReadOnlyList<CallParameter> Parameters, SemanticType Result, string? Summary = null);

/// <summary>
/// A language's callable names and their overloads, for the editor's signature help. A workspace
/// language's helper source exports one as a public static field or property.
/// </summary>
public sealed class CallSignatures(IReadOnlyDictionary<string, IReadOnlyList<CallSignature>> byName)
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<CallSignature>> _byName = byName ?? throw new ArgumentNullException(nameof(byName));

    /// <summary>The overloads of <paramref name="name"/>, in the order given; empty for an unknown name.</summary>
    public IReadOnlyList<CallSignature> For(string name) => _byName.TryGetValue(name, out var overloads) ? overloads : [];
}
