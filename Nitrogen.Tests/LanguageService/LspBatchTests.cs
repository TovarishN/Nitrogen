using System.Text.Json;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Planning a batch of messages read together: coalescing changes and cancelling requests.</summary>
public class LspBatchTests
{
    static JsonElement M(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static JsonElement Change(string uri, int version, string text) => M(
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"version\":" + version
        + "},\"contentChanges\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":0}},\"text\":\"" + text + "\"}]}}");

    static JsonElement Request(int id) => M("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"textDocument/hover\",\"params\":{}}");

    static JsonElement Cancel(int id) => M("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":" + id + "}}");

    [Fact]
    public void Adjacent_changes_to_one_document_merge()
    {
        var steps = LspBatch.Plan([Change("file:///a", 2, "x"), Change("file:///a", 3, "y"), Change("file:///a", 4, "z")]);
        var change = Assert.IsType<LspBatch.Change>(Assert.Single(steps));
        Assert.Equal(("file:///a", 4), (change.Uri, change.Version));
        Assert.Equal(["x", "y", "z"], change.Changes.Select(c => c.Text));
        Assert.NotNull(change.Changes[0].Range);
    }

    [Fact]
    public void Adjacent_changes_to_two_documents_give_one_step_each_in_first_change_order()
    {
        var steps = LspBatch.Plan([Change("file:///b", 2, "1"), Change("file:///a", 2, "2"), Change("file:///b", 3, "3")]);
        Assert.Equal([("file:///b", 3, "13"), ("file:///a", 2, "2")],
            steps.Cast<LspBatch.Change>().Select(c => (c.Uri, c.Version, string.Concat(c.Changes.Select(x => x.Text)))));
    }

    [Fact]
    public void A_request_between_changes_keeps_them_apart()
    {
        var steps = LspBatch.Plan([Change("file:///a", 2, "x"), Request(5), Change("file:///a", 3, "y")]);
        Assert.Collection(steps,
            s => Assert.Equal(2, Assert.IsType<LspBatch.Change>(s).Version),
            s => Assert.IsType<LspBatch.Message>(s),
            s => Assert.Equal(3, Assert.IsType<LspBatch.Change>(s).Version));
    }

    [Fact]
    public void A_request_cancelled_in_the_same_batch_is_a_cancelled_step()
    {
        var steps = LspBatch.Plan([Request(5), Request(6), Cancel(5)]);
        Assert.Collection(steps,
            s => Assert.Equal(5, Assert.IsType<LspBatch.Cancelled>(s).Id.GetInt32()),
            s => Assert.Equal(6, Assert.IsType<LspBatch.Message>(s).Element.GetProperty("id").GetInt32()));
    }

    [Fact]
    public void A_cancel_without_its_request_gives_no_steps()
    {
        Assert.Empty(LspBatch.Plan([Cancel(42)]));
    }

    [Fact]
    public void Steps_after_exit_are_dropped()
    {
        var steps = LspBatch.Plan([M("""{"jsonrpc":"2.0","method":"exit"}"""), Request(5)]);
        Assert.Equal("exit", Assert.IsType<LspBatch.Message>(Assert.Single(steps)).Element.GetProperty("method").GetString());
    }

    [Fact]
    public void A_change_that_cannot_be_read_is_a_message_step()
    {
        var steps = LspBatch.Plan([M("""{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":5}}""")]);
        Assert.IsType<LspBatch.Message>(Assert.Single(steps));
    }
}
