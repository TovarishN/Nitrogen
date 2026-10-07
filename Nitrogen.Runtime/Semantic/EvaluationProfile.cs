using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>What an evaluation may read besides its source: the host's current time.</summary>
public sealed record EvaluationContext(DateTimeOffset Now)
{
    /// <summary>The local date of <see cref="Now"/>.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}

/// <summary>
/// How an editor shows the values of a language's statements: which syntax kinds are statements, the
/// host handlers that project their HIR, the values of builtin symbols, and how a value reads as text.
/// A workspace language's helper source exports one as a public static field or property; the
/// workspace binds it to the language's composed catalog.
/// </summary>
public sealed class EvaluationProfile
{
    readonly Func<SemanticCatalog, IEnumerable<ProjectionHandler>> _handlers;
    readonly Func<Symbol, EvaluationContext, ProjectedValue?> _builtins;
    readonly Func<ProjectedValue, string> _format;

    /// <summary>A profile whose values never depend on the clock.</summary>
    /// <param name="handlers">The handlers, built from the catalog the profile is bound to (so their signatures are the catalog's own).</param>
    /// <param name="builtins">The value of a builtin symbol, or null when it has none.</param>
    public EvaluationProfile(
        IReadOnlySet<int> statementKinds,
        Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
        Func<Symbol, ProjectedValue?> builtins,
        Func<ProjectedValue, string> format)
        : this(statementKinds, handlers, Ignoring(builtins ?? throw new ArgumentNullException(nameof(builtins))), format, readsClock: false)
    {
    }

    /// <param name="builtins">The value of a builtin symbol in a context, or null when it has none.</param>
    /// <param name="readsClock">Whether values may depend on the context's time, so a host must not reuse them across days.</param>
    public EvaluationProfile(
        IReadOnlySet<int> statementKinds,
        Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
        Func<Symbol, EvaluationContext, ProjectedValue?> builtins,
        Func<ProjectedValue, string> format,
        bool readsClock)
    {
        StatementKinds = statementKinds ?? throw new ArgumentNullException(nameof(statementKinds));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _builtins = builtins ?? throw new ArgumentNullException(nameof(builtins));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        ReadsClock = readsClock;
    }

    /// <summary>The syntax kinds whose nodes are lowered and shown, one value each.</summary>
    public IReadOnlySet<int> StatementKinds { get; }

    /// <summary>Whether values may depend on the context's time.</summary>
    public bool ReadsClock { get; }

    /// <summary>The profile with its handlers checked against <paramref name="catalog"/>; throws when a handler differs from it or repeats.</summary>
    public BoundEvaluation Bind(SemanticCatalog catalog) => new(this, new ProjectionRegistry(catalog, _handlers(catalog)));

    public ProjectedValue? Builtin(Symbol symbol, EvaluationContext context) => _builtins(symbol, context);

    public string Format(ProjectedValue value) => _format(value);

    static Func<Symbol, EvaluationContext, ProjectedValue?> Ignoring(Func<Symbol, ProjectedValue?> builtins) =>
        (symbol, _) => builtins(symbol);
}

/// <summary>A profile bound to one language's catalog: what an editor projects that language's statements with.</summary>
public sealed record BoundEvaluation(EvaluationProfile Profile, ProjectionRegistry Registry);
