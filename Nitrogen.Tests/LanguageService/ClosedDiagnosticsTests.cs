using System.Text.Json;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Diagnostics of closed workspace files and grammars, checked while the server is idle.</summary>
public sealed class ClosedDiagnosticsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-closed-").FullName;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(string grammar = WorkspaceIndexTests.Grammar)
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", grammar);
        var service = new NitrogenLanguageService(new LanguageRegistry());
        service.ConfigureWorkspace(_root);
        service.IndexWorkspace(_root);
        return service;
    }

    [Fact]
    public void A_closed_file_reports_its_errors()
    {
        Write("a.links", "use alpha;");
        using var service = Service();
        Assert.Contains(service.Diagnostics(Uri("a.links")), d => d.Code == "NB0001");
    }

    [Fact]
    public void A_closed_grammar_reports_its_compile_errors()
    {
        using var service = Service(WorkspaceIndexTests.Grammar.Replace("token Word = ['a'..'z']+;", "token Word = ;"));
        Assert.Contains(service.Diagnostics(Uri("links.ngr")), d => d.Severity == ServiceSeverity.Error);
        Assert.Contains(Uri("links.ngr"), service.ClosedDiagnosticFiles());
    }

    [Fact]
    public void Closed_diagnostic_files_are_the_closed_files_and_grammars()
    {
        Write("a.links", "def alpha;");
        Write("b.links", "def beta;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "def beta;");
        Assert.Equal([Uri("a.links"), Uri("links.ngr")], service.ClosedDiagnosticFiles());
    }

    const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";

    static string Open(string uri, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri
        + "\",\"languageId\":\"links\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";

    static string Change(string uri, int version, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"version\":" + version
        + "},\"contentChanges\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]}}";

    static string Close(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"}}}";

    static string Hover(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"},\"position\":{\"line\":0,\"character\":4}}}";

    static string Deleted(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{\"changes\":[{\"uri\":\"" + uri + "\",\"type\":3}]}}";

    /// <summary>A session in lockstep (each item once the server is idle): initialize, initialized, the items, shutdown, exit.</summary>
    async Task<List<JsonElement>> Session(params object[] items)
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", WorkspaceIndexTests.Grammar);
        using var service = new NitrogenLanguageService(new LanguageRegistry());
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri + "\",\"capabilities\":{}}}";
        var input = new LockstepInput([initialize, Initialized, .. items,
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}"""]);
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null);
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return messages;
    }

    /// <summary>The publishes for one file, in order: its version (null when closed) and how many diagnostics.</summary>
    static List<(int? Version, int Count)> Publishes(List<JsonElement> messages, string uri) => messages
        .Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && m.GetProperty("params").GetProperty("uri").GetString() == uri)
        .Select(m => m.GetProperty("params"))
        .Select(p => (p.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null,
            p.GetProperty("diagnostics").GetArrayLength()))
        .ToList();

    [Fact]
    public async Task A_closed_file_with_an_error_is_published_after_initialization()
    {
        Write("a.links", "use alpha;");
        var messages = await Session();
        Assert.Equal(new (int?, int)[] { (null, 1) }, Publishes(messages, Uri("a.links")));
        Assert.Empty(Publishes(messages, Uri("links.ngr"))); // clean, never published: nothing sent
    }

    [Fact]
    public async Task Declaring_the_name_in_an_open_file_clears_the_closed_one()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("b.links"), "def beta;"), Change(Uri("b.links"), 2, "def alpha;"));
        Assert.Equal(new (int?, int)[] { (null, 1), (null, 0) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task A_batch_that_changes_nothing_publishes_nothing_for_closed_files()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("b.links"), "def beta;"), Hover(Uri("b.links")));
        Assert.Equal(new (int?, int)[] { (null, 1) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task A_deleted_closed_file_is_published_empty()
    {
        string path = Write("a.links", "use alpha;");
        var messages = await Session((Action)(() => File.Delete(path)), Deleted(Uri("a.links")));
        Assert.Equal(new (int?, int)[] { (null, 1), (null, 0) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task An_opened_closed_file_is_published_with_its_version_and_not_as_closed()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("a.links"), "use alpha;"), Change(Uri("a.links"), 2, "def alpha;\nuse alpha;"));
        Assert.Equal(new (int?, int)[] { (null, 1), (1, 1), (2, 0) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task A_closed_file_is_cleared_then_published_again_as_closed()
    {
        Write("a.links", "use alpha;");
        var messages = await Session(Open(Uri("a.links"), "use alpha;"), Close(Uri("a.links")));
        Assert.Equal(new (int?, int)[] { (null, 1), (1, 1), (null, 0), (null, 1) }, Publishes(messages, Uri("a.links")));
    }

    [Fact]
    public async Task After_a_burst_the_last_publish_of_a_closed_file_is_its_final_state()
    {
        Write("a.links", "use alpha;");
        string b = Uri("b.links");
        var messages = await Session((object)new[] { Open(b, "def beta;"), Change(b, 2, "def gamma;"), Change(b, 3, "def alpha;") }); // one burst
        Assert.Equal(((int?)null, 0), Publishes(messages, Uri("a.links"))[^1]);
    }
}
