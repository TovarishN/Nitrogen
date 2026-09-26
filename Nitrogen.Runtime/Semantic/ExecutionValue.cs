namespace Nitrogen.Semantic;

/// <summary>A finite numeric value in the executable scalar and angle slice.</summary>
public sealed class ExecutionValue
{
    public ExecutionValue(SemanticType type, float number)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.Equals(SemanticTypes.Scalar) && !type.Equals(SemanticTypes.Angle))
            throw new ArgumentException("Only scalar and angle values are executable.", nameof(type));
        if (!float.IsFinite(number))
            throw new ArgumentException("A finite number is required.", nameof(number));
        Type = type;
        Number = number;
    }

    public SemanticType Type { get; }
    public float Number { get; }
}

public sealed record ExecutionDiagnostic(string Code, SourceOrigin Origin, string Message);

public sealed class ExecutionResult
{
    public ExecutionResult(ExecutionValue? value, IEnumerable<SourceOrigin> origins,
        IEnumerable<ExecutionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Value = value;
        Origins = Array.AsReadOnly(origins.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public ExecutionValue? Value { get; }
    public IReadOnlyList<SourceOrigin> Origins { get; }
    public IReadOnlyList<ExecutionDiagnostic> Diagnostics { get; }
}
