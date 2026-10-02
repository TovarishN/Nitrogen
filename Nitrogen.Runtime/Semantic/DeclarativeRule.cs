using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

public enum DeclarativeForm
{
    None,
    Operation,
    Literal,
    Text,
    Sequence,
    Value,
    Reference,
    Repeat,
}

/// <summary>One syntax kind's declarative typing and lowering clauses (issue 251), emitted by the generator as data.</summary>
public sealed class DeclarativeRule
{
    public DeclarativeRule(int localKind, DeclarativeForm form, string? target, int[] arguments,
        string? declaredType, int declaredTypeChild, int sequenceStride = 1,
        string?[]? argumentSequenceTypes = null, int[]? argumentSequenceStrides = null,
        bool[]? argumentTexts = null, string?[]? argumentOptionalTypes = null,
        Property? property = null, Property? typeProperty = null, Property? operationProperty = null,
        bool optionalOperation = false, Property? initializerProperty = null, bool[]? argumentInferredSequences = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (form == DeclarativeForm.Repeat && (arguments.Length != 3 || target is null))
            throw new ArgumentException("Repeat lowering requires an element type and count, iterator, and template fields.");
        if (form == DeclarativeForm.Value && property is null)
            throw new ArgumentException("Value lowering requires a semantic property.", nameof(property));
        if (form != DeclarativeForm.Value && property is not null)
            throw new ArgumentException("Only value lowering accepts a semantic property.", nameof(property));
        if (typeProperty is not null && (form is not (DeclarativeForm.Value or DeclarativeForm.Reference) || target is not null))
            throw new ArgumentException("Computed type requires value or reference lowering without a fixed target.", nameof(typeProperty));
        if (form == DeclarativeForm.Value && target is null && typeProperty is null)
            throw new ArgumentException("Value lowering requires a fixed target or semantic type property.", nameof(target));
        if (form == DeclarativeForm.Reference && (target is not null || typeProperty is null))
            throw new ArgumentException("Reference lowering requires a semantic type property without a fixed target.", nameof(typeProperty));
        if (operationProperty is not null && (form != DeclarativeForm.Operation || target is not null))
            throw new ArgumentException("Computed operation lowering requires an operation without a fixed target.", nameof(operationProperty));
        if (form == DeclarativeForm.Operation && target is null && operationProperty is null)
            throw new ArgumentException("Operation lowering requires a fixed target or computed operation property.", nameof(target));
        if (optionalOperation && operationProperty is null)
            throw new ArgumentException("Optional operation lowering requires a computed operation property.", nameof(optionalOperation));
        if (initializerProperty is not null && form != DeclarativeForm.Reference)
            throw new ArgumentException("An initializer property requires reference lowering.", nameof(initializerProperty));
        LocalKind = localKind;
        Form = form;
        Target = target;
        Arguments = Array.AsReadOnly((int[])arguments.Clone());
        DeclaredType = declaredType;
        DeclaredTypeChild = declaredTypeChild;
        if (sequenceStride is not 1 and not 2) throw new ArgumentOutOfRangeException(nameof(sequenceStride));
        SequenceStride = sequenceStride;
        ArgumentSequenceTypes = Array.AsReadOnly(argumentSequenceTypes is null
            ? new string?[arguments.Length] : (string?[])argumentSequenceTypes.Clone());
        ArgumentSequenceStrides = Array.AsReadOnly(argumentSequenceStrides is null
            ? Enumerable.Repeat(1, arguments.Length).ToArray() : (int[])argumentSequenceStrides.Clone());
        ArgumentTexts = Array.AsReadOnly(argumentTexts is null
            ? new bool[arguments.Length] : (bool[])argumentTexts.Clone());
        ArgumentOptionalTypes = Array.AsReadOnly(argumentOptionalTypes is null
            ? new string?[arguments.Length] : (string?[])argumentOptionalTypes.Clone());
        ArgumentInferredSequences = Array.AsReadOnly(argumentInferredSequences is null
            ? new bool[arguments.Length] : (bool[])argumentInferredSequences.Clone());
        Property = property;
        TypeProperty = typeProperty;
        OperationProperty = operationProperty;
        OptionalOperation = optionalOperation;
        InitializerProperty = initializerProperty;
        if (ArgumentSequenceTypes.Count != arguments.Length || ArgumentSequenceStrides.Count != arguments.Length ||
            ArgumentTexts.Count != arguments.Length || ArgumentOptionalTypes.Count != arguments.Length ||
            ArgumentInferredSequences.Count != arguments.Length ||
            ArgumentSequenceStrides.Any(stride => stride is not 1 and not 2) ||
            Enumerable.Range(0, arguments.Length).Any(i =>
                (ArgumentTexts[i] && (ArgumentSequenceTypes[i] is not null || ArgumentOptionalTypes[i] is not null)) ||
                (ArgumentSequenceTypes[i] is not null && ArgumentOptionalTypes[i] is not null) ||
                (ArgumentInferredSequences[i] && (ArgumentTexts[i] || ArgumentSequenceTypes[i] is not null || ArgumentOptionalTypes[i] is not null))))
            throw new ArgumentException("Invalid operation argument sequence metadata.");
    }

    public int LocalKind { get; }

    public DeclarativeForm Form { get; }

    /// <summary>Operation: the operation ID. Literal and Sequence: the type name. Text: Core.Text.</summary>
    public string? Target { get; }

    /// <summary>Operation: argument child indices in parameter order. Other forms: one child index, -1 for the node itself.</summary>
    public IReadOnlyList<int> Arguments { get; }

    /// <summary>A qualified type the declared symbol has; null when absent or read from <see cref="DeclaredTypeChild"/>.</summary>
    public string? DeclaredType { get; }

    /// <summary>The child whose text names the declared symbol's type; -1 when absent.</summary>
    public int DeclaredTypeChild { get; }

    /// <summary>One for ordinary lists, two when list items alternate with separators.</summary>
    public int SequenceStride { get; }

    public IReadOnlyList<string?> ArgumentSequenceTypes { get; }
    public IReadOnlyList<bool> ArgumentInferredSequences { get; }

    public IReadOnlyList<int> ArgumentSequenceStrides { get; }

    public IReadOnlyList<bool> ArgumentTexts { get; }

    public IReadOnlyList<string?> ArgumentOptionalTypes { get; }

    /// <summary>The node's computed float property for value lowering.</summary>
    public Property? Property { get; }

    /// <summary>The node's computed semantic type for dynamically typed value lowering.</summary>
    public Property? TypeProperty { get; }

    /// <summary>The node's computed exact operation signature.</summary>
    public Property? OperationProperty { get; }

    /// <summary>A missing computed operation leaves this node outside declarative lowering.</summary>
    public bool OptionalOperation { get; }

    /// <summary>Optional initializer syntax node in this file; null retains a supplied-input reference.</summary>
    public Property? InitializerProperty { get; }
}
