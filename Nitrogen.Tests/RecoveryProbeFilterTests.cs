using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;

namespace Nitrogen.Tests;

public unsafe class RecoveryProbeFilterTests(ITestOutputHelper output)
{
    static int _calls;
    static int Number(ReadOnlySpan<char> text, int position)
    {
        _calls++;
        int end = position;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return end == position ? -1 : end;
    }

    static LookaheadSet Set(AsciiSet[]? starts) => new([], [], [&Number], false, starts);

    [Fact]
    public void Impossible_starts_skip_the_matcher_and_keep_the_same_answers()
    {
        var ordinary = Set(null);
        var filtered = Set([AsciiSet.Range('0', '9')]);
        string[] texts = ["", "123", "0x", "word", ";", "é", "😀", " 1", "\0"];
        foreach (string text in texts)
        {
            bool expected = ordinary.Matches(text, 0);
            _calls = 0;
            Assert.Equal(expected, filtered.Matches(text, 0));
            Assert.Equal(text.Length > 0 && char.IsAsciiDigit(text[0]) ? 1 : 0, _calls);
        }
    }

    [Fact]
    public void Unknown_starts_are_not_filtered_and_end_of_input_keeps_its_policy()
    {
        _calls = 0;
        Assert.False(Set([AsciiSet.Any]).Matches("é", 0));
        Assert.Equal(1, _calls);
        var end = new LookaheadSet([], [], [&Number], true, [AsciiSet.Range('0', '9')]);
        Assert.True(end.Matches("", 0));
    }

    [Fact]
    public void Filters_must_correspond_to_the_token_matchers() =>
        Assert.Throws<ArgumentException>(() => Set([]));

    [Fact]
    public void Repeated_recovery_probes_reduce_matcher_calls()
    {
        var ordinary = Set(null);
        var filtered = Set([AsciiSet.Range('0', '9')]);
        string[] inputs = ["??", "}", ";", "word", "é", "123"];
        const int rounds = 100_000;
        (long Ticks, int Calls, int Matches) Run(LookaheadSet set)
        {
            _calls = 0;
            int matches = 0;
            long before = Stopwatch.GetTimestamp();
            for (int i = 0; i < rounds; i++)
                foreach (string text in inputs) if (set.Matches(text, 0)) matches++;
            return (Stopwatch.GetTimestamp() - before, _calls, matches);
        }
        Run(ordinary);
        Run(filtered);
        var before = Run(ordinary);
        var after = Run(filtered);
        Assert.Equal(before.Matches, after.Matches);
        Assert.Equal(rounds * inputs.Length, before.Calls);
        Assert.Equal(rounds, after.Calls);
        output.WriteLine($"Mixed numeric probes: calls {before.Calls} -> {after.Calls}; elapsed {before.Ticks * 1000.0 / Stopwatch.Frequency:F2} -> {after.Ticks * 1000.0 / Stopwatch.Frequency:F2} ms. Timing is informational, not an admission gate.");
    }
}
