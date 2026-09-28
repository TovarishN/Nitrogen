using System.Globalization;
using System.Text;

namespace Nitrogen.Grammar;

internal enum TokenKind
{
    End,
    Identifier,
    QualifiedName,
    String,
    Char,
    Integer,
    LBrace,
    RBrace,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Semicolon,
    Colon,
    Equals,
    Bar,
    Slash,
    Question,
    Star,
    Plus,
    Bang,
    Amp,
    Dot,
    DotDot,
    Caret,
    Comma,
}

/// <param name="Value">Source text for names, numbers and punctuation; the decoded value for strings and chars.</param>
internal readonly record struct GrammarToken(TokenKind Kind, int Start, int Length, string Value)
{
    public int End => Start + Length;

    public GrammarSpan Span => new(Start, Length);
}

internal sealed class GrammarSyntaxException : Exception
{
    public GrammarSyntaxException(string message, GrammarSpan span) : base(message)
    {
        Span = span;
    }

    public GrammarSpan Span { get; }
}

internal static class GrammarLexer
{
    public static List<GrammarToken> Tokenize(string text)
    {
        var tokens = new List<GrammarToken>();
        int i = 0;
        while (true)
        {
            var token = Next(text, ref i);
            tokens.Add(token);
            if (token.Kind == TokenKind.End) return tokens;
        }
    }

    /// <summary>
    /// The token at or after <paramref name="i"/>, trivia skipped; <paramref name="i"/> moves past it.
    /// The parser lexes on demand (issue 239), so C# in semantics blocks is never tokenized.
    /// </summary>
    public static GrammarToken Next(string text, ref int i)
    {
        i = SkipTrivia(text, i);
        if (i >= text.Length) return new GrammarToken(TokenKind.End, text.Length, 0, "");

        int start = i;
        char c = text[i];
        if (IsIdentifierStart(c))
        {
            i = ScanIdentifier(text, i);
            var kind = TokenKind.Identifier;
            while (i + 1 < text.Length && text[i] == '.' && IsIdentifierStart(text[i + 1]))
            {
                i = ScanIdentifier(text, i + 1);
                kind = TokenKind.QualifiedName;
            }
            return new GrammarToken(kind, start, i - start, text.Substring(start, i - start));
        }
        if (IsDigit(c))
        {
            while (i < text.Length && IsDigit(text[i])) i++;
            return new GrammarToken(TokenKind.Integer, start, i - start, text.Substring(start, i - start));
        }
        if (c == '"')
        {
            i = ScanQuoted(text, i, '"', out string value);
            return new GrammarToken(TokenKind.String, start, i - start, value);
        }
        if (c == '\'')
        {
            i = ScanQuoted(text, i, '\'', out string value);
            if (value.Length != 1)
                throw new GrammarSyntaxException("a character literal must contain exactly one character", GrammarSpan.FromBounds(start, i));
            return new GrammarToken(TokenKind.Char, start, i - start, value);
        }
        if (c == '.' && i + 1 < text.Length && text[i + 1] == '.')
        {
            i += 2;
            return new GrammarToken(TokenKind.DotDot, start, 2, "..");
        }

        var punctuation = c switch
        {
            '{' => TokenKind.LBrace,
            '}' => TokenKind.RBrace,
            '(' => TokenKind.LParen,
            ')' => TokenKind.RParen,
            '[' => TokenKind.LBracket,
            ']' => TokenKind.RBracket,
            ';' => TokenKind.Semicolon,
            ':' => TokenKind.Colon,
            '=' => TokenKind.Equals,
            '|' => TokenKind.Bar,
            '/' => TokenKind.Slash,
            '?' => TokenKind.Question,
            '*' => TokenKind.Star,
            '+' => TokenKind.Plus,
            '!' => TokenKind.Bang,
            '&' => TokenKind.Amp,
            '.' => TokenKind.Dot,
            '^' => TokenKind.Caret,
            ',' => TokenKind.Comma,
            _ => TokenKind.End,
        };
        if (punctuation == TokenKind.End)
            throw new GrammarSyntaxException($"unexpected character '{c}'", new GrammarSpan(i, 1));
        i++;
        return new GrammarToken(punctuation, start, 1, c.ToString());
    }

    internal static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new GrammarSyntaxException("unterminated comment", GrammarSpan.FromBounds(i, text.Length));
                i = close + 2;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    static int ScanIdentifier(string text, int i)
    {
        i++;
        while (i < text.Length && IsIdentifierPart(text[i])) i++;
        return i;
    }

    static int ScanQuoted(string text, int start, char quote, out string value)
    {
        var builder = new StringBuilder();
        int i = start + 1;
        while (true)
        {
            if (i >= text.Length || text[i] == '\n')
                throw new GrammarSyntaxException("unterminated literal", GrammarSpan.FromBounds(start, i));
            char c = text[i];
            if (c == quote)
            {
                value = builder.ToString();
                return i + 1;
            }
            if (c != '\\')
            {
                builder.Append(c);
                i++;
                continue;
            }
            if (i + 1 >= text.Length)
                throw new GrammarSyntaxException("unterminated literal", GrammarSpan.FromBounds(start, text.Length));
            char escape = text[i + 1];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case '0': builder.Append('\0'); break;
                case '\\':
                case '"':
                case '\'':
                    builder.Append(escape);
                    break;
                case 'u':
                    if (i + 6 > text.Length
                        || !int.TryParse(text.Substring(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                        throw new GrammarSyntaxException("\\u needs four hex digits", GrammarSpan.FromBounds(i, Math.Min(i + 6, text.Length)));
                    builder.Append((char)code);
                    i += 6;
                    continue;
                default:
                    throw new GrammarSyntaxException($"unknown escape '\\{escape}'", new GrammarSpan(i, 2));
            }
            i += 2;
        }
    }

    internal static bool IsIdentifierStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    internal static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || IsDigit(c);

    static bool IsDigit(char c) => c >= '0' && c <= '9';
}
