using System.Collections.ObjectModel;

namespace Nitrogen.Semantic;

/// <summary>Exact, typed host handlers for operations in one semantic catalog.</summary>
public sealed class HostOperationRegistry
{
    readonly IReadOnlyDictionary<string, (OperationSignature Signature,
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> Handler)> _handlers;

    HostOperationRegistry(SemanticCatalog catalog,
        Dictionary<string, (OperationSignature, Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)> handlers)
    {
        Catalog = catalog;
        _handlers = new ReadOnlyDictionary<string,
            (OperationSignature, Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)>(handlers);
    }

    public SemanticCatalog Catalog { get; }

    public static HostOperationRegistry Bind(SemanticCatalog catalog, IEnumerable<HostOperationBinding> bindings)
    {
        if (!TryBind(catalog, bindings, out var registry, out var diagnostics))
            throw new SemanticCompositionException(diagnostics);
        return registry!;
    }

    public static bool TryBind(SemanticCatalog catalog, IEnumerable<HostOperationBinding> bindings,
        out HostOperationRegistry? registry, out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bindings);
        var supplied = bindings.ToArray();
        if (supplied.Any(binding => binding is null))
            throw new ArgumentException("Host bindings cannot contain null.", nameof(bindings));

        var errors = new List<CompositionDiagnostic>();
        var accepted = new Dictionary<string,
            (OperationSignature, Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)>(StringComparer.Ordinal);
        foreach (var group in supplied.GroupBy(binding => binding.Signature.Id, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var incompatibleDuplicate = group.Any(binding => !binding.Signature.Equals(first.Signature) ||
                !ReferenceEquals(binding.Handler, first.Handler));
            if (incompatibleDuplicate)
                errors.Add(new CompositionDiagnostic("NR0004", [],
                    $"Host operation '{group.Key}' has multiple incompatible bindings."));

            foreach (var binding in group)
            {
                if (!catalog.Operations.TryGetValue(group.Key, out var exported))
                    errors.Add(new CompositionDiagnostic("NR0001", [],
                        $"Host operation '{group.Key}' is absent from the semantic catalog."));
                else if (!exported.Equals(binding.Signature))
                    errors.Add(new CompositionDiagnostic("NR0002", [],
                        $"Host operation '{group.Key}' differs from the semantic catalog signature."));
                if (binding.Handler is not Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)
                    errors.Add(new CompositionDiagnostic("NR0003", [],
                        $"Host operation '{group.Key}' needs a numeric execution handler."));
            }
            if (!incompatibleDuplicate)
            {
                if (catalog.Operations.TryGetValue(group.Key, out var exported) &&
                    exported.Equals(first.Signature) &&
                    first.Handler is Func<IReadOnlyList<ExecutionValue>, ExecutionValue> handler)
                    accepted.Add(group.Key, (first.Signature, handler));
            }
        }
        diagnostics = Array.AsReadOnly(errors.OrderBy(error => error.Code, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal).Distinct().ToArray());
        registry = errors.Count == 0 ? new HostOperationRegistry(catalog, accepted) : null;
        return registry is not null;
    }

    internal bool TryGet(string operationId, out OperationSignature signature,
        out Func<IReadOnlyList<ExecutionValue>, ExecutionValue> handler)
    {
        if (_handlers.TryGetValue(operationId, out var found))
        {
            signature = found.Signature;
            handler = found.Handler;
            return true;
        }
        signature = null!;
        handler = null!;
        return false;
    }
}
