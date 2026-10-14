using Nitrogen;

namespace DateCalc.Syntax;

/// <summary>Structural matching and construction through the C# API, without evaluating the input.</summary>
public static class PatternMatching
{
    /// <summary>
    /// Swaps the operands of an addition. The caller owns a successful result and binds/checks it in its
    /// destination context. Keep input syntax alive until this method finishes copying captured operands.
    /// </summary>
    public static bool TrySwapAddition(Language language, QuoteNode input, out QuoteInstantiation? result)
    {
        result = null;
        var captures = new Dictionary<string, QuoteHole>
        {
            ["Left"] = QuoteHole.Node("DateCalc.Expr"),
            ["Right"] = QuoteHole.Node("DateCalc.Expr"),
        };
        using var pattern = language.CompilePattern("DateCalc.Expr", "$Left + $Right", captures);
        if (!pattern.Success) return false;
        var match = pattern.Pattern!.Match(input);
        if (!match.Matched) return false;
        using var replacement = language.CompileQuote("DateCalc.Expr", "$Right + $Left", captures);
        if (!replacement.Success) return false;
        var constructed = replacement.Template!.Instantiate(match.Captures);
        if (!constructed.Success)
        {
            constructed.Dispose();
            return false;
        }
        result = constructed;
        return true;
    }
}
