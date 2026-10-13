namespace Nitrogen.Grammar;

public sealed record ExtractionSource(string Uri, string Text);
public sealed record ExtractionProblem(string Code, string Message, string Uri, GrammarSpan Span);
public enum ExtractionShape { Rules, Alternatives }
public sealed record ExtractionSelection(string Uri, string Module, ExtractionShape Shape,
    string? ExtensionTarget, IReadOnlyList<GrammarSpan> Definitions, GrammarSpan Removal);
public sealed record ExtractionSelectionResult(ExtractionSelection? Selection, IReadOnlyList<ExtractionProblem> Problems);
public sealed record ExtractionTextEdit(string Uri, GrammarSpan Span, string NewText);
public sealed record ExtractionSymbolMove(string Before, string After);
public sealed record ExtractionTransform(string DestinationText, IReadOnlyList<ExtractionTextEdit> Edits,
    IReadOnlyList<ExtractionSymbolMove> Symbols, IReadOnlyList<ExtractionProblem> Problems);
