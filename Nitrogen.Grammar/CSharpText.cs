using System.Text;

namespace Nitrogen.Grammar;

/// <summary>C# source text helpers for the emitter.</summary>
internal static class CSharpText
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    public static string Identifier(string name) => Keywords.Contains(name) ? "@" + name : name;

    public static string Literal(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (char c in value) AppendEscaped(builder, c, '"');
        return builder.Append('"').ToString();
    }

    public static string CharLiteral(char value)
    {
        var builder = new StringBuilder("'");
        AppendEscaped(builder, value, '\'');
        return builder.Append('\'').ToString();
    }

    public static string LastSegment(string dotted)
    {
        int dot = dotted.LastIndexOf('.');
        return dot < 0 ? dotted : dotted.Substring(dot + 1);
    }

    static void AppendEscaped(StringBuilder builder, char c, char quote)
    {
        switch (c)
        {
            case '\\': builder.Append("\\\\"); break;
            case '\n': builder.Append("\\n"); break;
            case '\r': builder.Append("\\r"); break;
            case '\t': builder.Append("\\t"); break;
            case '\0': builder.Append("\\0"); break;
            default:
                if (c == quote) builder.Append('\\').Append(c);
                else if (c < ' ' || c > '~') builder.Append("\\u").Append(((int)c).ToString("x4"));
                else builder.Append(c);
                break;
        }
    }
}
