namespace DateCalc.Syntax;

/// <summary>
/// DateCalc inside C#: a string tagged <c>/*lang=datecalc*/</c> (or preceded by <c>// language=datecalc</c>)
/// is colored, completed and checked by the Nitrogen language server like a .datecalc file.
/// </summary>
public static class Snippets
{
    public static IReadOnlyList<DateCalcLine> Countdown() => DateCalcEvaluator.Run(/*lang=datecalc*/ """
        let christmas = 2026-12-25;
        (christmas - 2026-10-05) in days;
        weekday(christmas);
        """);

    // language=datecalc
    const string Deadline = "2026-10-05 + 6 weeks;";

    public static IReadOnlyList<DateCalcLine> Due() => DateCalcEvaluator.Run(Deadline);
}
