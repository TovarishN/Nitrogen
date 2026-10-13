namespace Nitrogen.LanguageService;

public sealed record ExtractionPreparation(string? Id, string? SuggestedModule, string? SuggestedFileName, string? UnavailableReason);
public sealed record ExtractionRequest(string Id, string DestinationUri, string Module);
public sealed record ExtractionDocumentChange(string Uri, int? Version, IReadOnlyList<DocumentEdit> Edits, string OriginalText);
public sealed record ExtractionProposal(string DestinationUri, string DestinationText,
    IReadOnlyList<ExtractionDocumentChange> Changes, IReadOnlyList<string> Notices, string? UnavailableReason);
