using System.Text.Json;

namespace Nitrogen.LanguageService.Lsp;

/// <summary>
/// Plans a batch of messages read together (spec: sync coalescing). A run of consecutive didChange
/// notifications becomes one change per document, in order of each document's first change, and a
/// request whose $/cancelRequest is in the batch becomes a cancelled step. Every other message is a
/// step of its own, in order; the steps after exit are dropped.
/// </summary>
internal static class LspBatch
{
    internal abstract record Step;

    /// <summary>A message handled as it is.</summary>
    internal sealed record Message(JsonElement Element) : Step;

    /// <summary>A document's content changes from consecutive didChange notifications, in order, at the last version.</summary>
    internal sealed record Change(string Uri, int Version, IReadOnlyList<TextDocumentContentChangeEvent> Changes) : Step;

    /// <summary>A request whose $/cancelRequest is in the same batch.</summary>
    internal sealed record Cancelled(JsonElement Id) : Step;

    public static IReadOnlyList<Step> Plan(IReadOnlyList<JsonElement> messages)
    {
        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
            if (MethodOf(message) == "$/cancelRequest" && message.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id))
                cancelled.Add(id.GetRawText());

        var steps = new List<Step>();
        var run = new List<Change>(); // the current run of didChange notifications, one entry per document
        foreach (var message in messages)
        {
            string method = MethodOf(message);
            if (method == "textDocument/didChange" && ChangeOf(message) is { } change)
            {
                int i = run.FindIndex(c => c.Uri == change.Uri);
                if (i < 0) run.Add(change);
                else run[i] = run[i] with { Version = change.Version, Changes = [.. run[i].Changes, .. change.Changes] };
                continue;
            }
            steps.AddRange(run);
            run.Clear();
            if (method == "$/cancelRequest") continue;
            if (method.Length > 0 && message.TryGetProperty("id", out var requestId) && cancelled.Contains(requestId.GetRawText()))
                steps.Add(new Cancelled(requestId.Clone()));
            else
                steps.Add(new Message(message));
            if (method == "exit") return steps;
        }
        steps.AddRange(run);
        return steps;
    }

    static string MethodOf(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "";

    /// <summary>The change a didChange notification makes; null when it can't be read (it is then handled, and reported, as a message).</summary>
    static Change? ChangeOf(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters)) return null;
        try
        {
            var change = parameters.Deserialize(LspJson.Default.DidChangeTextDocumentParams);
            if (change?.TextDocument?.Uri is null || change.ContentChanges is null) return null;
            return new Change(change.TextDocument.Uri, change.TextDocument.Version, change.ContentChanges);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
