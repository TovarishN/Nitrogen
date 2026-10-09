using System.Text.Json.Serialization;

namespace Nitrogen.LanguageService.Lsp;

// The LSP subset issue 238 uses, named as the protocol names them (JSON is camelCase).

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <summary>A content change: <paramref name="Text"/> replaces <paramref name="Range"/>, or the whole text when there is no range.</summary>
public sealed record TextDocumentContentChangeEvent(string Text, LspRange? Range = null);

public sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, TextDocumentContentChangeEvent[] ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record LspDiagnostic(LspRange Range, int Severity, string Code, string Source, string Message);

public sealed record PublishDiagnosticsParams(string Uri, int? Version, LspDiagnostic[] Diagnostics);

/// <param name="TextDocumentSync">2: incremental, each change carrying the range it replaces.</param>
public sealed record ServerCapabilities(
    int TextDocumentSync,
    SemanticTokensOptions? SemanticTokensProvider = null,
    bool? DocumentSymbolProvider = null,
    bool? DefinitionProvider = null,
    bool? ReferencesProvider = null,
    bool? DocumentHighlightProvider = null,
    bool? HoverProvider = null,
    RenameOptions? RenameProvider = null,
    CompletionOptions? CompletionProvider = null,
    bool? InlayHintProvider = null,
    CodeActionOptions? CodeActionProvider = null,
    SignatureHelpOptions? SignatureHelpProvider = null,
    bool? FoldingRangeProvider = null,
    bool? SelectionRangeProvider = null,
    bool? WorkspaceSymbolProvider = null,
    bool? DocumentFormattingProvider = null,
    bool? DocumentRangeFormattingProvider = null,
    DocumentOnTypeFormattingOptions? DocumentOnTypeFormattingProvider = null);

public sealed record RenameOptions(bool PrepareProvider);

public sealed record WorkspaceFolder(string Uri, string Name);

public sealed record DidChangeWatchedFilesClientCapabilities(bool? DynamicRegistration);

public sealed record InlayHintWorkspaceClientCapabilities(bool? RefreshSupport);

public sealed record WorkspaceClientCapabilities(DidChangeWatchedFilesClientCapabilities? DidChangeWatchedFiles,
    InlayHintWorkspaceClientCapabilities? InlayHint = null);

public sealed record ClientCapabilities(WorkspaceClientCapabilities? Workspace);

/// <param name="SkipLanguages">Languages, by name or extension without the dot, whose tagged C# strings this server leaves to another (an editor plugin that carries the language).</param>
public sealed record InitializationOptions(string[]? SkipLanguages);

public sealed record InitializeParams(string? RootUri, WorkspaceFolder[]? WorkspaceFolders, ClientCapabilities? Capabilities = null,
    InitializationOptions? InitializationOptions = null);

public sealed record FileEvent(string Uri, int Type);

public sealed record DidChangeWatchedFilesParams(FileEvent[] Changes);

public sealed record CompletionOptions(string[] TriggerCharacters);

public sealed record LspCompletionItem(string Label, int Kind, string Detail, LspTextEdit TextEdit, string? SortText = null);

public sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, LspPosition Position);

public sealed record ReferenceContext(bool IncludeDeclaration);

public sealed record ReferenceParams(TextDocumentIdentifier TextDocument, LspPosition Position, ReferenceContext Context);

public sealed record RenameParams(TextDocumentIdentifier TextDocument, LspPosition Position, string NewName);

public sealed record LspLocation(string Uri, LspRange Range);

public sealed record LspDocumentHighlight(LspRange Range, int Kind);

public sealed record MarkupContent(string Kind, string Value);

public sealed record LspHover(MarkupContent Contents, LspRange Range);

public sealed record LspTextEdit(LspRange Range, string NewText);

public sealed record WorkspaceEdit(Dictionary<string, LspTextEdit[]> Changes);

public sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);

public sealed record SemanticTokensOptions(SemanticTokensLegend Legend, bool Full);

/// <summary>The params of requests about one document (semantic tokens, document symbols).</summary>
public sealed record TextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record SemanticTokensResult(int[] Data);

