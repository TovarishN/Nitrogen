namespace Nitrogen.Grammar;

/// <summary>A half-open character range <c>[Start, Start + Length)</c> in a grammar file.</summary>
public readonly record struct GrammarSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static GrammarSpan FromBounds(int start, int end) => new(start, end - start);

    public override string ToString() => $"[{Start}..{End})";
}
