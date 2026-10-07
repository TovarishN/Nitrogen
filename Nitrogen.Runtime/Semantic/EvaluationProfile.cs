using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>
/// How an editor shows the values of a language's statements: which syntax kinds are statements, the
/// host handlers that project their HIR, the values of builtin symbols, and how a value reads as text.
/// A workspace language's helper source exports one as a public static field or property; the
/// workspace binds it to the language's composed catalog.
/// </summary>
/// <param name="handlers">The handlers, built from the catalog the profile is bound to (so their signatures are the catalog's own).</param>
/// <param name="builtins">The value of a builtin symbol, or null when it has none.</param>
public sealed class EvaluationProfile(
    IReadOnlySet<int> statementKinds,
    Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
    Func<Symbol, ProjectedValue?> builtins,
    Func<ProjectedValue, string> format)
{
    readonly Func<SemanticCatalog, IEnumerable<ProjectionHandler>> _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    readonly Func<Symbol, ProjectedValue?> _builtins = builtins ?? throw new ArgumentNullException(nameof(builtins));
    readonly Func<ProjectedValue, string> _format = format ?? throw new ArgumentNullException(nameof(format));

    /// <summary>The syntax kinds whose nodes are lowered and shown, one value each.</summary>
    public IReadOnlySet<int> StatementKinds { get; } = statementKinds ?? throw new ArgumentNullException(nameof(statementKinds));

    /// <summary>The profile with its handlers checked against <paramref name="catalog"/>; throws when a handler differs from it or repeats.</summary>
    public BoundEvaluation Bind(SemanticCatalog catalog) => new(this, new ProjectionRegistry(catalog, _handlers(catalog)));

    public ProjectedValue? Builtin(Symbol symbol) => _builtins(symbol);

    public string Format(ProjectedValue value) => _format(value);
}

/// <summary>A profile bound to one language's catalog: what an editor projects that language's statements with.</summary>
public sealed record BoundEvaluation(EvaluationProfile Profile, ProjectionRegistry Registry);
