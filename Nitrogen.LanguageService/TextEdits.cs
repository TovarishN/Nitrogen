namespace Nitrogen.LanguageService;

/// <summary>A replacement of a range of a text (LSP positions), or of the whole text when the range is null.</summary>
public sealed record TextChange(DocumentRange? Range, string Text);

/// <summary>Applies a didChange's content changes (spec: incremental sync).</summary>
public static class TextEdits
{
    /// <summary>The text after the changes, applied in order: each sees the text the previous ones produced. Positions past a line's or the text's end clamp.</summary>
    public static string Apply(string text, IReadOnlyList<TextChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Range is not { } range)
            {
                text = change.Text;
                continue;
            }
            var lines = new LineMap(text);
            int start = lines.OffsetOf(range.Start);
            int end = Math.Max(start, lines.OffsetOf(range.End));
            text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
        }
        return text;
    }
}
