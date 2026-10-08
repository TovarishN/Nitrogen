using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>
/// Signature help: the overloads of the <c>name(…)</c> call around the cursor, from the language's
/// <see cref="CallSignatures"/> or, for a template, from its declaration. The call is found in the text,
/// because error recovery can drop a half-typed call's commas from the tree: characters inside real
/// tokens other than <c>(</c>, <c>)</c>, <c>,</c>, <c>;</c>, <c>{</c> and <c>}</c> are skipped, and the scan
/// stops at <c>;</c>, <c>{</c> or <c>}</c>. A comment holding parentheses inside an unfinished call can still confuse it.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    public ServiceSignatureHelp? SignatureHelp(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri)) return Into(uri, position) is { } inner ? SignatureHelp(inner.Uri, inner.Position) : null;
        if (!_documents.TryGetValue(uri, out var document)) return null;
        int offset = document.Lines.OffsetOf(position);
        if (CallAt(document.Parsed.Tree, document.Text, offset) is not { } call) return null;

        var overloads = (document.Language.Calls?.For(call.Name) ?? []).ToList();
        if (overloads.Count == 0 && TemplateCallee(document, call.NameSpan) is { } template) overloads.Add(template);
        if (overloads.Count == 0) return null;

        var argumentTypes = ArgumentTypes(document, call.Open, call.Active);
        int active = overloads.FindIndex(o => o.Parameters.Count > call.Active &&
            argumentTypes.Select((type, i) => type is null || o.Parameters[i].Type.Equals(type)).All(fits => fits));
        if (active < 0) active = Math.Max(0, overloads.FindIndex(o => o.Parameters.Count > call.Active));
        return new ServiceSignatureHelp(overloads.Select(o => Label(call.Name, o)).ToList(), active, call.Active);
    }

    /// <summary>The call around <paramref name="offset"/>: its callee's name and span, its '(' offset, and the active parameter; null outside an unclosed name(…).</summary>
    static (string Name, TextSpan NameSpan, int Open, int Active)? CallAt(SyntaxTree tree, string text, int offset)
    {
        var opaque = new bool[text.Length];
        for (int node = 0; node < tree.NodeCount; node++)
        {
            var span = tree.Span(node);
            if (tree.ChildCount(node) != 0 || span.Length == 0) continue;
            if (text.AsSpan(span.Start, Math.Min(span.Length, text.Length - span.Start)) is "(" or ")" or "," or ";" or "{" or "}") continue;
            for (int i = span.Start; i < span.End && i < text.Length; i++) opaque[i] = true;
        }
        int depth = 0, active = 0;
        for (int i = Math.Min(offset, text.Length) - 1; i >= 0; i--)
        {
            if (opaque[i]) continue;
            switch (text[i])
            {
                case ')': depth++; break;
                case ',' when depth == 0: active++; break;
                case ';' or '{' or '}' when depth == 0: return null;
                case '(' when depth > 0: depth--; break;
                case '(':
                {
                    int end = i;
                    while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
                    int start = end;
                    while (start > 0 && IsNamePart(text[start - 1])) start--;
                    if (start == end || char.IsDigit(text[start])) return null;
                    return (text[start..end], new TextSpan(start, end - start), i, active);
                }
            }
        }
        return null;
    }

    static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The signature of the template the callee names, read from its declaration; null when it names none.</summary>
    CallSignature? TemplateCallee(Document document, TextSpan name)
    {
        if (NameAt(document.Uri, document.Lines.PositionOf(name.Start)) is not { Symbols.Count: > 0 } found) return null;
        var types = SemanticsOf(document.Language)[document.Uri].DeclarativeTypes;
        foreach (var symbol in found.Symbols)
            if (types.TemplateSignature(symbol) is { } template)
                return new CallSignature(
                    template.Parameters.Select(p => new CallParameter(p.Name, p.Type ?? SemanticTypes.Error)).ToArray(),
                    template.Result ?? SemanticTypes.Error);
        return null;
    }

    /// <summary>The types of the complete arguments before the active one, by the syntax node each spans exactly; null where unknown.</summary>
    List<SemanticType?> ArgumentTypes(Document document, int open, int active)
    {
        var types = new List<SemanticType?>();
        if (active == 0) return types;
        var semantics = SemanticsOf(document.Language)[document.Uri];
        var tree = document.Parsed.Tree;
        int start = open + 1;
        for (int k = 0; k < active; k++)
        {
            int comma = NextTopLevelComma(document.Text, start);
            if (comma < 0) { types.Add(null); start = document.Text.Length; continue; }
            var (from, to) = Trim(document.Text, start, comma);
            SemanticType? type = null;
            for (int node = 0; node < tree.NodeCount && type is null; node++)
                if (tree.Span(node) is var span && span.Start == from && span.End == to) type = semantics.DeclarativeTypes.TypeOf(node);
            types.Add(type);
            start = comma + 1;
        }
        return types;
    }

    static int NextTopLevelComma(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') { if (depth == 0) return -1; depth--; }
            else if (text[i] == ',' && depth == 0) return i;
        }
        return -1;
    }

    static (int From, int To) Trim(string text, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        return (from, to);
    }

    static ServiceSignature Label(string name, CallSignature signature)
    {
        var label = new System.Text.StringBuilder(name).Append('(');
        var ranges = new List<(int, int)>();
        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            if (i > 0) label.Append(", ");
            int start = label.Length;
            label.Append(signature.Parameters[i].Name).Append(": ").Append(signature.Parameters[i].Type);
            ranges.Add((start, label.Length));
        }
        label.Append(") → ").Append(signature.Result);
        return new ServiceSignature(label.ToString(), ranges, signature.Summary);
    }
}
