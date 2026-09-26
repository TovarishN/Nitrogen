using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>Evaluates the finite numeric HIR slice through prebound host handlers.</summary>
public static class HirEvaluator
{
    public static ExecutionResult Evaluate(HirNode root, HostOperationRegistry registry,
        IReadOnlyDictionary<Symbol, ExecutionValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var inputSnapshot = new Dictionary<Symbol, ExecutionValue>(ReferenceEqualityComparer.Instance);
        foreach (var input in inputs) inputSnapshot.Add(input.Key, input.Value);
        var preflight = HirPreflight.Check(root, registry, inputSnapshot);
        if (preflight.Count != 0)
            return new ExecutionResult(null, root.Origins, preflight);
        return EvaluateNode(root, registry, inputSnapshot);
    }

    static ExecutionResult EvaluateNode(HirNode node, HostOperationRegistry registry,
        IReadOnlyDictionary<Symbol, ExecutionValue> inputs)
    {
        switch (node)
        {
            case HirConstant constant:
                return Success(new ExecutionValue(constant.Type, constant.Value), node);
            case HirSymbolRef reference:
                return Success(inputs[reference.Symbol.Binding], node);
            case HirOperation operation:
            {
                var arguments = new ExecutionValue[operation.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                {
                    var child = EvaluateNode(operation.Arguments[i], registry, inputs);
                    if (child.Diagnostics.Count != 0)
                        return new ExecutionResult(null, node.Origins, child.Diagnostics);
                    arguments[i] = child.Value!;
                }

                registry.TryGet(operation.Signature.Id, out _, out var handler);
                try
                {
                    var value = handler(Array.AsReadOnly(arguments));
                    if (value is null || !value.Type.Equals(operation.Signature.Result))
                        return Failure("NE0007", node,
                            "Host result does not match the operation result type.");
                    return Success(value, node);
                }
                catch (Exception error)
                {
                    return Failure("NE0006", node,
                        $"Host operation '{operation.Signature.Id}' failed: {error.GetType().Name}: {error.Message}");
                }
            }
            default:
                throw new InvalidOperationException("Preflight accepted an unknown HIR node.");
        }
    }

    static ExecutionResult Success(ExecutionValue value, HirNode node) =>
        new(value, node.Origins, []);

    static ExecutionResult Failure(string code, HirNode node, string message) =>
        new(null, node.Origins, [new ExecutionDiagnostic(code, node.Origins[0], message)]);
}
