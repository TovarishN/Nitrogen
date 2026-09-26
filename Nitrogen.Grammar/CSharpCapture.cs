namespace Nitrogen.Grammar;

/// <summary>
/// Where a C# fragment in a semantics block ends (issue 239): at the first stop character outside
/// brackets, strings, chars and comments. The grammar compiler never parses the C#. The rules are
/// exactly those of the self-hosted grammar's Cs* tokens: brackets must match, and an unbalanced or
/// mismatched bracket, or an unterminated string or char, ends the fragment where it starts.
/// </summary>
internal static class CSharpCapture
{
    /// <summary>A value, default or message: up to <c>;</c>.</summary>
    public const string Statement = ";";

    /// <summary>A check's condition: up to <c>:</c> (or <c>;</c>).</summary>
    public const string Condition = ";:";

    /// <summary>A property's type: up to <c>=</c> (or <c>;</c>).</summary>
    public const string Type = ";=";

    /// <returns>The offset of the first top-level stop character, closer, or broken literal or group; else the text's end.</returns>
    public static int Scan(string text, int start, string stops)
    {
        int i = start;
        while (i < text.Length)
        {
            char c = text[i];
            if (stops.IndexOf(c) >= 0 || IsCloser(c)) return i;
            int next = Atom(text, i);
            if (next < 0) return i;
            i = next;
        }
        return i;
    }

    /// <summary>One group, string, char or comment starting at <paramref name="i"/>, else one character; -1 when broken.</summary>
    static int Atom(string text, int i)
    {
        char c = text[i];
        if (c == '(' || c == '[' || c == '{') return Group(text, i);
        int verbatim = VerbatimQuote(text, i);
        if (verbatim >= 0) return Verbatim(text, verbatim + 1);
        if (c == '"') return Regular(text, i + 1);
        if (c == '$' && At(text, i + 1, '"')) return Regular(text, i + 2);
        if (c == '\'') return Char(text, i + 1);
        if (c == '/' && At(text, i + 1, '/'))
        {
            int end = text.IndexOf('\n', i);
            return end < 0 ? text.Length : end;
        }
        if (c == '/' && At(text, i + 1, '*'))
        {
            int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return close < 0 ? i + 1 : close + 2;
        }
        return i + 1;
    }

    static int Group(string text, int i)
    {
        char close = text[i] == '(' ? ')' : text[i] == '[' ? ']' : '}';
        i++;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == close) return i + 1;
            if (IsCloser(c)) return -1;
            int next = Atom(text, i);
            if (next < 0) return -1;
            i = next;
        }
        return -1;
    }

    /// <summary>The index of the quote of <c>@"</c>, <c>$@"</c> or <c>@$"</c> at <paramref name="i"/>; -1 when none.</summary>
    static int VerbatimQuote(string text, int i)
    {
        if (At(text, i, '@') && At(text, i + 1, '"')) return i + 1;
        bool pair = (At(text, i, '$') && At(text, i + 1, '@')) || (At(text, i, '@') && At(text, i + 1, '$'));
        return pair && At(text, i + 2, '"') ? i + 2 : -1;
    }

    static int Verbatim(string text, int i)
    {
        while (i < text.Length)
        {
            if (text[i] != '"')
            {
                i++;
                continue;
            }
            if (!At(text, i + 1, '"')) return i + 1;
            i += 2;
        }
        return -1;
    }

    static int Regular(string text, int i)
    {
        while (i < text.Length && text[i] != '"' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
        return i < text.Length && text[i] == '"' ? i + 1 : -1;
    }

    static int Char(string text, int i)
    {
        int start = i;
        while (i < text.Length && text[i] != '\'' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
        return i > start && i < text.Length && text[i] == '\'' ? i + 1 : -1;
    }

    static bool IsCloser(char c) => c == ')' || c == ']' || c == '}';

    static bool At(string text, int i, char c) => i < text.Length && text[i] == c;
}
