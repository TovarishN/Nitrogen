using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>A semantic view of a declaration resolved by Nitrogen's existing binder.</summary>
public sealed record SemanticSymbol(Symbol Binding, string Module, SemanticType? Type)
{
    public static SemanticSymbol From(Symbol binding, string module, SemanticType? type)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        return new SemanticSymbol(binding, module, type);
    }

    public string Id => Module + ":" + Binding.Kind + ":" + Binding.Name;
}
