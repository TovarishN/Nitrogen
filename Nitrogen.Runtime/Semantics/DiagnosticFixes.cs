namespace Nitrogen.Semantics;

/// <summary>A diagnostic to fix: its code and span, and the document it is in.</summary>
public sealed record FixRequest(string Code, TextSpan Span, string Text, SyntaxTree Tree);

/// <summary>Replaces <paramref name="Span"/> of a document with <paramref name="NewText"/>.</summary>
public sealed record TextEdit(TextSpan Span, string NewText);

/// <summary>A proposed fix: what the editor shows, and the edits it makes to the document.</summary>
public sealed record QuickFix(string Title, IReadOnlyList<TextEdit> Edits);

/// <summary>
/// A language's quick fixes, by diagnostic code. A workspace language's helper source exports one as a
/// public static field or property. A fixer only proposes; the host offers a fix after checking it.
/// </summary>
public sealed class DiagnosticFixes(IReadOnlyDictionary<string, Func<FixRequest, IEnumerable<QuickFix>>> byCode)
{
    readonly IReadOnlyDictionary<string, Func<FixRequest, IEnumerable<QuickFix>>> _byCode = byCode ?? throw new ArgumentNullException(nameof(byCode));

    /// <summary>The fixes proposed for the request's diagnostic; empty for a code without a fixer.</summary>
    public IEnumerable<QuickFix> Propose(FixRequest request) =>
        _byCode.TryGetValue(request.Code, out var fixer) ? fixer(request) : [];
}
