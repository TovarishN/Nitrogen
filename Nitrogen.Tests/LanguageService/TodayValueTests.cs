using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>DateCalc's today in the editor: the service's clock, hints that follow the date, and when the day next changes.</summary>
public sealed class TodayValueTests : IDisposable
{
    static readonly DocumentRange Whole = new(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));
    static readonly DateTimeOffset Evening = new(2026, 10, 7, 22, 0, 0, TimeSpan.FromHours(3));

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-today-").FullName;

    public TodayValueTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service(ManualClock clock)
    {
        var service = new NitrogenLanguageService(LspCommand.Registry()) { Clock = clock };
        service.ConfigureWorkspace(_root);
        return service;
    }

    [Fact]
    public void Hints_show_the_clock_date_and_follow_it_past_midnight()
    {
        var clock = new ManualClock(Evening);
        using var service = Service(clock);
        string uri = Uri("a.datecalc");
        service.Open(uri, 1, "today;\n(2026-12-25 - today) in days;");

        Assert.Equal(["= 2026-10-07 Wed", "= 79"], service.ValueHints(uri, Whole).Select(h => h.Label));

        clock.Advance(TimeSpan.FromHours(3)); // 01:00 the next day; the document is unchanged
        Assert.Equal(["= 2026-10-08 Thu", "= 78"], service.ValueHints(uri, Whole).Select(h => h.Label));
    }

    [Fact]
    public void Hovering_today_shows_its_date()
    {
        using var service = Service(new ManualClock(Evening));
        string uri = Uri("a.datecalc");
        const string text = "today + 1 days;";
        service.Open(uri, 1, text);

        Assert.Contains("\n\n= 2026-10-07 Wed", service.Hover(uri, new DocumentPosition(0, 2))!.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_reads_the_clock_when_a_served_language_does()
    {
        using var service = Service(new ManualClock(Evening));
        Assert.True(service.ReadsClock);

        using var plain = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        Assert.False(plain.ReadsClock);
    }

    [Fact]
    public void The_next_day_change_is_the_next_local_midnight()
    {
        using var service = Service(new ManualClock(Evening));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(3)), service.NextDayChange);
    }
}
