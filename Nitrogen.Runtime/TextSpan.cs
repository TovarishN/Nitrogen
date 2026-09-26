namespace Nitrogen;

/// <summary>A half-open character range <c>[Start, Start + Length)</c> in the source text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public override string ToString() => $"[{Start}..{End})";
}
