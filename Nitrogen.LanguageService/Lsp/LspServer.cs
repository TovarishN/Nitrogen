using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nitrogen.LanguageService.Lsp;

/// <summary>
/// The LSP server loop (issue 238): one message at a time, in order. A request that fails gets an
/// error response and the loop goes on; broken framing ends the session. Diagnostics are pushed
/// after every document change, for every document the change may affect.
/// </summary>
/// <param name="fixedRoot">The directory whose <c>nitrogen.json</c> configures the languages, whatever root the client sends (<c>nitrogen lsp --config</c>); null to use the client's root.</param>
public sealed class LspServer(JsonRpcConnection connection, NitrogenLanguageService service, TextWriter log, string? fixedRoot = null)
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestFailed = -32803;

    bool _shutdown;
    string? _root;

    /// <returns>The process exit code: 0 after <c>shutdown</c> then <c>exit</c>; 1 for <c>exit</c> without shutdown, end of input or broken framing.</returns>
    public async Task<int> RunAsync(CancellationToken cancel)
    {
        while (true)
        {
            JsonDocument? message;
            try
            {
                message = await connection.ReadAsync(cancel);
            }
            catch (InvalidDataException error)
            {
                log.WriteLine($"nitrogen lsp: {error.Message}");
                return 1;
            }
            if (message is null) return 1;

            using (message)
            {
                var root = message.RootElement;
                string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                bool isRequest = root.TryGetProperty("id", out var idElement);
                var id = isRequest ? idElement.Clone() : default;
                var parameters = root.TryGetProperty("params", out var p) ? p : default;
                if (method == "exit") return _shutdown ? 0 : 1;

                try
                {
                    if (isRequest) await HandleRequestAsync(method, id, parameters, cancel);
                    else await HandleNotificationAsync(method, parameters, cancel);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (isRequest) await RespondErrorAsync(id, error switch { RenameRefusedException => RequestFailed, JsonException => InvalidParams, _ => InternalError }, error.Message, cancel);
                    else log.WriteLine($"nitrogen lsp: {method}: {error.Message}");
                }
            }
        }
    }

    async Task HandleRequestAsync(string method, JsonElement id, JsonElement parameters, CancellationToken cancel)
    {
        switch (method)
        {
            case "initialize":
                if (fixedRoot is not null) _root = fixedRoot;
                else if (parameters.ValueKind == JsonValueKind.Object)
                {
                    var initialize = parameters.Deserialize(LspJson.Default.InitializeParams);
                    string? root = initialize?.RootUri ?? initialize?.WorkspaceFolders?.FirstOrDefault()?.Uri;
                    if (root is not null && System.Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.IsFile) _root = uri.LocalPath;
                }
                await RespondAsync(id, new InitializeResult(
                    new ServerCapabilities(1, new SemanticTokensOptions(SemanticTokenEncoding.Legend, Full: true), DocumentSymbolProvider: true,
                        DefinitionProvider: true, ReferencesProvider: true, DocumentHighlightProvider: true, HoverProvider: true,
                        RenameProvider: new RenameOptions(PrepareProvider: true),
                        CompletionProvider: new CompletionOptions(["."])),
                    new ServerInfo("nitrogen", "0.1")), LspJson.Default.InitializeResult, cancel);
                break;
            case "shutdown":
                _shutdown = true;
                await RespondNullAsync(id, cancel);
                break;
            case "textDocument/semanticTokens/full":
            {
                string uri = Params(parameters, LspJson.Default.TextDocumentParams).TextDocument.Uri;
                await RespondAsync(id, new SemanticTokensResult(SemanticTokenEncoding.Encode(service.SemanticTokens(uri))),
                    LspJson.Default.SemanticTokensResult, cancel);
                break;
            }
            case "textDocument/documentSymbol":
            {
                string uri = Params(parameters, LspJson.Default.TextDocumentParams).TextDocument.Uri;
                await RespondAsync(id, service.DocumentSymbols(uri).Select(Symbol).ToArray(), LspJson.Default.LspDocumentSymbolArray, cancel);
                break;
            }
            case "textDocument/definition":
            {
                var (uri, position) = At(parameters);
                await RespondAsync(id, service.Definition(uri, position).Select(Location).ToArray(), LspJson.Default.LspLocationArray, cancel);
                break;
            }
            case "textDocument/references":
            {
                var request = Params(parameters, LspJson.Default.ReferenceParams);
                var locations = service.References(request.TextDocument.Uri, Position(request.Position), request.Context.IncludeDeclaration);
                await RespondAsync(id, locations.Select(Location).ToArray(), LspJson.Default.LspLocationArray, cancel);
                break;
            }
            case "textDocument/documentHighlight":
            {
                var (uri, position) = At(parameters);
                var highlights = service.Highlights(uri, position).Select(h => new LspDocumentHighlight(Range(h.Range), (int)h.Kind)).ToArray();
                await RespondAsync(id, highlights, LspJson.Default.LspDocumentHighlightArray, cancel);
                break;
            }
            case "textDocument/hover":
            {
                var (uri, position) = At(parameters);
                if (service.Hover(uri, position) is { } hover)
                    await RespondAsync(id, new LspHover(new MarkupContent("markdown", hover.Markdown), Range(hover.Range)), LspJson.Default.LspHover, cancel);
                else
                    await RespondNullAsync(id, cancel);
                break;
            }
            case "textDocument/completion":
            {
                var (uri, position) = At(parameters);
                var items = service.Completion(uri, position)
                    .Select(i => new LspCompletionItem(i.Label, (int)i.Kind, i.Detail, new LspTextEdit(Range(i.Replace), i.Label), i.SortText))
                    .ToArray();
                await RespondAsync(id, items, LspJson.Default.LspCompletionItemArray, cancel);
                break;
            }
            case "textDocument/prepareRename":
            {
                var (uri, position) = At(parameters);
                await RespondAsync(id, Range(service.PrepareRename(uri, position)), LspJson.Default.LspRange, cancel);
                break;
            }
            case "textDocument/rename":
            {
                var request = Params(parameters, LspJson.Default.RenameParams);
                var edits = service.Rename(request.TextDocument.Uri, Position(request.Position), request.NewName);
                var changes = edits.ToDictionary(e => e.Key, e => e.Value.Select(x => new LspTextEdit(Range(x.Range), x.NewText)).ToArray());
                await RespondAsync(id, new WorkspaceEdit(changes), LspJson.Default.WorkspaceEdit, cancel);
                break;
            }
            default:
                await RespondErrorAsync(id, MethodNotFound, $"'{method}' is not supported", cancel);
                break;
        }
    }

    async Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancel)
    {
        switch (method)
        {
            case "textDocument/didOpen":
            {
                var item = Params(parameters, LspJson.Default.DidOpenTextDocumentParams).TextDocument;
                await PublishAsync(service.Open(item.Uri, item.Version, item.Text), cancel);
                break;
            }
            case "textDocument/didChange":
            {
                var change = Params(parameters, LspJson.Default.DidChangeTextDocumentParams);
                // Full sync: the last change carries the whole text.
                await PublishAsync(service.Change(change.TextDocument.Uri, change.TextDocument.Version, change.ContentChanges[^1].Text), cancel);
                break;
            }
            case "textDocument/didClose":
            {
                string uri = Params(parameters, LspJson.Default.DidCloseTextDocumentParams).TextDocument.Uri;
                var others = service.Close(uri);
                await NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(uri, null, []),
                    LspJson.Default.PublishDiagnosticsParams, cancel);
                await PublishAsync(others, cancel);
                break;
            }
            case "initialized":
                if (_root is not null) await PublishAsync(service.ConfigureWorkspace(_root), cancel);
                break;
            case "workspace/didChangeWatchedFiles":
                foreach (var change in Params(parameters, LspJson.Default.DidChangeWatchedFilesParams).Changes)
                    if (System.Uri.TryCreate(change.Uri, UriKind.Absolute, out var uri) && uri.IsFile)
                        await PublishAsync(service.FileChanged(uri.LocalPath), cancel);
                break;
            // $/cancelRequest, $/setTrace and the rest: nothing to do.
        }
    }

    async Task PublishAsync(IReadOnlyList<string> uris, CancellationToken cancel)
    {
        foreach (string uri in uris)
        {
            var diagnostics = service.Diagnostics(uri)
                .Select(d => new LspDiagnostic(Range(d.Range), (int)d.Severity, d.Code, "nitrogen", d.Message))
                .ToArray();
            await NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(uri, service.VersionOf(uri), diagnostics),
                LspJson.Default.PublishDiagnosticsParams, cancel);
        }
    }

    static LspDocumentSymbol Symbol(OutlineSymbol symbol) =>
        new(symbol.Name, symbol.Kind, (int)symbol.Outline, Range(symbol.Range), Range(symbol.SelectionRange),
            symbol.Children.Select(Symbol).ToArray());

    static (string Uri, DocumentPosition Position) At(JsonElement parameters)
    {
        var request = Params(parameters, LspJson.Default.TextDocumentPositionParams);
        return (request.TextDocument.Uri, Position(request.Position));
    }

    static DocumentPosition Position(LspPosition position) => new(position.Line, position.Character);

    static LspLocation Location(DocumentLocation location) => new(location.Uri, Range(location.Range));

    internal static LspRange Range(DocumentRange range) =>
        new(new LspPosition(range.Start.Line, range.Start.Character), new LspPosition(range.End.Line, range.End.Character));

    static T Params<T>(JsonElement parameters, JsonTypeInfo<T> type) =>
        parameters.ValueKind == JsonValueKind.Undefined
            ? throw new JsonException("missing params")
            : parameters.Deserialize(type) ?? throw new JsonException("null params");

    Task RespondAsync<T>(JsonElement id, T result, JsonTypeInfo<T> type, CancellationToken cancel) =>
        connection.WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WritePropertyName("result");
            JsonSerializer.Serialize(w, result, type);
            w.WriteEndObject();
        }, cancel);

    Task RespondNullAsync(JsonElement id, CancellationToken cancel) =>
        connection.WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteNull("result");
            w.WriteEndObject();
        }, cancel);

    Task RespondErrorAsync(JsonElement id, int code, string message, CancellationToken cancel) =>
        connection.WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }, cancel);

    Task NotifyAsync<T>(string method, T parameters, JsonTypeInfo<T> type, CancellationToken cancel) =>
        connection.WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WritePropertyName("params");
            JsonSerializer.Serialize(w, parameters, type);
            w.WriteEndObject();
        }, cancel);
}
