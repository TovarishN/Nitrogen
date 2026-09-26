using System.Collections.ObjectModel;
using Nitrogen.Semantic;

namespace Nitrogen.Workspace.Admission;

/// <summary>The effects supported by reviewed local capability grants.</summary>
public enum CapabilityEffect { Pure }

/// <summary>An exact host-owned operation grant.</summary>
public sealed record HostCapability(OperationSignature Signature, HostOperationBinding Binding,
    CapabilityEffect Effect)
{
    public string Id => Signature.Id;
}

/// <summary>An immutable catalog of capabilities available to one host profile.</summary>
public sealed class HostCapabilitySet
{
    readonly IReadOnlyDictionary<string, HostCapability> _entries;
    readonly IReadOnlyList<HostOperationBinding> _bindings;

    public HostCapabilitySet(IEnumerable<HostCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var entries = new Dictionary<string, HostCapability>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (capability is null || capability.Signature is null || capability.Binding is null ||
                capability.Effect != CapabilityEffect.Pure ||
                !capability.Signature.Equals(capability.Binding.Signature) ||
                !entries.TryAdd(capability.Id, capability))
                throw new ArgumentException("Capability grants must be unique, pure, and have exact bindings.",
                    nameof(capabilities));
        }
        _entries = new ReadOnlyDictionary<string, HostCapability>(entries);
        _bindings = Array.AsReadOnly(entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.Binding).ToArray());
    }

    public bool TryGet(string id, out HostCapability capability) => _entries.TryGetValue(id, out capability!);

    internal IReadOnlyList<HostOperationBinding> AllBindings => _bindings;
}
