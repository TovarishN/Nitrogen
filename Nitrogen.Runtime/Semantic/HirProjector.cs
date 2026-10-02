using System.Collections.ObjectModel;
using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>A host value whose semantic type is checked at every HIR boundary.</summary>
public sealed class ProjectedValue
{
    public ProjectedValue(SemanticType type, object value, IReadOnlyList<SourceOrigin>? origins = null)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Origins = Array.AsReadOnly((origins ?? []).ToArray());
    }

    public SemanticType Type { get; }
    public object Value { get; }
    public IReadOnlyList<SourceOrigin> Origins { get; }

    internal ProjectedValue At(IReadOnlyList<SourceOrigin> origins) =>
        Origins.Count == 0 ? new ProjectedValue(Type, Value, origins) : this;
}

/// <summary>The payload of a projected Core.Optional value.</summary>
public sealed record ProjectedOptional(ProjectedValue? Value);

/// <summary>A domain validation failure at a source location carried by a projected argument.</summary>
public sealed class ProjectionException(string code, SourceOrigin origin, string message) : Exception(message)
{
    public string Code { get; } = code;
    public SourceOrigin Origin { get; } = origin;
}

public sealed record ProjectionHandler(OperationSignature Signature,
    Func<IReadOnlyList<ProjectedValue>, ProjectedValue>? Project)
{
    public Func<Func<int, ProjectedValue?>, ProjectedValue?>? ProjectDeferred { get; init; }

    /// <summary>Bind a synchronous handler that requests typed arguments by index on demand.
    /// Each argument is evaluated at most once; access expires when the handler returns.</summary>
    public static ProjectionHandler Deferred(OperationSignature signature,
        Func<Func<int, ProjectedValue?>, ProjectedValue?> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new ProjectionHandler(signature, null) { ProjectDeferred = project };
    }
}

/// <summary>Host implementations for exact operation signatures in one semantic catalog.</summary>
public sealed class ProjectionRegistry
{
    readonly IReadOnlyDictionary<string, ProjectionHandler> _handlers;

    public ProjectionRegistry(SemanticCatalog catalog, IEnumerable<ProjectionHandler> handlers)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(handlers);
        var accepted = new Dictionary<string, ProjectionHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (handler is null || handler.Signature is null ||
                (handler.Project is null) == (handler.ProjectDeferred is null))
                throw new ArgumentException("A projection handler requires a signature and implementation.", nameof(handlers));
            if (!catalog.Operations.TryGetValue(handler.Signature.Id, out var exported) ||
                !exported.Equals(handler.Signature))
                throw new ArgumentException($"Operation '{handler.Signature.Id}' differs from the semantic catalog.", nameof(handlers));
            if (!accepted.TryAdd(handler.Signature.Id, handler))
                throw new ArgumentException($"Operation '{handler.Signature.Id}' has duplicate projection handlers.", nameof(handlers));
        }
        _handlers = new ReadOnlyDictionary<string, ProjectionHandler>(accepted);
    }

    public SemanticCatalog Catalog { get; }

    internal bool TryGet(string id, out ProjectionHandler handler) => _handlers.TryGetValue(id, out handler!);
}

public sealed record ProjectionResult(ProjectedValue? Value, IReadOnlyList<ExecutionDiagnostic> Diagnostics);

/// <summary>Evaluates typed HIR with domain handlers, without accessing the source syntax tree.</summary>
public static class HirProjector
{
    /// <summary>Checks the complete tree before any projection handler runs.</summary>
    public static IReadOnlyList<ExecutionDiagnostic> Preflight(HirNode root, ProjectionRegistry registry,
        IReadOnlyDictionary<Symbol, ProjectedValue>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);
        var diagnostics = new List<ExecutionDiagnostic>();
        Check(root, new Dictionary<Symbol, SemanticType>());
        return Array.AsReadOnly(diagnostics.ToArray());

