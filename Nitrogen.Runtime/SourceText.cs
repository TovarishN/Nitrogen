namespace Nitrogen;

/// <summary>Source text with lazily computed line starts for diagnostics.</summary>
public sealed class SourceText
{
    int[]? _lineStarts;

    public SourceText(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Text { get; }

    /// <summary>One-based line and column of <paramref name="position"/>; the end of the text is valid.</summary>
    public (int Line, int Column) GetLineColumn(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);
        var starts = _lineStarts ??= ComputeLineStarts(Text);
        int line = Array.BinarySearch(starts, position);
        if (line < 0) line = ~line - 1;
        return (line + 1, position - starts[line] + 1);
    }

    static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
}
