using Nitrogen.Semantics;

namespace Nitrogen.LanguageService;

/// <summary>
/// What the service needs from a language's semantics (issue 239):
/// <list type="bullet">
/// <item>its hover property;</item>
/// <item>its expected-type property;</item>
/// <item>the symbol property named like the hover one, which is a symbol's type;</item>
/// <item>an <c>out</c> property named <c>Constant</c>, shown after the type on hover (issue 240).</item>
/// </list>
/// </summary>
internal sealed record SemanticsInfo(bool HasSemantics, Property? Hover, Property? Expected, SymbolProperty? SymbolType, Property? Constant)
{
    public static SemanticsInfo Of(Language language)
    {
        var properties = language.Modules.SelectMany(m => m.Properties).ToList();
        var symbolProperties = language.Modules.SelectMany(m => m.SymbolProperties).ToList();
        var hover = properties.FirstOrDefault(p => p.IsHover);
        return new SemanticsInfo(
            language.Modules.Any(m => m.HasSemantics) || properties.Count > 0 || symbolProperties.Count > 0,
            hover,
            properties.FirstOrDefault(p => p.IsExpected),
            hover is null ? null : symbolProperties.FirstOrDefault(p => p.Name == hover.Name),
            properties.FirstOrDefault(p => p.Name == "Constant" && !p.IsInherited));
    }
}
