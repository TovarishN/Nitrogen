namespace Nitrogen.LanguageService;

public sealed record ExtractionPreparation(string? Id, string? SuggestedModule, string? SuggestedFileName, string? UnavailableReason);
public sealed record ExtractionRequest(string Id, string DestinationUri, string Module);
public sealed record ExtractionDocumentChange(string Uri, int? Version, IReadOnlyList<DocumentEdit> Edits, string OriginalText);
public sealed record ExtractionProposal(string DestinationUri, string DestinationText,
    IReadOnlyList<ExtractionDocumentChange> Changes, IReadOnlyList<string> Notices, string? UnavailableReason, bool CreateDestination = true);

public sealed record QuoteHelperExtractionRequest(string Id, string DestinationUri, string Module, string HelperName, bool ExistingDestination);
public sealed record QuoteHelperExtractionPreparation(string? Id, string? SuggestedHelperName, string? SourceModule, string? SourceUri, string? UnavailableReason, IReadOnlyList<QuoteHelperDestination>? Destinations = null);
public sealed record QuoteHelperDestination(string Uri, string Module);
