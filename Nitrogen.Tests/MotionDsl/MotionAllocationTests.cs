using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Xunit;

namespace Nitrogen.Tests;

public class MotionAllocationTests
{
    /// <summary>
    /// The least of three consecutive measurements. A Gen2 GC from tests running in parallel can make
    /// ArrayPool trim a cached array, so one measurement re-allocates it; a real per-node allocation
    /// would show in all three.
    /// </summary>
    static long Steady(string text) => Math.Min(Measure(text), Math.Min(Measure(text), Measure(text)));

    static long Measure(string text)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (var result = NitrogenMotionParser.Language.Parse(text, MotionModule.File))
        {
            if (!result.Success) throw new InvalidOperationException("parse failed");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Parsing_a_rig_to_a_tree_allocates_a_constant_amount()
    {
        var files = MotionCorpus.Files();
        if (files.Count == 0) return;
        string smallest = File.ReadAllText(files.OrderBy(f => new FileInfo(f).Length).First());
        string largest = File.ReadAllText(files.OrderBy(f => new FileInfo(f).Length).Last());
        for (int i = 0; i < 3; i++)
        {
            Measure(smallest);
            Measure(largest);
        }

        long small = Steady(smallest), large = Steady(largest);

        Assert.True(large - small <= 256, $"allocation grows with input: {small} B for {smallest.Length} chars, {large} B for {largest.Length} chars");
        Assert.True(large < 4096, $"per-parse fixed cost too high: {large} B");
    }
}
