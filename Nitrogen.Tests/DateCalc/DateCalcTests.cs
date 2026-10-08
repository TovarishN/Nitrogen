using DateCalc.Syntax;
using Xunit;

namespace Nitrogen.Tests.DateCalc;

/// <summary>The DateCalc example: typed overloads, dates and durations, from source to value.</summary>
public sealed class DateCalcTests
{
    [Fact]
    public void Sample_shows_each_statement_value()
    {
        string sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", "sample.datecalc"));

        var lines = DateCalcEvaluator.Run(sample);

        Assert.Equal(
        [
            new(2, "2026-10-05 Mon"),
            new(3, "14 days"),
            new(4, "2026-10-19 Mon"),
            new(5, "Monday"),
            new(6, "81"),
            new(7, "14"),
            new(8, "6.28"),
            new(9, "2026-12-25 Fri"),
        ], lines);
    }

    [Theory]
    [InlineData("1 + 2 * 3;", "7")]
    [InlineData("(1 + 2) * 3;", "9")]
    [InlineData("2026-03-01 - 2026-02-01;", "28 days")]
    [InlineData("2026-03-01 - 1 days;", "2026-02-28 Sat")]
    [InlineData("1 weeks + 2 days;", "9 days")]
    [InlineData("let d = 2026-01-01; let e = d + 1 weeks; e;", "2026-01-08 Thu")]
    [InlineData("sqrt(16) + abs(0 - 2);", "6")]
    [InlineData("pow(2, 10);", "1024")]
    [InlineData("round(pi, 2);", "3.14")]
    [InlineData("round(2.5) + floor(1.9) + ceil(1.1);", "6")]
    [InlineData("mod(0 - 7, 3);", "2")]
    [InlineData("log(8, 2) + ln(e);", "4")]
    [InlineData("max(2026-10-05, 2026-12-25);", "2026-12-25 Fri")]
    [InlineData("min(2 weeks, 10 days);", "10 days")]
    [InlineData("abs(2026-10-05 - 2026-12-25);", "81 days")]
    public void Operators_pick_the_overload_for_their_operand_types(string source, string expected) =>
        Assert.Equal(expected, DateCalcEvaluator.Run(source)[^1].Text);

    [Theory]
    [InlineData("2026-10-05 + 2026-10-06;", "DC0002: '+' does not apply to DateCalc.Date and DateCalc.Date")]
    [InlineData("2 weeks * 2026-10-05;", "DC0002: '*' does not apply to DateCalc.Duration and DateCalc.Date")]
    [InlineData("2026-02-30;", "DC0001: '2026-02-30' is not a calendar date")]
    [InlineData("weekday(3);", "DC0003: 'weekday' does not take (Core.Scalar)")]
    [InlineData("pow(2);", "DC0003: 'pow' does not take (Core.Scalar)")]
    [InlineData("sqrt(2026-10-05);", "DC0003: 'sqrt' does not take (DateCalc.Date)")]
    [InlineData("cube(2);", "unresolved function 'cube'")]
    [InlineData("weekday((2026-10-05 + 2026-10-06));", "DC0002: '+' does not apply to DateCalc.Date and DateCalc.Date")]
    public void Ill_typed_programs_report_source_diagnostics(string source, string message)
    {
        var line = Assert.Single(DateCalcEvaluator.Run(source));
        Assert.True(line.IsError);
        Assert.Equal(message, line.Text);
    }

    [Fact]
    public void A_nonfinite_result_stops_at_the_call()
    {
        var line = Assert.Single(DateCalcEvaluator.Run("sqrt(0 - 1);"));
        Assert.True(line.IsError);
        Assert.Contains("finite", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grammar_declares_every_function_and_constant_of_the_modules()
    {
        string grammar = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", "DateCalc.ngr"));
        string Builtins(string kind)
        {
            int start = grammar.IndexOf("builtin " + kind, StringComparison.Ordinal);
            return grammar[(grammar.IndexOf('{', start) + 1)..grammar.IndexOf('}', start)];
        }
        Assert.Equal(DateCalcLanguage.AllFunctions.Select(f => f.Name).ToHashSet(),
            Builtins("function").Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).ToHashSet());
        Assert.Equal(MathModule.Constants.Select(c => c.Name).Append("today").ToHashSet(),
            Builtins("value").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet());
    }

    [Fact]
    public void The_profile_binds_to_the_language_and_selects_its_statements()
    {
        var bound = DateCalcEvaluator.Profile.Bind(DateCalcEvaluator.Language.SemanticCatalog);

        Assert.Equal(new HashSet<int> { DateCalcKinds.Let, DateCalcKinds.Show }, bound.Profile.StatementKinds);
        Assert.Equal("2026-10-05 Mon", DateCalcEvaluator.Profile.Format(
            new Nitrogen.Semantic.ProjectedValue(DateCalcLanguage.Date, new DateOnly(2026, 10, 5))));
    }

    [Theory]
    [InlineData("today;", "2026-10-07 Wed")]
    [InlineData("(2026-12-25 - today) in days;", "79")]
    [InlineData("today + 6 weeks;", "2026-11-18 Wed")]
    public void Today_is_the_date_the_caller_gives(string source, string expected) =>
        Assert.Equal(expected, Assert.Single(DateCalcEvaluator.Run(source, new DateOnly(2026, 10, 7))).Text);

    [Fact]
    public void The_profile_reads_the_clock()
    {
        Assert.True(DateCalcEvaluator.Profile.ReadsClock);
    }

    [Theory]
    [InlineData("2026-02-30", "2026-02-28")]
    [InlineData("2028-02-30", "2028-02-29")]
    [InlineData("2026-13-05", "2026-12-05")]
    [InlineData("2026-00-00", "2026-01-01")]
    [InlineData("2026-04-31", "2026-04-30")]
    public void The_nearest_date_clamps_the_month_then_the_day(string text, string expected) =>
        Assert.Equal(expected, DateCalcFixes.Nearest(text)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Tagged_snippets_run()
    {
        Assert.Equal(["81", "Friday"], Snippets.Countdown().Skip(1).Select(l => l.Text));
        Assert.Equal("2026-11-16 Mon", Assert.Single(Snippets.Due()).Text);
    }
}
