namespace Nitrogen.LanguageService;

/// <summary>
/// Formatting requests (spec: formatting). A document with syntax errors is not formatted, and the edits
/// are returned only when the formatted text has the same tokens: a formatter bug can never change code.
/// C# hosts are left to C#.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    public IReadOnlyList<DocumentEdit> Format(string uri, FormattingOptions options) => FormatLines(uri, options, 0, int.MaxValue);

    /// <summary>The edits for the lines <paramref name="range"/> touches; the indentation still follows the whole document.</summary>
    public IReadOnlyList<DocumentEdit> FormatRange(string uri, DocumentRange range, FormattingOptions options) =>
        FormatLines(uri, options, range.Start.Line, range.End.Line);

    /// <summary>After <c>}</c>, the edits re-indenting its line; nothing after any other character.</summary>
    public IReadOnlyList<DocumentEdit> FormatOnType(string uri, DocumentPosition position, string typed, FormattingOptions options) =>
        typed == "}" ? FormatLines(uri, options, position.Line, position.Line) : [];

    IReadOnlyList<DocumentEdit> FormatLines(string uri, FormattingOptions options, int first, int last)
    {
        if (_hosts.ContainsKey(uri) || IsEmbedded(uri) || !_documents.TryGetValue(uri, out var document)) return [];
        var parsed = document.Parsed;
        if (parsed.HasErrors || parsed.Tree.SkippedSpans.Length > 0) return [];
        var edits = Formatter.Edits(parsed.Tree, document.Text, options, first, last);
        if (edits.Count == 0) return [];

        string formatted = Formatter.Apply(document.Text, edits);
        using var reparsed = document.Language.Language.Parse(formatted, document.Start);
        if (reparsed.HasErrors || !Formatter.Tokens(reparsed.Tree, formatted).Select(t => t.Text)
                .SequenceEqual(Formatter.Tokens(parsed.Tree, document.Text).Select(t => t.Text)))
            return [];
        return edits.OrderBy(e => e.Start)
            .Select(e => new DocumentEdit(new DocumentRange(document.Lines.PositionOf(e.Start), document.Lines.PositionOf(e.End)), e.Text))
            .ToList();
    }
}
