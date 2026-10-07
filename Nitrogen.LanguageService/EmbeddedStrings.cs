using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nitrogen.LanguageService;

/// <summary>
/// A C# string literal tagged with a language: its tag, its value, and where each value character
/// came from. <see cref="Map"/> has one source offset per value character plus one for the end, so
/// a value span [s, e) is the source span [Map[s], Map[e]). <see cref="End"/> is the source offset just
/// past the literal (its closing quote, and a <c>u8</c> suffix).
/// </summary>
internal sealed record EmbeddedString(string Tag, string Value, int[] Map, int End)
{
    /// <summary>The value offset at a source offset, or null when the offset is outside the value.</summary>
    public int? ValueOffset(int source)
    {
        if (source < Map[0] || source > Map[^1]) return null;
        int index = Array.BinarySearch(Map, source);
        return index >= 0 ? index : ~index; // inside an escape: the character it spells
    }
}

/// <summary>
/// Finds tagged string literals in C# text (the convention Rider and Visual Studio use for language
/// injection): <c>/*lang=calc*/ "1 + 2;"</c> or <c>/* language=calc */</c> right before the literal, or a
/// <c>// language=calc</c> line comment before the statement whose first string literal it tags. Regular,
/// verbatim and raw literals are supported; interpolated ones are skipped. The scan is lexical: it skips
/// comments, character literals and strings, so quotes inside them start nothing.
/// </summary>
internal static partial class EmbeddedStrings
{
    [GeneratedRegex(@"^\s*(?:lang|language)\s*=\s*([A-Za-z0-9_.+\-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();

    public static List<EmbeddedString> Find(string text)
    {
        var found = new List<EmbeddedString>();
        string? inlineTag = null;   // a block comment tag, for the literal right after it
        string? lineTag = null;     // a line comment tag, until the end of the next statement
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && At(text, i + 1, '/'))
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                if (Tag(text[(i + 2)..end]) is { } tag) lineTag = tag;
                inlineTag = null;
                i = end;
                continue;
            }
            if (c == '/' && At(text, i + 1, '*'))
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 2;
                inlineTag = Tag(text[(i + 2)..(close < 0 ? text.Length : close)]);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                i = SkipChar(text, i);
                inlineTag = null;
                continue;
            }
            if (Literal(text, i) is { } literal)
            {
                string? tag = inlineTag ?? lineTag;
                if (tag is not null && literal.Value is { } value) found.Add(new EmbeddedString(tag, value.Text, value.Map, literal.End));
                inlineTag = null;
                lineTag = null;
                i = literal.End;
                continue;
            }
            if (c is ';' or '{' or '}') lineTag = null;
            inlineTag = null;
            i++;
        }
        return found;
    }

    static string? Tag(string comment) => TagPattern().Match(comment) is { Success: true } match ? match.Groups[1].Value : null;

    static bool At(string text, int index, char c) => index < text.Length && text[index] == c;

    static int SkipChar(string text, int i)
    {
        int j = i + 1;
        while (j < text.Length && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
        return Math.Min(j + 1, text.Length);
    }

    /// <summary>A string literal starting at <paramref name="i"/>: its end, and its value unless it is interpolated.</summary>
    static (int End, (string Text, int[] Map)? Value)? Literal(string text, int i)
    {
        int j = i, dollars = 0;
        bool verbatim = false;
        while (j < text.Length && (text[j] == '$' || text[j] == '@'))
        {
            if (text[j] == '$') dollars++;
            else verbatim = true;
            j++;
        }
        if (!At(text, j, '"')) return null;

        int quotes = 0;
        while (At(text, j + quotes, '"')) quotes++;
        if (quotes >= 3) return Raw(text, j, quotes, dollars > 0);
        if (verbatim) return Verbatim(text, j, dollars > 0);
        return Regular(text, j, dollars > 0);
    }

    static (int End, (string, int[])? Value) Regular(string text, int open, bool interpolated)
    {
        var value = new StringBuilder();
        var map = new List<int>();
        int j = open + 1, depth = 0;
        while (j < text.Length && text[j] != '\n')
        {
            char c = text[j];
            if (interpolated && c == '{' && !At(text, j + 1, '{')) depth++;
            else if (interpolated && c == '}' && depth > 0) depth--;
            if (c == '"' && depth == 0) break;
            if (c == '\\' && j + 1 < text.Length)
            {
                int start = j;
                var (decoded, length) = Escape(text, j);
                foreach (char d in decoded)
                {
                    value.Append(d);
                    map.Add(start);
                }
                j += length;
                continue;
            }
            value.Append(c);
            map.Add(j);
            j++;
        }
        map.Add(j);
        return (Suffix(text, j + 1), interpolated ? null : (value.ToString(), map.ToArray()));
    }

    static (int End, (string, int[])? Value) Verbatim(string text, int open, bool interpolated)
    {
        var value = new StringBuilder();
        var map = new List<int>();
        int j = open + 1;
        while (j < text.Length)
        {
            if (text[j] == '"')
            {
                if (!At(text, j + 1, '"')) break;
                value.Append('"');
                map.Add(j);
                j += 2;
                continue;
            }
            value.Append(text[j]);
            map.Add(j);
            j++;
        }
        map.Add(j);
        return (Suffix(text, j + 1), interpolated ? null : (value.ToString(), map.ToArray()));
    }

    /// <summary>A raw literal: on one line its text between the quotes; on several, its lines between the delimiter lines, less the closing line's indentation.</summary>
    static (int End, (string, int[])? Value) Raw(string text, int open, int quotes, bool interpolated)
    {
        string delimiter = new('"', quotes);
        int start = open + quotes;
        int close = text.IndexOf(delimiter, start, StringComparison.Ordinal);
        if (close < 0) close = text.Length;
        int end = Suffix(text, Math.Min(close + quotes, text.Length));
        if (interpolated) return (end, null);

        int firstBreak = text.IndexOf('\n', start, close - start);
        if (firstBreak < 0) return (end, (text[start..close], Enumerable.Range(start, close - start + 1).ToArray()));

        int lastBreak = text.LastIndexOf('\n', close - 1, close - start);
        int indent = close - lastBreak - 1; // the closing delimiter's indentation
        var value = new StringBuilder();
        var map = new List<int>();
        int line = firstBreak + 1;
        while (line <= lastBreak)
        {
            int lineEnd = text.IndexOf('\n', line);
            int from = line;
            for (int k = 0; k < indent && from < lineEnd && text[from] is ' ' or '\t'; k++) from++;
            int stop = lineEnd > line && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            for (int k = from; k < stop; k++)
            {
                value.Append(text[k]);
                map.Add(k);
            }
            if (lineEnd < lastBreak)
            {
                value.Append('\n');
                map.Add(stop);
            }
            line = lineEnd + 1;
        }
        map.Add(value.Length == 0 ? lastBreak : map[^1] + 1);
        return (end, (value.ToString(), map.ToArray()));
    }

    /// <summary>After a closing quote, a <c>u8</c> suffix belongs to the literal.</summary>
    static int Suffix(string text, int j) =>
        j + 1 < text.Length && (text[j] == 'u' || text[j] == 'U') && text[j + 1] == '8' ? j + 2 : Math.Min(j, text.Length);

    static (string Text, int Length) Escape(string text, int j)
    {
        char e = text[j + 1];
        switch (e)
        {
            case 'n': return ("\n", 2);
            case 't': return ("\t", 2);
            case 'r': return ("\r", 2);
            case '0': return ("\0", 2);
            case 'a': return ("\a", 2);
            case 'b': return ("\b", 2);
            case 'f': return ("\f", 2);
            case 'v': return ("\v", 2);
            case 'e': return ("\u001b", 2);
            case 'u' or 'x' or 'U':
            {
                int max = e == 'u' ? 4 : e == 'U' ? 8 : 4, k = j + 2;
                while (k < text.Length && k - j - 2 < max && Uri.IsHexDigit(text[k])) k++;
                if (k == j + 2) return (e.ToString(), 2);
                int code = int.Parse(text.AsSpan(j + 2, k - j - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                string decoded = code is >= 0xD800 and <= 0xDFFF ? ((char)code).ToString()
                    : code <= 0x10FFFF ? char.ConvertFromUtf32(code) : "�";
                return (decoded, k - j);
            }
            default: return (e.ToString(), 2);
        }
    }
}
