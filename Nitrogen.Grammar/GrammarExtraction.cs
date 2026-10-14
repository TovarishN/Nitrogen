namespace Nitrogen.Grammar;

/// <summary>Source-preserving extraction of complete declarations, never textual name substitution.</summary>
public static partial class GrammarExtraction
{
    public static ExtractionSelectionResult Select(ExtractionSource source, GrammarSpan selection)
    {
        if (selection.Start < 0 || selection.Length <= 0 || selection.End > source.Text.Length)
            return SelectionError("Select one or more complete rules or alternatives.");
        var parsed = GrammarParser.Parse(DocumentSignatures.Read(source.Text).MaskedText);
        if (parsed.File is null)
            return SelectionError("Fix grammar syntax errors before extracting definitions.");
        foreach (var module in parsed.File.Modules)
        {
            if (module.Helpers.Any(helper => selection.Start < helper.Span.End && helper.Span.Start < selection.End))
                return SelectionError("Quote helper declarations cannot be moved with Extract Block; choose a destination when extracting the inline quote.");
            var rules = Match(module.Rules.Select(r => r.Span));
            if (rules.Length > 0)
                return Success(module, ExtractionShape.Rules, null, rules);
            foreach (var rule in module.Rules.OfType<ExtensibleRule>())
            {
                var alternatives = Match(rule.Alternatives.Select(a => a.Span));
                if (alternatives.Length > 0)
                    return Success(module, ExtractionShape.Alternatives, module.Name + "." + rule.Name, alternatives);
            }
            foreach (var extend in module.Extends)
            {
                var alternatives = Match(extend.Alternatives.Select(a => a.Span));
                if (alternatives.Length > 0)
                {
                    string target = extend.Target.Contains('.') ? extend.Target : module.Name + "." + extend.Target;
                    return Success(module, ExtractionShape.Alternatives, target, alternatives);
                }
            }
        }
        return SelectionError("Select complete sibling rules or alternatives in a single module.");

        ExtractionSelectionResult SelectionError(string message) =>
            new(null, new[] { new ExtractionProblem("NX0001", message, source.Uri, selection) });

        GrammarSpan[] Match(IEnumerable<GrammarSpan> spans)
        {
            var inside = spans.Where(s => selection.Start <= s.Start && s.End <= selection.End).OrderBy(s => s.Start).ToArray();
            if (inside.Length == 0 || !Trivia(source.Text.Substring(selection.Start, inside[0].Start - selection.Start)) ||
                !Trivia(source.Text.Substring(inside[inside.Length - 1].End, selection.End - inside[inside.Length - 1].End)))
                return Array.Empty<GrammarSpan>();
            for (int i = 1; i < inside.Length; i++)
                if (!Trivia(source.Text.Substring(inside[i - 1].End, inside[i].Start - inside[i - 1].End)))
                    return Array.Empty<GrammarSpan>();
            return inside;
        }

        ExtractionSelectionResult Success(ModuleDecl module, ExtractionShape shape, string? target, GrammarSpan[] spans)
        {
            int start = Math.Min(selection.Start, AttachedStart(source.Text, spans[0].Start));
            int end = Math.Max(selection.End, spans[spans.Length - 1].End);
            int lineEnd = source.Text.IndexOf('\n', end);
            if (lineEnd >= 0 && source.Text.Substring(end, lineEnd - end).Trim().Length == 0)
                end = lineEnd + 1;
            return new(new(source.Uri, module.Name, shape, target, spans, GrammarSpan.FromBounds(start, end)), Array.Empty<ExtractionProblem>());
        }
    }

    static bool Trivia(string text)
    {
        try
        {
            int at = 0;
            return GrammarLexer.Next(text, ref at).Kind == TokenKind.End;
        }
        catch (GrammarSyntaxException) { return false; }
    }

    static int AttachedStart(string text, int at)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        if (text.Substring(start, at - start).Trim().Length != 0)
            return at;
        int scan = start;
        bool inBlock = false;
        while (scan > 0)
        {
            int previous = text.LastIndexOf('\n', Math.Max(0, scan - 2)) + 1;
            string line = text.Substring(previous, scan - previous).Trim();
            if (!inBlock && !line.StartsWith("//", StringComparison.Ordinal) && !line.EndsWith("*/", StringComparison.Ordinal))
                break;
            if (line.EndsWith("*/", StringComparison.Ordinal)) inBlock = true;
            if (line.StartsWith("/*", StringComparison.Ordinal)) inBlock = false;
            scan = previous;
            if (!inBlock) start = scan;
        }
        return start;
    }
}
