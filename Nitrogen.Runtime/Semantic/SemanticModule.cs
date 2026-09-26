namespace Nitrogen.Semantic;

/// <summary>A typed operation exported by a semantic module.</summary>
public sealed class OperationSignature : IEquatable<OperationSignature>
{
    readonly SemanticType[] _inputs;

    public OperationSignature(string id, SemanticType result, params SemanticType[] inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Any(input => input is null))
            throw new ArgumentException("Operation inputs cannot contain null.", nameof(inputs));
        Id = id;
        Result = result;
        _inputs = (SemanticType[])inputs.Clone();
        Inputs = Array.AsReadOnly(_inputs);
    }

    public string Id { get; }
    public SemanticType Result { get; }
    public IReadOnlyList<SemanticType> Inputs { get; }

    public bool Equals(OperationSignature? other) => other is not null && Id == other.Id &&
        Result.Equals(other.Result) && _inputs.SequenceEqual(other._inputs);
    public override bool Equals(object? obj) => obj is OperationSignature other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Result);
        foreach (var input in _inputs) hash.Add(input);
        return hash.ToHashCode();
    }
}

/// <summary>One independently compiled module's semantic imports and exports.</summary>
public sealed class SemanticModule
{
    public SemanticModule(string name, IEnumerable<string> imports, IEnumerable<SemanticType> types,
        IEnumerable<OperationSignature> operations, IEnumerable<LoweringRegistration>? lowerers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(operations);
        Name = name;
        Imports = Array.AsReadOnly(imports.ToArray());
        Types = Array.AsReadOnly(types.ToArray());
        Operations = Array.AsReadOnly(operations.ToArray());
        Lowerers = Array.AsReadOnly((lowerers ?? []).ToArray());
    }

    public string Name { get; }
    public IReadOnlyList<string> Imports { get; }
    public IReadOnlyList<SemanticType> Types { get; }
    public IReadOnlyList<OperationSignature> Operations { get; }
    public IReadOnlyList<LoweringRegistration> Lowerers { get; }
}
