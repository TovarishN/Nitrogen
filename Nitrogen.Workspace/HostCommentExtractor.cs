using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nitrogen.Workspace;

/// <summary>Host lexical boundaries only; no native semantic model is exposed to Nitrogen rules.</summary>
public static class HostCommentExtractor
{
    static readonly CSharpParseOptions Options = new(LanguageVersion.Preview);

    public static IReadOnlyList<SourceProjection> Read(string path, string text)
    {
        if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return CSharpSyntaxTree.ParseText(text, Options).GetRoot().DescendantTrivia(descendIntoTrivia: true)
                .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                .Select(t => new SourceProjection(path, text.Substring(t.SpanStart, t.Span.Length), t.SpanStart)).ToArray();
        if (Path.GetExtension(path).ToLowerInvariant() is ".razor" or ".html" or ".htm") return RazorComments(path, text);
        return [];
    }

    // Razor mixes markup and C#. Track its code transitions and markup attributes, and let Roslyn
    // consume C# literals (including raw/interpolated forms), instead of looking for markers in strings.
    static IReadOnlyList<SourceProjection> RazorComments(string path, string text)
    {
        var comments = new List<SourceProjection>();
        var closers = new Stack<char>();
        bool inTag = false, implicitCode = false, controlBody = false, directiveArguments = false;
        int markupDepth = 0, markupCodeDepth = 0, tagStart = -1;
        string tagName = "";
        bool closingTag = false;
        for (int i = 0; i < text.Length;)
        {
            if (At(i, "@*")) { i = After(i + 2, "*@"); continue; }
            if (At(i, "<!--"))
            {
                int end = After(i + 4, "-->");
                comments.Add(new(path, text[i..end], i));
                i = end;
                continue;
            }
            if (closers.Count > markupCodeDepth && (At(i, "//") || At(i, "/*")))
            {
                if (At(i, "/*")) i = After(i + 2, "*/");
                else { while (i < text.Length && text[i] is not ('\r' or '\n')) i++; }
                continue;
            }
            if (inTag && text[i] is '\'' or '"')
            {
                char quote = text[i++];
                while (i < text.Length && text[i] != quote) i++;
                if (i < text.Length) i++;
                continue;
            }
            if ((closers.Count > markupCodeDepth || directiveArguments) && LiteralAt(i))
            {
                var token = SyntaxFactory.ParseTokens(text[i..], options: Options).First();
                i += Math.Max(token.Span.End, 1);
                continue;
            }
            if (text[i] == '@' && i + 1 < text.Length)
            {
                if (text[i + 1] is '{' or '(')
                {
                    closers.Push(text[i + 1] == '{' ? '}' : ')');
                    i += 2;
                    continue;
                }
                int start = ++i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string keyword = text[start..i];
                controlBody = keyword is "code" or "functions" or "if" or "foreach" or "for" or "while" or "switch" or "try";
                directiveArguments = keyword is "attribute" or "page" or "inject" or "model" or "inherits" or
                    "implements" or "typeparam" or "using" or "namespace" or "layout" or "rendermode";
                implicitCode = keyword.Length > 0;
                continue;
            }
            if (!inTag && (closers.Count > 0 || implicitCode || controlBody || directiveArguments))
            {
                if (text[i] is '(' or '{' or '[') closers.Push(text[i] switch { '(' => ')', '[' => ']', _ => '}' });
                else if (closers.Count > 0 && text[i] == closers.Peek()) closers.Pop();
            }
            if (text[i] == '<' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/') &&
                (closers.Count == 0 || markupDepth > 0 || MarkupMayStart(i)))
            {
                inTag = true;
                tagStart = i;
                closingTag = text[i + 1] == '/';
                int nameStart = i + (closingTag ? 2 : 1), nameEnd = nameStart;
                while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '-' or '.')) nameEnd++;
                tagName = text[nameStart..nameEnd].ToLowerInvariant();
            }
            else if (inTag && text[i] == '>')
            {
                inTag = false;
                bool voidTag = tagName is "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input" or "link" or "meta" or "param" or "source" or "track" or "wbr";
                if (closingTag) markupDepth = Math.Max(0, markupDepth - 1);
                else if (i > tagStart && text[i - 1] != '/' && !voidTag)
                {
                    if (markupDepth == 0) markupCodeDepth = closers.Count;
                    markupDepth++;
                }
                if (markupDepth == 0) markupCodeDepth = 0;
            }
            if (text[i] is '\r' or '\n' && closers.Count == 0) directiveArguments = false;
            if (char.IsWhiteSpace(text[i])) implicitCode = false;
            if (text[i] == '}' && closers.Count == 0) controlBody = false;
            i++;
        }
        return comments;

        bool MarkupMayStart(int at)
        {
            while (at > 0 && char.IsWhiteSpace(text[at - 1]))
            {
                if (text[at - 1] is '\r' or '\n') return true;
                at--;
            }
            return at == 0 || text[at - 1] is '{' or '}' or ';';
        }
        bool LiteralAt(int at)
        {
            if (text[at] == '\'') return true;
            while (at < text.Length && text[at] is '$' or '@') at++;
            return at < text.Length && text[at] == '"';
        }
        bool At(int at, string value) => text.AsSpan(at).StartsWith(value, StringComparison.Ordinal);
        int After(int start, string close)
        {
            int end = text.IndexOf(close, start, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + close.Length;
        }
    }
}
