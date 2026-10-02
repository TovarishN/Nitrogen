using System.Collections.ObjectModel;

namespace Nitrogen.Semantic;

public sealed record CompositionDiagnostic(string Code, IReadOnlyList<string> Modules, string Message);

public sealed class SemanticCompositionException(IReadOnlyList<CompositionDiagnostic> diagnostics)
    : Exception(string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.Message)))
{
    public IReadOnlyList<CompositionDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Frozen semantic imports and exports for one language.</summary>
public sealed class SemanticCatalog
{
    SemanticCatalog(Dictionary<string, SemanticType> types, Dictionary<string, OperationSignature> operations,
        Dictionary<int, IReadOnlyList<LoweringRegistration>> lowerers)
    {
        Types = new ReadOnlyDictionary<string, SemanticType>(types);
        Operations = new ReadOnlyDictionary<string, OperationSignature>(operations);
        _lowerers = new ReadOnlyDictionary<int, IReadOnlyList<LoweringRegistration>>(lowerers);
    }

    public IReadOnlyDictionary<string, SemanticType> Types { get; }
    public IReadOnlyDictionary<string, OperationSignature> Operations { get; }
    readonly IReadOnlyDictionary<int, IReadOnlyList<LoweringRegistration>> _lowerers;
    public IReadOnlyList<LoweringRegistration> LowerersFor(int syntaxKind) =>
        _lowerers.TryGetValue(syntaxKind, out var found) ? found : [];

    public static SemanticCatalog? Compose(IEnumerable<SemanticModule> modules,
        out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var all = modules.Append(new SemanticModule("Core", [],
            [SemanticTypes.Scalar, SemanticTypes.Bool, SemanticTypes.Text, SemanticTypes.Error], [])).ToArray();
        var groups = all.GroupBy(module => module.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var errors = new List<CompositionDiagnostic>();
        var imports = groups.ToDictionary(pair => pair.Key,
            pair => pair.Value.SelectMany(module => module.Imports).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        foreach (var (owner, dependencies) in imports.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            foreach (var dependency in dependencies)
                if (!groups.ContainsKey(dependency))
                    errors.Add(new CompositionDiagnostic("NC0001", [owner, dependency],
                        $"Semantic module '{owner}' imports missing module '{dependency}'."));

        var active = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();
        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            if (active.Contains(name))
            {
                int start = stack.IndexOf(name);
                var cycle = stack.Skip(start).Append(name).ToArray();
                errors.Add(new CompositionDiagnostic("NC0002", cycle,
                    "Semantic import cycle: " + string.Join(" -> ", cycle)));
                return;
            }
            active.Add(name);
            stack.Add(name);
            foreach (var dependency in imports[name])
                if (groups.ContainsKey(dependency)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            active.Remove(name);
            visited.Add(name);
        }
        foreach (var name in groups.Keys.Order(StringComparer.Ordinal)) Visit(name);

        var types = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        var typeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var operations = new Dictionary<string, OperationSignature>(StringComparer.Ordinal);
        var operationOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var lowererOwners = new Dictionary<(int Kind, string Operation), string>();
        var lowerers = new Dictionary<int, List<LoweringRegistration>>();
        foreach (var module in all.OrderBy(module => module.Name, StringComparer.Ordinal))
        {
            foreach (var type in module.Types.OrderBy(type => type.Id, StringComparer.Ordinal))
            {
                if (types.TryGetValue(type.Id, out var previous) && !previous.Equals(type))
                    errors.Add(new CompositionDiagnostic("NC0003", [typeOwners[type.Id], module.Name],
                        $"Semantic type '{type.Id}' differs between '{typeOwners[type.Id]}' and '{module.Name}'."));
                else if (previous is null) { types.Add(type.Id, type); typeOwners.Add(type.Id, module.Name); }
            }
            foreach (var operation in module.Operations.OrderBy(operation => operation.Id, StringComparer.Ordinal))
            {
                if (operations.TryGetValue(operation.Id, out var previous) && !previous.Equals(operation))
                    errors.Add(new CompositionDiagnostic("NC0004", [operationOwners[operation.Id], module.Name],
                        $"Semantic operation '{operation.Id}' differs between '{operationOwners[operation.Id]}' and '{module.Name}'."));
                else if (previous is null) { operations.Add(operation.Id, operation); operationOwners.Add(operation.Id, module.Name); }
            }
            foreach (var lowerer in module.Lowerers.OrderBy(lowerer => lowerer.SyntaxKind)
                         .ThenBy(lowerer => lowerer.OperationId, StringComparer.Ordinal))
            {
                var key = (lowerer.SyntaxKind, lowerer.OperationId);
                if (lowererOwners.TryGetValue(key, out var owner))
                    errors.Add(new CompositionDiagnostic("NC0005", [owner, module.Name],
                        $"Semantic lowerer for kind {key.SyntaxKind} and '{key.OperationId}' is registered by '{owner}' and '{module.Name}'."));
                else
                {
                    lowererOwners.Add(key, module.Name);
                    if (!lowerers.TryGetValue(key.SyntaxKind, out var entries)) lowerers[key.SyntaxKind] = entries = [];
                    entries.Add(lowerer);
                }
            }
        }
        diagnostics = errors;
        return errors.Count == 0 ? new SemanticCatalog(types, operations,
            lowerers.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<LoweringRegistration>)Array.AsReadOnly(pair.Value.ToArray()))) : null;
    }
}
