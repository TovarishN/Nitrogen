using Xunit;

namespace Nitrogen.Tests;

public class MemoTableTests
{
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
}
