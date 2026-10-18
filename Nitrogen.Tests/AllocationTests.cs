using System.Text;
using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class AllocationTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();

    static string Program(int statements)
    {
        var text = new StringBuilder();
        for (int i = 0; i < statements; i++) text.Append("x").Append(i).Append(" = a*(b+1) - f(c, 2);\n");
        return text.ToString();
    }

    /// <summary>
    /// The least of three consecutive measurements. A Gen2 GC from tests running in parallel can make
    /// ArrayPool trim a cached array, so one measurement re-allocates it; a real per-node allocation
    /// would show in all three.
    /// </summary>
    static long Steady(string text) => Math.Min(Measure(text), Math.Min(Measure(text), Measure(text)));

    static long Measure(string text)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (var result = Calc.Parse(text, CalcModule.Program))
        {
            if (!result.Success) throw new InvalidOperationException("parse failed");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Steady_state_parse_allocates_a_constant_amount_regardless_of_size()
    {
        string small = Program(50), large = Program(2000);
        for (int i = 0; i < 3; i++)
        {
            Measure(small);
            Measure(large);
        }

        long smallBytes = Steady(small);
        long largeBytes = Steady(large);

        Assert.True(largeBytes - smallBytes <= 256,
            $"allocation grows with input: {smallBytes} B for 50 statements, {largeBytes} B for 2000");
        Assert.True(largeBytes < 4096, $"per-parse fixed cost too high: {largeBytes} B");
    }

    [Fact]
    public void A_small_parse_keeps_its_scratch_for_reuse()
    {
        Measure(Program(50));

        Assert.NotNull(ParseContext.Cached);
    }

    [Fact]
    public void An_oversized_parse_does_not_keep_its_scratch_on_the_thread()
    {
        using (var result = Calc.Parse(Program(10_000), CalcModule.Program))
        {
            Assert.True(result.Success);
            Assert.True(result.Stats.ArenaNodes > ParseContext.RetainedArenaNodes, $"input too small: {result.Stats.ArenaNodes} arena nodes");
        }
        Assert.Null(ParseContext.Cached);

        Measure(Program(50));
        Assert.NotNull(ParseContext.Cached);
    }
}
