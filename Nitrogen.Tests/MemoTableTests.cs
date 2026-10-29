using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class MemoTableTests
{
    [Fact]
    public void Recovery_seed_preserves_opposite_context_successes_at_the_same_rule_and_position()
    {
        var source=new MemoTable();var seed=new MemoTable();
        source.Set(2,4,11,9,12,0);source.Set(2,4,21,19,22,1UL<<63);
        source.Set(2,5,-1,-1,23,1UL<<63);source.CopySuccessesTo(seed);
        Assert.Equal(2,seed.Count);
        Assert.True(seed.TryGet(2,4,out int low,out _,out _,0));Assert.Equal(11,low);
        Assert.True(seed.TryGet(2,4,out int high,out _,out _,1UL<<63));Assert.Equal(21,high);
        Assert.False(seed.TryGet(2,5,out _,out _,out _,1UL<<63));
    }

    [Fact]
    public void Recovery_seed_copies_successes_and_discards_failures_and_old_generations()
    {
        var source = new MemoTable();
        var seed = new MemoTable();
        source.Set(1, 0, 10, 5, 8);
        source.Clear();
        source.Set(2, 4, 11, 9, 12);
        source.Set(3, 4, -1, -1, 13);
        seed.Set(9, 0, 20, 5);
        source.CopySuccessesTo(seed);
        Assert.Equal(1, seed.Count);
        Assert.True(seed.TryGet(2, 4, out int node, out int end, out int examined));
        Assert.Equal((11, 9, 12), (node, end, examined));
        Assert.False(seed.TryGet(1, 0, out _, out _));
        Assert.False(seed.TryGet(3, 4, out _, out _));
        Assert.False(seed.TryGet(2 | (1 << 8), 4, out _, out _)); // Repair flags require a distinct result.
        Assert.False(seed.TryGet(9, 0, out _, out _));
    }

    [Fact]
    public void Set_then_get_round_trips()
    {
        var memo = new MemoTable();
        memo.Set(ruleKey: 5, position: 10, node: 42, end: 17);
        Assert.True(memo.TryGet(5, 10, out int node, out int end));
        Assert.Equal((42, 17), (node, end));
    }

    [Fact]
    public void Missing_key_is_not_found()
    {
        var memo = new MemoTable();
        memo.Set(5, 10, 42, 17);
        Assert.False(memo.TryGet(5, 11, out _, out _));
        Assert.False(memo.TryGet(6, 10, out _, out _));
    }

    [Fact]
    public void Failure_is_stored_as_negative_node()
    {
        var memo = new MemoTable();
        memo.Set(1, 0, -1, -1);
        Assert.True(memo.TryGet(1, 0, out int node, out _));
        Assert.Equal(-1, node);
    }

    [Fact]
    public void Overwrite_keeps_one_entry()
    {
        var memo = new MemoTable();
        memo.Set(1, 0, 3, 4);
        memo.Set(1, 0, 5, 6);
        Assert.Equal(1, memo.Count);
        Assert.True(memo.TryGet(1, 0, out int node, out int end));
        Assert.Equal((5, 6), (node, end));
    }

    [Fact]
    public void Clear_forgets_everything_without_reallocating()
    {
        var memo = new MemoTable();
        memo.Set(1, 0, 3, 4);
        int capacity = memo.Capacity;
        memo.Clear();
        Assert.Equal(0, memo.Count);
        Assert.False(memo.TryGet(1, 0, out _, out _));
        Assert.Equal(capacity, memo.Capacity);
    }

    [Fact]
    public void Growth_preserves_entries()
    {
        var memo = new MemoTable();
        for (int i = 0; i < 1000; i++) memo.Set(i % 7, i, i * 2, i * 3);
        Assert.Equal(1000, memo.Count);
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(memo.TryGet(i % 7, i, out int node, out int end));
            Assert.Equal((i * 2, i * 3), (node, end));
        }
    }

    [Fact]
    public void Capacity_grows_only_past_three_quarters_load()
    {
        var memo = new MemoTable();
        for (int i = 0; i < 300; i++) memo.Set(i % 5, i, i, i);
        Assert.Equal(512, memo.Capacity);
    }

    [Fact]
    public void Clear_forgets_every_entry_and_keeps_capacity()
    {
        var memo = new MemoTable();
        for (int i = 0; i < 1000; i++) memo.Set(i % 7, i, i, i);
        int capacity = memo.Capacity;
        memo.Clear();
        Assert.Equal(0, memo.Count);
        Assert.Equal(capacity, memo.Capacity);
        for (int i = 0; i < 1000; i++) Assert.False(memo.TryGet(i % 7, i, out _, out _));
        memo.Set(0, 0, 1, 2);
        Assert.True(memo.TryGet(0, 0, out int node, out int end));
        Assert.Equal((1, 2), (node, end));
    }

    [Fact]
    public void Entries_survive_growth()
    {
        var memo = new MemoTable();
        for (int i = 0; i < 5000; i++) memo.Set(i % 11, i, i * 2 + 1, i * 3 + 2);
        Assert.Equal(5000, memo.Count);
        for (int i = 0; i < 5000; i++)
        {
            Assert.True(memo.TryGet(i % 11, i, out int node, out int end));
            Assert.Equal((i * 2 + 1, i * 3 + 2), (node, end));
        }
    }

    [Fact]
    public void Growth_happens_on_the_insert_after_three_quarters_full()
    {
        var memo = new MemoTable();
        int slots = memo.Capacity;
        int limit = (int)(slots * MemoTable.MaxLoad);
        for (int i = 0; i < limit; i++) memo.Set(0, i, i, i);
        Assert.Equal(slots, memo.Capacity);
        memo.Set(0, limit, 0, 0);
        Assert.Equal(slots * 2, memo.Capacity);
        Assert.Equal(192, limit);
    }

    [Fact]
    public void Overwriting_a_key_leaves_count_unchanged()
    {
        var memo = new MemoTable();
        memo.Set(2, 3, 1, 1);
        memo.Set(4, 5, 1, 1);
        memo.Set(2, 3, 9, 9);
        Assert.Equal(2, memo.Count);
    }

    [Fact]
    public void Generation_wrap_does_not_resurrect_stale_entries()
    {
        var memo = new MemoTable();
        memo.Set(1, 1, 7, 8); // stamped with generation 1
        memo.Generation = int.MaxValue - 1;
        memo.Clear(); // wraps back to generation 1
        Assert.Equal(0, memo.Count);
        Assert.False(memo.TryGet(1, 1, out _, out _));
        memo.Set(2, 2, 3, 4);
        Assert.True(memo.TryGet(2, 2, out int node, out _));
        Assert.Equal(3, node);
    }

    [Fact]
    public void A_failure_with_no_prefix_candidate_is_not_memoized()
    {
        var calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
        using (var result = calc.Parse("x = ;", CalcModule.Program)) Assert.False(result.Success);

        var memo = ParseContext.Cached!.Memo;
        int ruleKey = CalcModule.Instance.Expr.GlobalId << 10; // minPrecedence 0, no call flags
        Assert.True(memo.Contains(ruleKey, 0)); // `x` parsed, so successes are still memoized
        for (int position = 2; position <= 5; position++)
            Assert.False(memo.Contains(ruleKey, position), $"failure memoized at {position}");
    }
}
