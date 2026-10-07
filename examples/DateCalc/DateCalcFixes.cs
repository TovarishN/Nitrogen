using System.Globalization;
using Nitrogen.Semantics;

namespace DateCalc.Syntax;

/// <summary>
/// DateCalc's quick fixes: the nearest calendar date for an invalid one (DC0001), and <c>-</c> for a
/// <c>+</c> that doesn't apply (DC0002). The language server finds <see cref="Fixes"/> and offers a fix
/// only when it removes the error, so <c>-</c> is offered for <c>date + date</c> but not for
/// <c>duration + date</c>.
/// </summary>
public static class DateCalcFixes
{
    public static readonly DiagnosticFixes Fixes = new(new Dictionary<string, Func<FixRequest, IEnumerable<QuickFix>>>
    {
        ["DC0001"] = NearestDate,
        ["DC0002"] = Subtract,
    });

    /// <summary>The valid date nearest a <c>yyyy-MM-dd</c> text: its year clamped to 1–9999, its month to 1–12, then its day to that month's days; null when it is not that shape.</summary>
    public static DateOnly? Nearest(string text)
    {
        string[] parts = text.Split('-');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int day))
            return null;
        year = Math.Clamp(year, 1, 9999);
        month = Math.Clamp(month, 1, 12);
        return new DateOnly(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)));
    }

    static IEnumerable<QuickFix> NearestDate(FixRequest request)
    {
        if (Nearest(request.Text.Substring(request.Span.Start, request.Span.Length)) is not { } date) return [];
        string text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return [new QuickFix($"Change to {text}", [new TextEdit(request.Span, text)])];
    }

    /// <summary>The <c>+</c> of the binary expression at the diagnostic's span, as a <c>-</c>.</summary>
    static IEnumerable<QuickFix> Subtract(FixRequest request)
    {
        var tree = request.Tree;
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.Span(node) != request.Span || tree.ChildCount(node) != 3) continue;
            var op = tree.Span(tree.Child(node, 1));
            if (request.Text.Substring(op.Start, op.Length) == "+")
                return [new QuickFix("Use '-'", [new TextEdit(op, "-")])];
        }
        return [];
    }
}
