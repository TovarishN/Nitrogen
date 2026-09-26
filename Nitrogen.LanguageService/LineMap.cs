namespace Nitrogen.LanguageService;

/// <summary>
/// Text offsets and LSP positions (0-based line, UTF-16 column) over one text. A line ends before
/// its <c>\n</c>, <c>\r\n</c> or <c>\r</c>; positions past a line's end or the last line clamp.
/// </summary>
public sealed class LineMap
{
    readonly int[] _starts;
    readonly string _text;

    public LineMap(string text)
    {
        _text = text;
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        _starts = starts.ToArray();
    }

    public int LineCount => _starts.Length;

    /// <summary>A line's length without its line break.</summary>
    public int LineLength(int line) => LineEnd(line) - _starts[line];

    public DocumentPosition PositionOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        int line = Array.BinarySearch(_starts, offset);
        if (line < 0) line = ~line - 1;
        return new DocumentPosition(line, offset - _starts[line]);
    }

    public int OffsetOf(DocumentPosition position)
    {
        if (position.Line < 0) return 0;
        if (position.Line >= _starts.Length) return _text.Length;
        int start = _starts[position.Line];
        return start + Math.Clamp(position.Character, 0, LineEnd(position.Line) - start);
    }

    public DocumentRange RangeOf(TextSpan span) => new(PositionOf(span.Start), PositionOf(span.End));

    /// <summary>The offset where a line's text ends, before its line break.</summary>
    int LineEnd(int line)
    {
        int end = line + 1 < _starts.Length ? _starts[line + 1] : _text.Length;
        while (end > _starts[line] && (_text[end - 1] == '\n' || _text[end - 1] == '\r')) end--;
        return end;
    }
}
