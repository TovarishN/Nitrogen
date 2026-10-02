using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SemanticCacheTests
{
    [Fact]
    public void Reading_one_property_does_not_allocate_a_cache_for_every_node_in_a_large_file()
    {
        string source = "unit a { " + string.Concat(Enumerable.Repeat("let x = 1;", 2000)) + " }";
        using var parsed = FileBindingTests.Scopes.Parse(source, ScopesModule.File);
        Assert.True(parsed.Success);
        var project = new Project(FileBindingTests.Scopes);
        project.Set("cache.scopes", parsed.Tree);
        var file = new ProjectSemantics(project)["cache.scopes"];
        var property = new Property<string>("rare", inherited: false, static () => "cached");
        long before = GC.GetAllocatedBytesForCurrentThread();
        string value = file.Get(file.Tree.Root, property);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("cached", value);
        // A broad deterministic allocation budget, not a timing test: touching
        // one node must not reserve arrays proportional to the entire file.
        Assert.InRange(allocated, 0, 4096);
    }

    [Fact]
    public void Dense_reads_keep_each_nodes_memoized_value_when_the_cache_expands()
    {
        string source = "unit a { " + string.Concat(Enumerable.Repeat("let x = 1;", 100)) + " }";
        using var parsed = FileBindingTests.Scopes.Parse(source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        project.Set("cache.scopes", parsed.Tree);
        var file = new ProjectSemantics(project)["cache.scopes"];
        int computed = 0;
        var property = new Property<int>("identity", inherited: false, () => ++computed);
        int[] forward = Enumerable.Range(0, file.Tree.NodeCount).Select(node => file.Get(node, property)).ToArray();
        Assert.Equal(Enumerable.Range(1, file.Tree.NodeCount), forward);
        foreach (int node in Enumerable.Range(0, file.Tree.NodeCount).Reverse())
            Assert.Equal(forward[node], file.Get(node, property));
        Assert.Equal(file.Tree.NodeCount, computed);
    }

    [Fact]
    public void Nested_evaluation_keeps_in_progress_and_completed_entries_when_the_cache_expands()
    {
        string source = "unit a { " + string.Concat(Enumerable.Repeat("let x = 1;", 100)) + " }";
        using var parsed = FileBindingTests.Scopes.Parse(source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        project.Set("cache.scopes", parsed.Tree);
        var file = new ProjectSemantics(project)["cache.scopes"];
        int computed = 0;
        Property<int>? property = null;
        property = new Property<int>("nested", inherited: false, () =>
        {
            int node = computed++;
            return node + 1 < file.Tree.NodeCount ? file.Get(node + 1, property!) + 1
                : node + 1 == file.Tree.NodeCount ? file.Get(0, property!) : 0;
        });
        Assert.Equal(file.Tree.NodeCount - 1, file.Get(0, property));
        foreach (int node in Enumerable.Range(0, file.Tree.NodeCount))
            Assert.Equal(file.Tree.NodeCount - node - 1, file.Get(node, property));
        Assert.Equal(file.Tree.NodeCount + 1, computed);
        Assert.Contains(file.Diagnostics(), error => error.Code == SemanticCodes.Cycle);
    }
}
