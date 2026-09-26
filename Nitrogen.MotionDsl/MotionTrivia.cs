namespace Nitrogen.MotionDsl;

/// <summary>MotionLexer's trivia: whitespace and <c>//</c> line comments (no block comments).</summary>
public static class MotionTrivia
{
    public static int Skip(ReadOnlySpan<char> text, int position)
    {
        int i = position;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    /// <summary>Every character <see cref="Skip"/> can start at: ASCII whitespace, '/', any non-ASCII character.</summary>
    public static AsciiSet StartChars { get; } = AsciiSet.Of("\t\n\v\f\r\u001c\u001d\u001e\u001f /").WithNonAscii();
}