        void Check(HirNode node, IReadOnlyDictionary<Symbol, SemanticType> bound)
        {
            var origin = node.Origins[0];
            if (node is HirRepeat repeat)
            {
                Check(repeat.Count, bound);
                if (repeat.Count is HirConstant { Value: >= 2147483648f } constant && float.IsFinite(constant.Value))
                    diagnostics.Add(new ExecutionDiagnostic("NP0007", repeat.Count.Origins[0], "Repeat count exceeds the supported index range."));
                var inner = new Dictionary<Symbol, SemanticType>(bound) { [repeat.Iterator.Binding] = SemanticTypes.Scalar };
                Check(repeat.Template, inner);
                return;
            }
            switch (node)
            {
                case HirConstant constant when !float.IsFinite(constant.Value):
                    diagnostics.Add(new ExecutionDiagnostic("NP0001", origin, "A projected constant must be finite."));
                    break;
                case HirSymbolRef reference:
                {
                    if (bound.TryGetValue(reference.Symbol.Binding, out var localType))
                    {
                        if (!localType.Equals(reference.Type))
                            diagnostics.Add(new ExecutionDiagnostic("NP0004", origin, "Bound iterator has the wrong type."));
                        break;
                    }
                    var input = inputs?.FirstOrDefault(pair => ReferenceEquals(pair.Key, reference.Symbol.Binding)).Value;
                    if (input is null)
                        diagnostics.Add(new ExecutionDiagnostic("NP0003", origin,
                            $"No input was supplied for '{reference.Symbol.Binding.Name}'."));
                    else if (!input.Type.Equals(reference.Type))
                        diagnostics.Add(new ExecutionDiagnostic("NP0004", origin,
                            $"Input for '{reference.Symbol.Binding.Name}' has the wrong type."));
                    else if (input.Value is float number && !float.IsFinite(number))
                        diagnostics.Add(new ExecutionDiagnostic("NP0001", origin, "A projected numeric input must be finite."));
                    break;
                }
                case HirOperation operation:
                    if (!registry.Catalog.Operations.TryGetValue(operation.Signature.Id, out var exported) ||
                        !exported.Equals(operation.Signature))
                        diagnostics.Add(new ExecutionDiagnostic("NP0004", origin,
                            $"Operation '{operation.Signature.Id}' differs from the catalog."));
                    else if (!registry.TryGet(operation.Signature.Id, out _))
                        diagnostics.Add(new ExecutionDiagnostic("NP0002", origin,
                            $"Operation '{operation.Signature.Id}' has no projection handler."));
                    break;
            }
            var children = node switch
            {
                HirOperation operation => operation.Arguments,
                HirSequence sequence => sequence.Items,
                HirOptional { Value: { } value } => new[] { value },
                _ => Array.Empty<HirNode>(),
            };
            foreach (var child in children) Check(child, bound);
        }
    }

    public static ProjectionResult Project(HirNode root, ProjectionRegistry registry,
        IReadOnlyDictionary<Symbol, ProjectedValue>? inputs = null)
    {
        var preflight = Preflight(root, registry, inputs);
        if (preflight.Count > 0) return new ProjectionResult(null, preflight);
        var diagnostics = new List<ExecutionDiagnostic>();
        var bound = new Dictionary<Symbol, ProjectedValue>();
        var value = Evaluate(root);
        return new ProjectionResult(value, Array.AsReadOnly(diagnostics.ToArray()));

        ProjectedValue? Evaluate(HirNode node)
        {
            var origin = node.Origins[0];
            switch (node)
            {
                case HirConstant constant:
                    if (!float.IsFinite(constant.Value))
                        return Fail("NP0001", origin, "A projected constant must be finite.");
                    return new ProjectedValue(constant.Type, constant.Value, constant.Origins);
                case HirText text:
                    return new ProjectedValue(text.Type, text.Value, text.Origins);
                case HirSymbolRef reference:
                {
                    var input = bound.GetValueOrDefault(reference.Symbol.Binding) ??
                        inputs?.FirstOrDefault(pair => ReferenceEquals(pair.Key, reference.Symbol.Binding)).Value;
                    if (input is null) return Fail("NP0003", origin, $"No input was supplied for '{reference.Symbol.Binding.Name}'.");
                    if (!input.Type.Equals(reference.Type))
                        return Fail("NP0004", origin, $"Input for '{reference.Symbol.Binding.Name}' has the wrong type.");
                    if (input.Value is float number && !float.IsFinite(number))
                        return Fail("NP0001", origin, "A projected numeric input must be finite.");
                    return input.At(reference.Origins);
                }
                case HirRepeat repeat:
                {
                    var count = Evaluate(repeat.Count);
                    if (count is null) return null;
                    if (count.Value is not float number || !float.IsFinite(number) || number >= 2147483648f)
                        return Fail("NP0007", repeat.Count.Origins[0], "Repeat count must be finite and within the supported index range.");
                    int length = number <= 0 ? 0 : (int)number;
                    var groups = new List<ProjectedValue>();
                    bool hadPrevious = bound.TryGetValue(repeat.Iterator.Binding, out var previous);
                    try
                    {
                        for (int i = 0; i < length; i++)
                        {
                            bound[repeat.Iterator.Binding] = new ProjectedValue(SemanticTypes.Scalar, (float)i);
                            var group = Evaluate(repeat.Template);
                            if (group is null) return null;
                            groups.Add(group);
                        }
                    }
                    finally
                    {
                        if (hadPrevious) bound[repeat.Iterator.Binding] = previous!;
                        else bound.Remove(repeat.Iterator.Binding);
                    }
                    return new ProjectedValue(repeat.Type, Array.AsReadOnly(groups.ToArray()), repeat.Origins);
                }
                case HirSequence sequence:
                {
                    var items = new ProjectedValue[sequence.Items.Count];
                    for (int i = 0; i < items.Length; i++)
                    {
                        var item = Evaluate(sequence.Items[i]);
                        if (item is null) return null;
                        if (!item.Type.Equals(sequence.ElementType))
                            return Fail("NP0004", sequence.Items[i].Origins[0],
                                "A projected sequence item has the wrong type.");
                        items[i] = item;
                    }
                    return new ProjectedValue(sequence.Type, Array.AsReadOnly(items), sequence.Origins);
                }
                case HirOptional optional:
                {
                    var value = optional.Value is null ? null : Evaluate(optional.Value);
                    if (optional.Value is not null && value is null) return null;
                    if (value is not null && !value.Type.Equals(optional.ElementType))
                        return Fail("NP0004", optional.Value!.Origins[0],
                            "A projected optional value has the wrong type.");
                    return new ProjectedValue(optional.Type, new ProjectedOptional(value), optional.Origins);
                }
                case HirOperation operation:
                {
                    if (!registry.Catalog.Operations.TryGetValue(operation.Signature.Id, out var exported) ||
                        !exported.Equals(operation.Signature))
                        return Fail("NP0004", origin, $"Operation '{operation.Signature.Id}' differs from the catalog.");
                    if (!registry.TryGet(operation.Signature.Id, out var handler))
                        return Fail("NP0002", origin, $"Operation '{operation.Signature.Id}' has no projection handler.");
                    var arguments = new ProjectedValue?[operation.Arguments.Count];
                    var requested = new bool[arguments.Length];
                    bool active = true, failed = false;
                    ProjectedValue? GetArgument(int i)
                    {
                        if (!active) throw new InvalidOperationException("Deferred argument access has expired.");
                        if (requested[i]) return arguments[i];
                        requested[i] = true;
                        var argument = Evaluate(operation.Arguments[i]);
                        if (argument is null) { failed = true; return null; }
                        if (!argument.Type.Equals(operation.Signature.Inputs[i]))
                        {
                            failed = true;
                            return Fail("NP0004", operation.Arguments[i].Origins[0],
                                $"Argument {i} for '{operation.Signature.Id}' has the wrong type.");
                        }
                        return arguments[i] = argument;
                    }
                    try
                    {
                        ProjectedValue? result;
                        if (handler.ProjectDeferred is { } deferred) result = deferred(GetArgument);
                        else
                        {
                            var eager = new ProjectedValue[arguments.Length];
                            for (int i = 0; i < eager.Length; i++)
                            {
                                var argument = GetArgument(i);
                                if (argument is null) return null;
                                eager[i] = argument;
                            }
                            result = handler.Project!(Array.AsReadOnly(eager));
                        }
                        if (failed) return null;
                        if (result is null || !result.Type.Equals(operation.Type))
                            return Fail("NP0004", origin,
                                $"Projection handler '{operation.Signature.Id}' returned the wrong type.");
                        if (result.Value is float number && !float.IsFinite(number))
                            return Fail("NP0001", origin, $"Operation '{operation.Signature.Id}' produced a nonfinite numeric value.");
                        return result.At(operation.Origins);
                    }
                    catch (ProjectionException error)
                    {
                        return Fail(error.Code, error.Origin, error.Message);
                    }
                    catch (Exception error)
                    {
                        if (failed) return null;
                        return Fail("NP0005", origin,
                                $"Projection handler '{operation.Signature.Id}' failed: {error.GetType().Name}: {error.Message}");
                    }
                    finally { active = false; }
                }
                default:
                    return Fail("NP0006", origin, "This HIR node cannot be projected.");
            }
        }

        ProjectedValue? Fail(string code, SourceOrigin origin, string message)
        {
            diagnostics.Add(new ExecutionDiagnostic(code, origin, message));
            return null;
        }
    }
}
