using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>nitrogen.json grammar languages: editing the grammar re-serves its samples (issue 238).</summary>
public sealed class GrammarLoopTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-ide-").FullName;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    static string Uri(string path) => new System.Uri(path).AbsoluteUri;

    const string Config = """{ "languages": [ { "name": "greet", "extensions": [".greet"], "grammars": ["grammars/*.ngr"], "start": "Greet.Hello", "tokens": { } } ] }""";

    [Fact]
    public void Editing_the_grammar_reserves_its_samples_and_a_broken_grammar_keeps_the_last_good_one()
    {
        Write("nitrogen.json", Config);
        string grammar = Write("grammars/greet.ngr", WorkspaceTests.Greet);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);

        string sample = Uri(Path.Combine(_root, "a.greet"));
        Assert.NotEmpty(service.Open(sample, 1, "hello bob"));
        Assert.Empty(service.Diagnostics(sample));

        var affected = service.Open(Uri(grammar), 1, WorkspaceTests.Greet.Replace("Name:Word;", "Name:Word \"!\";"));
        Assert.Contains(sample, affected);
        Assert.Single(service.Diagnostics(sample)); // the grammar in the editor now wants "!"

        service.Change(Uri(grammar), 2, WorkspaceTests.Greet.Replace("Name:Word;", "Name:;"));
        Assert.Contains(service.Diagnostics(Uri(grammar)), d => d.Code == "NGR0001");
        Assert.Single(service.Diagnostics(sample)); // the last good grammar still serves it

        service.Change(Uri(grammar), 3, WorkspaceTests.Greet);
        Assert.Empty(service.Diagnostics(sample));
        Assert.DoesNotContain(service.Diagnostics(Uri(grammar)), d => d.Code.StartsWith("NGR", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_opened_before_its_grammar_compiles_is_served_once_it_does()
    {
        Write("nitrogen.json", Config);
        string grammar = Write("grammars/greet.ngr", WorkspaceTests.Greet.Replace("Name:Word;", "Name:;"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);

        string sample = Uri(Path.Combine(_root, "a.greet"));
        Assert.Empty(service.Open(sample, 1, "hello bob"));
        Assert.False(service.IsOpen(sample));

        File.WriteAllText(grammar, WorkspaceTests.Greet);
        Assert.Contains(sample, service.FileChanged(grammar));
        Assert.True(service.IsOpen(sample));
        Assert.Empty(service.Diagnostics(sample));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_fixed_config_root_serves_its_language_and_ignores_the_workspace_one()
    {
        // The bundled language wants "!"; the workspace's own nitrogen.json would accept "hello bob".
        Write("bundle/nitrogen.json", Config);
        Write("bundle/grammars/greet.ngr", WorkspaceTests.Greet.Replace("Name:Word;", "Name:Word \"!\";"));
        Write("workspace/nitrogen.json", Config);
        Write("workspace/grammars/greet.ngr", WorkspaceTests.Greet);
        string sample = Uri(Path.Combine(_root, "workspace", "a.greet"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        string[] bodies =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + Uri(Path.Combine(_root, "workspace")) + "\",\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + sample + "\",\"languageId\":\"greet\",\"version\":1,\"text\":\"hello bob\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"shutdown\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
        ];
        var input = new MemoryStream(bodies.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
        var output = new MemoryStream();
        await new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null, Path.Combine(_root, "bundle"))
            .RunAsync(CancellationToken.None);

        output.Position = 0;
        var counts = new List<int>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message)
                if (message.RootElement.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics"
                    && message.RootElement.GetProperty("params").GetProperty("uri").GetString() == sample)
                    counts.Add(message.RootElement.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(1, counts.Last()); // "!" missing: the bundled grammar, not the workspace's
    }

    [Theory]
    [InlineData("missing/nitrogen.json", "no file")]
    [InlineData("bundle/other.json", "nitrogen.json")]
    public void An_unusable_lsp_config_is_rejected(string relative, string expected)
    {
        Write("bundle/other.json", Config);
        Assert.Null(LspCommand.ConfigRoot(Path.Combine(_root, relative), out string error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_lsp_config_names_its_directory()
    {
        Write("bundle/nitrogen.json", Config);
        Assert.Equal(Path.Combine(_root, "bundle"), LspCommand.ConfigRoot(Path.Combine(_root, "bundle", "nitrogen.json"), out _));
    }

    [Fact]
    public void A_grammar_with_checks_but_no_properties_reports_its_checks()
    {
        Write("nitrogen.json", Config);
        Write("grammars/greet.ngr", """
            syntax module Greet
            {
              token Word = ['a'..'z']+;
              syntax Hello = "hello" Name:Word
              {
                check GR0001 Name.Text != "bob" : "bob is not welcome" at Name;
              }
            }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);

        string sample = Uri(Path.Combine(_root, "a.greet"));
        service.Open(sample, 1, "hello bob");
        var diagnostic = Assert.Single(service.Diagnostics(sample));
        Assert.Equal(("GR0001", "bob is not welcome"), (diagnostic.Code, diagnostic.Message));
        service.Change(sample, 2, "hello ann");
        Assert.Empty(service.Diagnostics(sample));
    }

    [Fact]
    public async Task The_workspace_root_and_watched_grammar_changes_over_the_protocol()
    {
        Write("nitrogen.json", Config);
        string grammar = Write("grammars/greet.ngr", WorkspaceTests.Greet);
        string sample = Uri(Path.Combine(_root, "a.greet"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + Uri(_root) + "\",\"capabilities\":{}}}";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + sample + "\",\"languageId\":\"greet\",\"version\":1,\"text\":\"hello bob\"}}}";
        string watched = "{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{\"changes\":[{\"uri\":\"" + Uri(grammar) + "\",\"type\":2}]}}";

        // The grammar changes on disk between the open and the watched-files notification.
        var input = new BlockingInput(
            [initialize, "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}", open],
            () => File.WriteAllText(grammar, WorkspaceTests.Greet.Replace("Name:Word;", "Name:Word \"!\";")),
            [watched, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"shutdown\"}", "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}"]);
        var output = new MemoryStream();
        await new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null).RunAsync(CancellationToken.None);

        output.Position = 0;
        var counts = new List<int>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message)
                if (message.RootElement.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics"
                    && message.RootElement.GetProperty("params").GetProperty("uri").GetString() == sample)
                    counts.Add(message.RootElement.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(new[] { 0, 1 }, counts); // clean with the first grammar, then "!" missing after the edit on disk
    }

    /// <summary>Framed messages in two batches, with an action run between them when the first batch is consumed.</summary>
    sealed class BlockingInput(string[] first, Action between, string[] second) : Stream
    {
        readonly MemoryStream _first = new(first.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
        readonly MemoryStream _second = new(second.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
        bool _switched;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _first.Read(buffer, offset, count);
            if (read > 0) return read;
            if (!_switched)
            {
                _switched = true;
                between();
            }
            return _second.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    const string Checked = """
        syntax module Checked
        {
          token Word = ['a'..'z']+;
          syntax Hello = "hello" Name:Word
          {
            out Size : int = 0;
            Size = Lengths.Of(Name.Text);
            check GR0001 Size < 5 : $"'{Name}' is too long";
          }
        }
        """;

    const string CheckedConfig = """{ "languages": [ { "name": "checked", "extensions": [".checked"], "grammars": ["grammars/*.ngr"], "sources": ["types/*.cs"], "usings": ["Checked.Helpers"], "start": "Checked.Hello" } ] }""";

    [Fact]
    public void A_workspace_grammar_uses_its_helper_sources_and_recompiles_when_they_change()
    {
        Write("nitrogen.json", CheckedConfig);
        Write("grammars/checked.ngr", Checked);
        string helper = Write("types/Lengths.cs", "namespace Checked.Helpers; public static class Lengths { public static int Of(string text) => text.Length; }");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);

        string sample = Uri(Path.Combine(_root, "a.checked"));
        service.Open(sample, 1, "hello roberta");
        Assert.Equal("GR0001", Assert.Single(service.Diagnostics(sample)).Code);

        File.WriteAllText(helper, "namespace Checked.Helpers; public static class Lengths { public static int Of(string text) => 0; }");
        Assert.Contains(sample, service.FileChanged(helper));
        Assert.Empty(service.Diagnostics(sample));
    }

    [Fact]
    public void A_lowerer_that_throws_is_an_error_on_its_syntax()
    {
        Write("nitrogen.json", """{ "languages": [ { "name": "boom", "extensions": [".boom"], "grammars": ["boom.ngr"], "start": "Boom.Doc", "sources": ["Boom.cs"], "namespace": "Boom.Syntax" } ] }""");
        Write("boom.ngr", """
            syntax module Boom
            {
              syntax Doc = Items:Item*;
              syntax Item = "boom" ";";
            }
            """);
        Write("Boom.cs", """
            using Nitrogen.Semantic;
            using Boom.Syntax;

            public static class BoomSemantics
            {
                static readonly OperationSignature Explode = new("Boom.Explode", SemanticTypes.Scalar);
                public static readonly SemanticModule Module = new("Boom", [], [], [Explode],
                    [new LoweringRegistration(BoomKinds.Item, Explode.Id, (_, _) => throw new System.InvalidOperationException("kaboom"))]);
            }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string sample = Uri(Path.Combine(_root, "a.boom"));
        service.Open(sample, 1, "boom; boom;");

        var errors = service.Diagnostics(sample);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d => Assert.Equal(("NH0004", ServiceSeverity.Error), (d.Code, d.Severity)));
        Assert.Contains("kaboom", errors[0].Message);
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 6), new DocumentPosition(0, 11)), errors[1].Range);
    }

    [Fact]
    public void A_lowering_blocked_by_a_name_an_open_scope_accepts_is_no_editor_error()
    {
        Write("nitrogen.json", """{ "languages": [ { "name": "open", "extensions": [".open"], "grammars": ["open.ngr"], "start": "Open.Doc", "sources": ["Open.cs"] } ] }""");
        Write("open.ngr", """
            syntax module Open
            {
              symbols { value }
              token N = ['a'..'z']+;
              token D = ['0'..'9']+;
              syntax Doc = Items:Item*;
              syntax Item = Gen / Use;
              syntax Gen = "gen" Name:GenName ";" declares value Name type Core.Scalar;
              syntax GenName = Head:N "$" Seq:D dynamic;
              syntax Use = "use" Value:Ref ";" lowers Open.Use(Value);
              syntax Ref = Name:N references value Name;
            }
            """);
        Write("Open.cs", """
            using Nitrogen.Semantic;

            public static class OpenSemantics
            {
                static readonly OperationSignature Use = new("Open.Use", SemanticTypes.Scalar, SemanticTypes.Scalar);
                public static readonly SemanticModule Module = new("Open", [], [], [Use]);
            }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string sample = Uri(Path.Combine(_root, "a.open"));
        service.Open(sample, 1, "gen x$1; use y;");

        Assert.Equal("NH0002", Assert.Single(service.InspectDocument(sample)!.Diagnostics).Code); // lowering is blocked ...
        Assert.Empty(service.Diagnostics(sample));                                                 // ... but nothing is wrong
    }
}
