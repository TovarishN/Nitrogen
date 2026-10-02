namespace Nitrogen.Semantic;

/// <summary>A module-qualified type whose identity includes its ordered type arguments.</summary>
public sealed class SemanticType : IEquatable<SemanticType>
{
    readonly SemanticType[] _arguments;

    SemanticType(string module, string name, SemanticType[] arguments)
    {
        Module = module;
        Name = name;
        Id = module + "." + name;
        _arguments = arguments;
        Arguments = Array.AsReadOnly(_arguments);
    }

    public string Module { get; }
    public string Name { get; }
    public string Id { get; }
    public IReadOnlyList<SemanticType> Arguments { get; }

    public static SemanticType Named(string module, string name, params SemanticType[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(argument => argument is null))
            throw new ArgumentException("Type arguments cannot contain null.", nameof(arguments));
        return new SemanticType(module, name, (SemanticType[])arguments.Clone());
    }

    public bool Equals(SemanticType? other) =>
        other is not null && Id == other.Id && _arguments.SequenceEqual(other._arguments);

    public override bool Equals(object? obj) => obj is SemanticType other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        foreach (var argument in _arguments) hash.Add(argument);
        return hash.ToHashCode();
    }

    public override string ToString() => _arguments.Length == 0
        ? Id
        : Id + "<" + string.Join(", ", _arguments.Select(argument => argument.ToString())) + ">";
}

public static class SemanticTypes
{
    public static readonly SemanticType Scalar = SemanticType.Named("Core", "Scalar");
    public static readonly SemanticType Bool = SemanticType.Named("Core", "Bool");
    public static readonly SemanticType Error = SemanticType.Named("Core", "Error");
    public static readonly SemanticType Text = SemanticType.Named("Core", "Text");
    public static readonly SemanticType Angle = SemanticType.Named("Units", "Angle");

    public static SemanticType SequenceOf(SemanticType element) =>
        SemanticType.Named("Core", "Sequence", element ?? throw new ArgumentNullException(nameof(element)));

    public static SemanticType OptionalOf(SemanticType element) =>
        SemanticType.Named("Core", "Optional", element ?? throw new ArgumentNullException(nameof(element)));
}
