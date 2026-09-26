namespace Nitrogen;

/// <summary>
/// First-character filter for extension dispatch: a 128-bit ASCII mask plus one bit for "any
/// non-ASCII character". The default value is unrestricted and matches everything, including end
/// of input.
/// </summary>
public readonly struct AsciiSet
{
    readonly ulong _low;
    readonly ulong _high;
    readonly bool _restricted;
    readonly bool _nonAscii;

    AsciiSet(ulong low, ulong high, bool nonAscii)
    {
        _low = low;
        _high = high;
        _nonAscii = nonAscii;
        _restricted = true;
    }

    public static AsciiSet Any => default;

    public bool IsAny => !_restricted;

    public static AsciiSet Of(string chars)
    {
        ulong low = 0, high = 0;
        foreach (char c in chars)
        {
            if (c >= 128) throw new ArgumentException($"'{c}' is not ASCII; use WithNonAscii().", nameof(chars));
            if (c < 64) low |= 1UL << c;
            else high |= 1UL << (c - 64);
        }
        return new AsciiSet(low, high, nonAscii: false);
    }

    public static AsciiSet Range(char first, char last)
    {
        if (last >= 128 || first > last) throw new ArgumentException($"'{first}'..'{last}' is not an ASCII range.");
        ulong low = 0, high = 0;
        for (char c = first; c <= last; c++)
        {
            if (c < 64) low |= 1UL << c;
            else high |= 1UL << (c - 64);
        }
        return new AsciiSet(low, high, nonAscii: false);
    }

    public AsciiSet Union(AsciiSet other) =>
        !_restricted || !other._restricted
            ? Any
            : new AsciiSet(_low | other._low, _high | other._high, _nonAscii || other._nonAscii);

    public AsciiSet WithNonAscii() => _restricted ? new AsciiSet(_low, _high, nonAscii: true) : this;

    public bool Matches(char c) =>
        !_restricted
        || (c < 64 ? ((_low >> c) & 1) != 0
            : c < 128 ? ((_high >> (c - 64)) & 1) != 0
            : _nonAscii);

    /// <summary>Whether some character passes both filters; an unrestricted set overlaps everything.</summary>
    public bool Overlaps(AsciiSet other) =>
        !_restricted || !other._restricted
        || (_low & other._low) != 0 || (_high & other._high) != 0 || (_nonAscii && other._nonAscii);
}
