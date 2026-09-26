using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>One C# registered unit of syntax, semantics, entry points, and host requirements.</summary>
public sealed class ModuleDescriptor
{
    public ModuleDescriptor(string id, SyntaxModule? syntax, SemanticModule? semantics,
        IEnumerable<string> startRules, IEnumerable<OperationSignature> requiredOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(startRules);
        ArgumentNullException.ThrowIfNull(requiredOperations);
        if (syntax is null && semantics is null)
            throw new ArgumentException("Syntax or semantics is required.", nameof(syntax));

        var starts = startRules.ToArray();
        if (starts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Start rule names cannot be null or blank.", nameof(startRules));
        if (syntax is null && starts.Length > 0)
            throw new ArgumentException("Start rules require a syntax module.", nameof(startRules));

        var requirements = requiredOperations.ToArray();
        if (requirements.Any(signature => signature is null))
            throw new ArgumentException("Required operations cannot contain null.", nameof(requiredOperations));

        Id = id;
        Syntax = syntax;
        Semantics = semantics;
        StartRules = Array.AsReadOnly(starts);
        RequiredOperations = Array.AsReadOnly(requirements);
    }

    public string Id { get; }
    public SyntaxModule? Syntax { get; }
    public SemanticModule? Semantics { get; }
    public IReadOnlyList<string> StartRules { get; }
    public IReadOnlyList<OperationSignature> RequiredOperations { get; }
}

/// <summary>A host implementation promised for an exact semantic operation signature.</summary>
public sealed class HostOperationBinding
{
    public HostOperationBinding(OperationSignature signature, Delegate handler)
    {
        Signature = signature ?? throw new ArgumentNullException(nameof(signature));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public OperationSignature Signature { get; }
    public Delegate Handler { get; }
}
