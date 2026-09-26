using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>Checks every node and input before a host operation can run.</summary>
public static class HirPreflight
{
    public static IReadOnlyList<ExecutionDiagnostic> Check(HirNode root, HostOperationRegistry registry,
        IReadOnlyDictionary<Symbol, ExecutionValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(inputs);
        var errors = new List<ExecutionDiagnostic>();
        foreach (var node in HirTraversal.PreOrder(root))
        {
            var origin = node.Origins[0];
            switch (node)
            {
                case HirConstant constant:
                    if (!IsExecutable(constant.Type) || !float.IsFinite(constant.Value))
                        errors.Add(new ExecutionDiagnostic("NE0001", origin,
                            "Constant is outside the finite numeric execution slice."));
                    break;
                case HirSymbolRef reference:
                    if (!TryGetInput(inputs, reference.Symbol.Binding, out var input) || input is null)
                        errors.Add(new ExecutionDiagnostic("NE0002", origin,
                            $"No input was supplied for '{reference.Symbol.Binding.Name}'."));
                    else if (!IsExecutable(reference.Type) || !input.Type.Equals(reference.Type))
                        errors.Add(new ExecutionDiagnostic("NE0003", origin,
                            $"Input for '{reference.Symbol.Binding.Name}' has the wrong type."));
                    break;
                case HirOperation operation:
                    if (!registry.Catalog.Operations.TryGetValue(operation.Signature.Id, out var exported) ||
                        !exported.Equals(operation.Signature))
                        errors.Add(new ExecutionDiagnostic("NE0004", origin,
                            $"Operation '{operation.Signature.Id}' differs from the catalog."));
                    else if (!IsExecutable(operation.Type) || operation.Signature.Inputs.Any(type => !IsExecutable(type)))
                        errors.Add(new ExecutionDiagnostic("NE0001", origin,
                            $"Operation '{operation.Signature.Id}' is outside the numeric execution slice."));
                    else if (!registry.TryGet(operation.Signature.Id, out _, out _))
                        errors.Add(new ExecutionDiagnostic("NE0005", origin,
                            $"Operation '{operation.Signature.Id}' has no bound host handler."));
                    break;
                default:
                    errors.Add(new ExecutionDiagnostic("NE0001", origin,
                        "HIR node is outside the numeric execution slice."));
                    break;
            }
        }
        return Array.AsReadOnly(errors.ToArray());
    }

    static bool IsExecutable(SemanticType type) =>
        type.Equals(SemanticTypes.Scalar) || type.Equals(SemanticTypes.Angle);

    static bool TryGetInput(IReadOnlyDictionary<Symbol, ExecutionValue> inputs, Symbol symbol,
        out ExecutionValue? value)
    {
        foreach (var input in inputs)
        {
            if (!ReferenceEquals(input.Key, symbol)) continue;
            value = input.Value;
            return true;
        }
        value = null;
        return false;
    }
}
