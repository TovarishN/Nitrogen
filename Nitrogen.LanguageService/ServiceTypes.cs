namespace Nitrogen.LanguageService;

/// <summary>An LSP position: 0-based line and UTF-16 column.</summary>
public readonly record struct DocumentPosition(int Line, int Character);

public readonly record struct DocumentRange(DocumentPosition Start, DocumentPosition End);

/// <summary>LSP's DiagnosticSeverity values.</summary>
public enum ServiceSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
}

/// <summary>A parse diagnostic (code: the runtime's DiagnosticCode name) or a binding diagnostic (NBxxxx).</summary>
public sealed record ServiceDiagnostic(DocumentRange Range, ServiceSeverity Severity, string Code, string Message);

/// <summary>One coloured stretch of a single line.</summary>
public readonly record struct SemanticToken(DocumentPosition Start, int Length, TokenType Type, TokenModifiers Modifiers);

/// <summary>An outline entry: a declaration, its declaring node's range, its name's range, and the declarations inside it.</summary>
public sealed record OutlineSymbol(string Name, string Kind, OutlineKind Outline, DocumentRange Range, DocumentRange SelectionRange,
    IReadOnlyList<OutlineSymbol> Children);

public sealed record DocumentLocation(string Uri, DocumentRange Range);

/// <summary>LSP's DocumentHighlightKind values.</summary>
public enum HighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3,
}

public sealed record DocumentHighlight(DocumentRange Range, HighlightKind Kind);

public sealed record HoverInfo(string Markdown, DocumentRange Range);

public sealed record DocumentEdit(DocumentRange Range, string NewText);

/// <summary>A rename the service will not do; the message says why and is shown to the user.</summary>
public sealed class RenameRefusedException(string message) : Exception(message);

/// <summary>LSP's CompletionItemKind values the service uses.</summary>
public enum CompletionKind
{
    Method = 2,
    Function = 3,
    Field = 5,
    Variable = 6,
    Class = 7,
    Module = 9,
    Property = 10,
    Keyword = 14,
    EnumMember = 20,
    Constant = 21,
    Event = 23,
}

/// <summary>A completion: its label replaces <paramref name="Replace"/> (the typed prefix).</summary>
/// <param name="SortText">The item's rank as text; editors order by it.</param>
public sealed record CompletionItem(string Label, CompletionKind Kind, string Detail, DocumentRange Replace, string? SortText = null);
