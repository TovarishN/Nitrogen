namespace Nitrogen;

/// <summary>Trivia skippers: <c>(text, position) → first non-trivia position</c>.</summary>
public static class StandardTrivia
{
    /// <summary>Whitespace, <c>// line</c> and <c>/* block */</c> comments. An unterminated block comment runs to end of input.</summary>
    public static int WhitespaceAndComments(ReadOnlySpan<char> text, int position)
    {
        int i = position;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    int close = text.Slice(i + 2).IndexOf("*/");
                    i = close < 0 ? text.Length : i + 2 + close + 2;
                    continue;
                }
            }
            break;
        }
        return i;
    }

    public static int None(ReadOnlySpan<char> text, int position) => position;

    /// <summary>
    /// Every character <see cref="WhitespaceAndComments"/> can start at: ASCII whitespace
    /// (char.IsWhiteSpace includes U+001C..U+001F), '/', and any non-ASCII character.
    /// </summary>
    public static AsciiSet WhitespaceAndCommentsStart { get; } =
        AsciiSet.Of("\t\n\v\f\r\u001c\u001d\u001e\u001f /").WithNonAscii();
}
