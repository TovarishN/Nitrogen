using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Workspace files of a nitrogen.json language are bound while closed (spec: workspace indexing).</summary>
public sealed class WorkspaceIndexTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-index-").FullName;

    internal const string Config = """{ "languages": [ { "name": "links", "extensions": [".links"], "grammars": ["links.ngr"], "start": "Links.File" } ] }""";

    internal const string Grammar = """
        syntax module Links
        {
          symbols { item }
          token Word = ['a'..'z']+;
          syntax File = Lines:Line*;
          syntax Line = Decl / Use;
          syntax Decl = "def" Name:Word ";" declares item Name export;
          syntax Use  = "use" Target:Word ";" references item Target;
        }
        """;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(bool index = true)
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var service = new NitrogenLanguageService(new LanguageRegistry());
        service.ConfigureWorkspace(_root);
        if (index) service.IndexWorkspace(_root);
        return service;
    }

    static IReadOnlyList<string> Codes(NitrogenLanguageService service, string uri) => service.Diagnostics(uri).Select(d => d.Code).ToList();

    [Fact]
    public void Without_indexing_a_closed_declaration_is_unresolved()
    {
        Write("a.links", "def alpha;");
        using var service = Service(index: false);
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));
    }

    [Fact]
    public void A_closed_declaration_resolves()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Empty(Codes(service, Uri("b.links")));
        Assert.False(service.IsOpen(Uri("a.links")));
    }

    [Fact]
    public void Definition_references_and_rename_reach_the_closed_file()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        var at = new DocumentPosition(0, 5);

        var definition = Assert.Single(service.Definition(Uri("b.links"), at));
        Assert.Equal((Uri("a.links"), 0, 4, 9), (definition.Uri, definition.Range.Start.Line, definition.Range.Start.Character, definition.Range.End.Character));
        Assert.Contains(service.References(Uri("b.links"), at, includeDeclaration: true), l => l.Uri == Uri("a.links"));
        var edits = service.Rename(Uri("b.links"), at, "beta");
        Assert.Equal(new[] { Uri("a.links"), Uri("b.links") }, edits.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Closing_an_edited_file_goes_back_to_its_disk_text()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        service.Open(Uri("a.links"), 1, "def gamma;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));

        Assert.Contains(Uri("b.links"), service.Close(Uri("a.links")));
        Assert.Empty(Codes(service, Uri("b.links")));
    }

    [Fact]
    public void Files_created_changed_and_deleted_on_disk_follow()
    {
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));

        string a = Write("a.links", "def alpha;");
        Assert.Contains(Uri("b.links"), service.FileChanged(a));
        Assert.Empty(Codes(service, Uri("b.links")));

        File.WriteAllText(a, "def gamma;");
        service.FileChanged(a);
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));

        File.WriteAllText(a, "def alpha;");
        service.FileChanged(a);
        File.Delete(a);
        Assert.Contains(Uri("b.links"), service.FileChanged(a));
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));
    }

    [Theory]
    [InlineData("bin/a.links")]
    [InlineData("obj/a.links")]
    [InlineData("node_modules/a.links")]
    [InlineData(".git/a.links")]
    [InlineData("a.txt")]
    public void Build_output_hidden_folders_and_other_extensions_are_not_indexed(string path)
    {
        Write(path, "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");
        Assert.Contains("NB0001", Codes(service, Uri("b.links")));
    }

    [Fact]
    public void A_recompiled_grammar_keeps_closed_files_bound()
    {
        Write("a.links", "def alpha;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "use alpha;");

        string grammar = Write("links.ngr", Grammar + "\n// edited\n");
        service.FileChanged(grammar);

        Assert.Empty(Codes(service, Uri("b.links")));
    }

    async Task<List<JsonElement>> Session(string? fixedRoot, string workspace, string capabilities, params string[] after)
    {
        string[] bodies =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(workspace).AbsoluteUri + "\",\"capabilities\":" + capabilities + "}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}",
            .. after,
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"shutdown\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
        ];
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        var input = new LockstepInput(bodies);
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null, fixedRoot);
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return messages;
    }

    string Open(string relative, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri(relative) + "\",\"languageId\":\"links\",\"version\":1,\"text\":\"" + text + "\"}}}";

    static int LastDiagnosticCount(List<JsonElement> messages, string uri) => messages
        .Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && m.GetProperty("params").GetProperty("uri").GetString() == uri)
        .Select(m => m.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).Last();

    [Fact]
    public async Task With_a_fixed_config_the_client_workspace_is_still_indexed()
    {
        Write("bundle/nitrogen.json", Config);
        Write("bundle/links.ngr", Grammar);
        Write("workspace/a.links", "def alpha;");
        var messages = await Session(Path.Combine(_root, "bundle"), Path.Combine(_root, "workspace"), "{}", Open("workspace/b.links", "use alpha;"));
        Assert.Equal(0, LastDiagnosticCount(messages, Uri("workspace/b.links")));
    }

    [Fact]
    public async Task A_client_with_dynamic_registration_is_asked_to_watch_the_indexed_extensions()
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var messages = await Session(null, _root, """{"workspace":{"didChangeWatchedFiles":{"dynamicRegistration":true}}}""");
        var request = Assert.Single(messages, m => m.TryGetProperty("method", out var method) && method.GetString() == "client/registerCapability");
        var registration = request.GetProperty("params").GetProperty("registrations")[0];
        Assert.Equal("workspace/didChangeWatchedFiles", registration.GetProperty("method").GetString());
        Assert.Equal(new[] { "**/*.links", "**/*.ngr", "**/nitrogen.json" },
            registration.GetProperty("registerOptions").GetProperty("watchers").EnumerateArray().Select(w => w.GetProperty("globPattern").GetString()));
    }

    [Fact]
    public async Task A_client_without_dynamic_registration_is_not_asked()
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var messages = await Session(null, _root, "{}");
        Assert.DoesNotContain(messages, m => m.TryGetProperty("method", out var method) && method.GetString() == "client/registerCapability");
    }

    [Fact]
    public async Task A_response_from_the_client_gets_no_reply()
    {
        Write("nitrogen.json", Config);
        Write("links.ngr", Grammar);
        var messages = await Session(null, _root, "{}", """{"jsonrpc":"2.0","id":"nitrogen-watch","result":null}""");
        Assert.DoesNotContain(messages, m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "nitrogen-watch");
    }
}
