using Nitrogen.Tests.Calc;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;

namespace Nitrogen.Tests;

public class RecoverySeedingTests(ITestOutputHelper output)
{
    static readonly Language Language = new LanguageBuilder().Add(CalcModule.Instance).Build();

    sealed class UnadmittedModule() : SyntaxModule("Unadmitted")
    {
        public override string GetKindName(int localKind) => "Unknown";
        public override void Register(ExtensionRegistry registry) { }
    }

    [Fact]
    public void A_module_without_the_seeding_contract_keeps_the_original_recovery_path()
    {
        var module = new UnadmittedModule();
        Assert.False(module.SupportsRecoverySeeding);
        var language = new LanguageBuilder().Add(CalcModule.Instance).Add(module).Build();
        const string text = "a + b; c + d; x = ;";
        using var reference = language.ParseUnseeded(text, CalcModule.Program);
        using var actual = language.Parse(text, CalcModule.Program);
        TreeAssert.Same(reference, actual);
        Assert.Equal(reference.Stats.MemoMisses, actual.Stats.MemoMisses);
    }

    [Theory]
    [InlineData("a + b; c + d; x = ;")]
    [InlineData("a + b; c + d; ??")]
    [InlineData("a + b; c + d; f(a, ;")]
    [InlineData("a + b; c + d; x = (a + );")]
    public void Healthy_strict_recognition_reduces_recovery_work(string text)
    {
        using var reference = Language.ParseUnseeded(text, CalcModule.Program);
        using var seeded = Language.Parse(text, CalcModule.Program);
        TreeAssert.Same(reference, seeded);
        Assert.False(seeded.Success);
        Assert.True(seeded.Stats.MemoMisses < reference.Stats.MemoMisses,
            $"Seeded misses {seeded.Stats.MemoMisses}; unseeded {reference.Stats.MemoMisses}");
        output.WriteLine($"Memo misses: {reference.Stats.MemoMisses} -> {seeded.Stats.MemoMisses}");
    }

    [Fact]
    public void Composed_ambiguous_grammar_matches_unseeded_recovery_at_every_edit_position()
    {
        var language = new LanguageBuilder().Add(CalcModule.Instance).Add(ClashModule.Instance).Add(PowerModule.Instance).Build();
        const string baseline = "f(a, b); x = a ^ b; g(c);";
        for (int i = 0; i <= baseline.Length; i++)
            foreach (string insert in new[] { "??", ";", "}", "/*", "😀" })
            {
                string text = baseline[..i] + insert + baseline[i..];
                using var reference = language.ParseUnseeded(text, CalcModule.Program);
                using var seeded = language.Parse(text, CalcModule.Program);
                TreeAssert.Same(reference, seeded);
            }
    }

    [Fact]
    public void Late_error_measurement()
    {
        string text = string.Concat(Enumerable.Repeat("a + f(b, 2);\n", 2000)) + "x = ;";
        using (var warm = Language.Parse(text, CalcModule.Program)) { }
        using (var warm = Language.ParseUnseeded(text, CalcModule.Program)) { }
        var oldTimes = new List<double>();
        var newTimes = new List<double>();
        for (int i = 0; i < 6; i++)
            foreach (bool seed in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                long before = Stopwatch.GetTimestamp();
                using var result = seed ? Language.Parse(text, CalcModule.Program) : Language.ParseUnseeded(text, CalcModule.Program);
                (seed ? newTimes : oldTimes).Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
            }
        oldTimes.Sort(); newTimes.Sort();
        output.WriteLine($"Late error ({text.Length} UTF-16 units), six alternating warm runs, median: unseeded {(oldTimes[2] + oldTimes[3]) / 2:F2} ms; seeded {(newTimes[2] + newTimes[3]) / 2:F2} ms. Informational; no timing gate.");
        using var expected = Language.ParseUnseeded(text, CalcModule.Program);
        using var actual = Language.Parse(text, CalcModule.Program);
        TreeAssert.Same(expected, actual);
    }
}
