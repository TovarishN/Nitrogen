using Nitrogen.Binding;
using Nitrogen.Semantics;

namespace Nitrogen.LanguageService;

/// <summary>
/// Quick fixes: a language's fixers propose edits for its errors, and the service offers one only when
/// the edited document, parsed and checked on its own, no longer has that error there and has fewer
/// errors overall. The check sees one document, so a fix that relies on another file can be rejected.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    /// <summary>The checked fixes for the errors overlapping <paramref name="range"/>; for a C# host, those of the tagged string at its start, at host positions.</summary>
    public IReadOnlyList<ServiceFix> QuickFixes(string uri, DocumentRange range)
    {
        if (_hosts.ContainsKey(uri))
        {
            if (Into(uri, range.Start) is not { } inner) return [];
            return QuickFixes(inner.Uri, new DocumentRange(inner.Position, inner.Position))
                .Select(fix => new ServiceFix(fix.Title, fix.Diagnostic with { Range = OutOf(inner.Uri, fix.Diagnostic.Range) },
                    fix.Edits.Select(edit => edit with { Range = OutOf(inner.Uri, edit.Range) }).ToList()))
                .ToList();
        }
        if (!_documents.TryGetValue(uri, out var document) || document.Language.Fixes is not { } fixes) return [];
        var errors = Diagnostics(uri).Where(d => d.Severity == ServiceSeverity.Error && Overlaps(d.Range, range)).ToList();
        if (errors.Count == 0) return [];

        int before = Errors(document.Language, document.Start, uri, document.Text).Count;
        var offered = new List<ServiceFix>();
        foreach (var error in errors)
        {
            int start = document.Lines.OffsetOf(error.Range.Start);
            var request = new FixRequest(error.Code, new TextSpan(start, document.Lines.OffsetOf(error.Range.End) - start), document.Text, document.Parsed.Tree);
            List<QuickFix> proposed;
            try
            {
                proposed = fixes.Propose(request).ToList();
            }
            catch (Exception) // a fixer that throws proposes nothing
            {
                continue;
            }
            foreach (var fix in proposed)
            {
                if (Apply(document.Text, fix.Edits) is not { } applied) continue;
                var after = Errors(document.Language, document.Start, uri, applied.Text);
                if (after.Count >= before || after.Any(e => e.Code == error.Code && applied.Edited.Any(edited => Overlaps(e.Span, edited)))) continue;
                offered.Add(new ServiceFix(fix.Title, error,
                    fix.Edits.Select(edit => new DocumentEdit(document.Lines.RangeOf(edit.Span), edit.NewText)).ToList()));
            }
        }
        return offered;
    }

    /// <summary>The text with the edits made, and where each edit's new text lies in it; null when edits overlap or fall outside the text.</summary>
    static (string Text, List<TextSpan> Edited)? Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var ordered = edits.OrderBy(e => e.Span.Start).ToList();
        var result = new System.Text.StringBuilder();
        var edited = new List<TextSpan>();
        int at = 0;
        foreach (var edit in ordered)
        {
            if (edit.Span.Start < at || edit.Span.End > text.Length) return null;
            result.Append(text, at, edit.Span.Start - at);
            edited.Add(new TextSpan(result.Length, edit.NewText.Length));
            result.Append(edit.NewText);
            at = edit.Span.End;
        }
        result.Append(text, at, text.Length - at);
        return (result.ToString(), edited);
    }

    /// <summary>The errors of <paramref name="text"/> as a document of <paramref name="language"/> on its own: parse, binding and check errors.</summary>
    static List<(string Code, TextSpan Span)> Errors(LanguageEntry language, Rule start, string uri, string text)
    {
        using var document = new Document(uri, 0, text, language, start);
        var project = new Project(language.Language);
        project.Set(uri, document.Parsed.Tree);
        var errors = new List<(string Code, TextSpan Span)>();
        foreach (var diagnostic in document.Parsed.Diagnostics)
            if (Severity(diagnostic.Severity) == ServiceSeverity.Error) errors.Add((diagnostic.Code.ToString(), diagnostic.Span));
        foreach (var diagnostic in project.Diagnostics(uri)) errors.Add((diagnostic.Code, diagnostic.Span));
        foreach (var diagnostic in new ProjectSemantics(project)[uri].Diagnostics())
            if (!diagnostic.Code.StartsWith("NS000", StringComparison.Ordinal)) errors.Add((diagnostic.Code, diagnostic.Span));
        return errors;
    }

    static bool Overlaps(DocumentRange a, DocumentRange b) =>
        (a.Start.Line, a.Start.Character).CompareTo((b.End.Line, b.End.Character)) <= 0 &&
        (b.Start.Line, b.Start.Character).CompareTo((a.End.Line, a.End.Character)) <= 0;

    static bool Overlaps(TextSpan a, TextSpan b) => a.Start <= b.End && b.Start <= a.End;
}