public sealed record LspDocumentSymbol(string Name, string Detail, int Kind, LspRange Range, LspRange SelectionRange, LspDocumentSymbol[] Children);

public sealed record CodeActionOptions(string[] CodeActionKinds);

public sealed record SignatureHelpOptions(string[] TriggerCharacters);

public sealed record LspFoldingRange(int StartLine, int EndLine, string? Kind = null);

public sealed record SelectionRangeParams(TextDocumentIdentifier TextDocument, LspPosition[] Positions);

/// <summary>A selection range, inside its <paramref name="Parent"/> (the next larger range), if any.</summary>
public sealed record LspSelectionRange(LspRange Range, LspSelectionRange? Parent = null);

public sealed record WorkspaceSymbolParams(string? Query);

/// <summary>A symbol found by <c>workspace/symbol</c>; <paramref name="Kind"/> is LSP's SymbolKind.</summary>
public sealed record LspSymbolInformation(string Name, int Kind, LspLocation Location, string? ContainerName = null);

public sealed record DocumentOnTypeFormattingOptions(string FirstTriggerCharacter);

public sealed record LspFormattingOptions(int TabSize, bool InsertSpaces);

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, LspFormattingOptions Options);

public sealed record DocumentRangeFormattingParams(TextDocumentIdentifier TextDocument, LspRange Range, LspFormattingOptions Options);

public sealed record DocumentOnTypeFormattingParams(TextDocumentIdentifier TextDocument, LspPosition Position, string Ch, LspFormattingOptions Options);

/// <summary>A parameter by its [start, end) offsets in the signature's label.</summary>
public sealed record LspParameterInformation(int[] Label);

public sealed record LspSignatureInformation(string Label, LspParameterInformation[] Parameters, string? Documentation = null);

public sealed record LspSignatureHelp(LspSignatureInformation[] Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>The params of <c>textDocument/codeAction</c>; the client's diagnostics are not used: the server checks its own.</summary>
public sealed record CodeActionParams(TextDocumentIdentifier TextDocument, LspRange Range);

public sealed record LspCodeAction(string Title, string Kind, LspDiagnostic[] Diagnostics, WorkspaceEdit Edit);

public sealed record InlayHintParams(TextDocumentIdentifier TextDocument, LspRange Range);

/// <summary>An inlay hint with a plain-text label; no kind, since a value is neither a type nor a parameter name.</summary>
public sealed record LspInlayHint(LspPosition Position, string Label, bool? PaddingLeft = null, string? Tooltip = null);

public sealed record ServerInfo(string Name, string Version);

public sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(TextDocumentParams))]
[JsonSerializable(typeof(SemanticTokensResult))]
[JsonSerializable(typeof(LspDocumentSymbol[]))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(RenameParams))]
[JsonSerializable(typeof(LspLocation[]))]
[JsonSerializable(typeof(LspDocumentHighlight[]))]
[JsonSerializable(typeof(LspHover))]
[JsonSerializable(typeof(LspRange))]
[JsonSerializable(typeof(WorkspaceEdit))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(DidChangeWatchedFilesParams))]
[JsonSerializable(typeof(LspCompletionItem[]))]
[JsonSerializable(typeof(InlayHintParams))]
[JsonSerializable(typeof(LspInlayHint[]))]
[JsonSerializable(typeof(CodeActionParams))]
[JsonSerializable(typeof(LspCodeAction[]))]
[JsonSerializable(typeof(LspSignatureHelp))]
[JsonSerializable(typeof(LspFoldingRange[]))]
[JsonSerializable(typeof(SelectionRangeParams))]
[JsonSerializable(typeof(LspSelectionRange[]))]
[JsonSerializable(typeof(WorkspaceSymbolParams))]
[JsonSerializable(typeof(LspSymbolInformation[]))]
[JsonSerializable(typeof(DocumentFormattingParams))]
[JsonSerializable(typeof(DocumentRangeFormattingParams))]
[JsonSerializable(typeof(DocumentOnTypeFormattingParams))]
[JsonSerializable(typeof(LspTextEdit[]))]
internal sealed partial class LspJson : JsonSerializerContext;
