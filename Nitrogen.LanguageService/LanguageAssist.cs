using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>
/// What a language's editor knows beyond its grammar's binding, semantics and lowering: colour,
/// completion, hover and diagnostics for text those do not describe, such as the lowering clauses of a
/// .ngr grammar. Offsets are UTF-16 offsets into the document; the service maps them to positions.
/// Its tokens and hover win over the generic ones; its completions come first.
/// </summary>
public interface ILanguageAssist
{
    IEnumerable<AssistToken> Tokens(AssistDocument document);

    IEnumerable<AssistCompletion> Completion(AssistDocument document, int offset);

    AssistHover? Hover(AssistDocument document, int offset);

    IEnumerable<AssistDiagnostic> Diagnostics(AssistDocument document);
}

/// <param name="Catalog">The semantic catalog the document's text refers to: for a grammar of a workspace language, that language's last good catalog; null otherwise.</param>
public sealed record AssistDocument(string Uri, string Text, SyntaxTree Tree, SemanticCatalog? Catalog);

public readonly record struct AssistToken(TextSpan Span, TokenType Type);

public sealed record AssistCompletion(string Label, CompletionKind Kind, string Detail, TextSpan Replace);

public sealed record AssistHover(string Markdown, TextSpan Span);

public sealed record AssistDiagnostic(TextSpan Span, ServiceSeverity Severity, string Code, string Message);
