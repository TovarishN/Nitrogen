using System.Collections.ObjectModel;
using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>A validated language and its registered entry points and host operations.</summary>
public sealed class ModuleComposition
{
    internal ModuleComposition(Language language, Dictionary<(string Module, string Name), Rule> startRules,
        Dictionary<string, HostOperationBinding> hostBindings)
    {
        Language = language;
        StartRules = new ReadOnlyDictionary<(string Module, string Name), Rule>(startRules);
        HostBindings = new ReadOnlyDictionary<string, HostOperationBinding>(hostBindings);
    }

    public Language Language { get; }
    public IReadOnlyDictionary<(string Module, string Name), Rule> StartRules { get; }
    public IReadOnlyDictionary<string, HostOperationBinding> HostBindings { get; }
}

/// <summary>Composes C# registered Nitrogen modules without changing existing builder callers.</summary>
public static class ModuleComposer
{
    public static ModuleComposition Compose(IEnumerable<ModuleDescriptor> descriptors,
        IEnumerable<HostOperationBinding> bindings)
    {
        if (!TryCompose(descriptors, bindings, out var result, out var diagnostics))
            throw new SemanticCompositionException(diagnostics);
        return result!;
    }

    public static bool TryCompose(IEnumerable<ModuleDescriptor> descriptors,
        IEnumerable<HostOperationBinding> bindings, out ModuleComposition? result,
        out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(bindings);
        var all = descriptors.ToArray();
        if (all.Any(descriptor => descriptor is null))
            throw new ArgumentException("Descriptors cannot contain null.", nameof(descriptors));

        var errors = new List<CompositionDiagnostic>();
        foreach (var duplicate in all.GroupBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal))
            errors.Add(new CompositionDiagnostic("NM0001", [duplicate.Key],
                $"Module descriptor ID '{duplicate.Key}' is registered more than once."));
        if (errors.Count > 0)
        {
            result = null;
            diagnostics = errors;
            return false;
        }

        var builder = new LanguageBuilder();
        var starts = new Dictionary<(string Module, string Name), Rule>();
        foreach (var descriptor in all.OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal))
        {
            if (descriptor.Syntax is { } syntax) builder.Add(syntax);
            if (descriptor.Semantics is { } semantics) builder.AddSemantic(semantics);
            foreach (var name in descriptor.StartRules.Order(StringComparer.Ordinal))
            {
                var rule = descriptor.Syntax?.GetRule(name);
                if (rule is null)
                    errors.Add(new CompositionDiagnostic("NM0002", [descriptor.Id],
                        $"Module '{descriptor.Id}' has no start rule '{name}'."));
                else
                    starts.TryAdd((descriptor.Id, name), rule.Value);
            }
        }

        Language? language;
        IReadOnlyList<CompositionDiagnostic> semanticDiagnostics;
        bool built;
        try
        {
            built = builder.TryBuild(out language, out semanticDiagnostics);
        }
        catch (LanguageCompositionException error)
        {
            var contributors = all.Where(descriptor => descriptor.Syntax is not null &&
                    error.Message.Contains($"'{descriptor.Syntax.Name}'", StringComparison.Ordinal))
                .Select(descriptor => descriptor.Id).Order(StringComparer.Ordinal).ToArray();
            errors.Add(new CompositionDiagnostic("NM0007", contributors, error.Message));
            result = null;
            diagnostics = errors.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal).ToArray();
            return false;
        }
        errors.AddRange(semanticDiagnostics);
        if (!built)
        {
            result = null;
            diagnostics = errors.OrderBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal).ToArray();
            return false;
        }

        var supplied = bindings.ToArray();
        if (supplied.Any(binding => binding is null))
            throw new ArgumentException("Host bindings cannot contain null.", nameof(bindings));
        var hosts = new Dictionary<string, HostOperationBinding>(StringComparer.Ordinal);
        var duplicateHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in supplied.GroupBy(binding => binding.Signature.Id, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(binding => !binding.Signature.Equals(first.Signature) ||
                                     !ReferenceEquals(binding.Handler, first.Handler)))
            {
                errors.Add(new CompositionDiagnostic("NM0006", [],
                    $"Host operation '{group.Key}' has multiple incompatible bindings."));
                duplicateHosts.Add(group.Key);
            }
            else hosts.Add(group.Key, first);
        }

        var requirements = all.SelectMany(descriptor => descriptor.RequiredOperations.Select(signature =>
            (Owner: descriptor.Id, Signature: signature)));
        foreach (var group in requirements.GroupBy(requirement => requirement.Signature.Id, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First().Signature;
            var owners = group.Select(requirement => requirement.Owner).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (group.Any(requirement => !requirement.Signature.Equals(first)))
                errors.Add(new CompositionDiagnostic("NM0005", owners,
                    $"Modules require incompatible signatures for operation '{group.Key}'."));
            else if (!language!.SemanticCatalog.Operations.TryGetValue(group.Key, out var exported))
                errors.Add(new CompositionDiagnostic("NM0003", owners,
                    $"Required operation '{group.Key}' is not exported by the semantic catalog."));
            else if (!exported.Equals(first))
                errors.Add(new CompositionDiagnostic("NM0005", owners,
                    $"Required operation '{group.Key}' differs from its semantic export."));
            else if (!hosts.TryGetValue(group.Key, out var host))
            {
                if (!duplicateHosts.Contains(group.Key))
                    errors.Add(new CompositionDiagnostic("NM0004", owners,
                        $"Required operation '{group.Key}' has no host binding."));
            }
            else if (!host.Signature.Equals(first))
                errors.Add(new CompositionDiagnostic("NM0005", owners,
                    $"Host binding for '{group.Key}' has an incompatible signature."));
        }
        if (errors.Count > 0)
        {
            result = null;
            diagnostics = errors.OrderBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal).ToArray();
            return false;
        }

        result = new ModuleComposition(language!, starts, hosts);
        diagnostics = [];
        return true;
    }
}
