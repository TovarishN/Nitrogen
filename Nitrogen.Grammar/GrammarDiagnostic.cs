namespace Nitrogen.Grammar;

public enum GrammarSeverity
{
    Error,
    Warning,
}

/// <param name="Module">The module the diagnostic belongs to; null for syntax errors.</param>
public sealed record GrammarDiagnostic(
    string Code, GrammarSeverity Severity, string Message, GrammarSpan Span, string? Module = null)
{
    public override string ToString() => $"{Code} {Span}: {Message}";
}

public static class GrammarCodes
{
    public const string Syntax = "NGR0001";
    public const string UndefinedRule = "NGR0101";
    public const string AmbiguousReference = "NGR0102";
    public const string UnknownModule = "NGR0103";
    public const string LeftRecursion = "NGR0104";
    public const string UnreachableAlternative = "NGR0105";
    public const string DuplicateLabel = "NGR0106";
    public const string DuplicateAlternative = "NGR0107";
    public const string DuplicateRule = "NGR0108";
    public const string AlternativeNeedsName = "NGR0109";
    public const string BadPrecedence = "NGR0110";
    public const string NotExtensible = "NGR0111";
    public const string TokenReferencesSyntax = "NGR0112";
    public const string NullablePrefix = "NGR0113";
    public const string DuplicateModule = "NGR0114";
    public const string UnknownSymbolKind = "NGR0115";
    public const string UnknownBindingField = "NGR0116";
    public const string BindingOnAlias = "NGR0117";
    public const string DuplicateSymbolKind = "NGR0118";
    public const string DuplicateBuiltin = "NGR0119";
    public const string DuplicateClause = "NGR0120";
    public const string BadBuiltinScope = "NGR0121";
    public const string UnknownProperty = "NGR0122";
    public const string UnknownSemanticChild = "NGR0123";
    public const string WrongPropertyDirection = "NGR0124";
    public const string SymbolWithoutDeclaration = "NGR0125";
    public const string DuplicateProperty = "NGR0126";
    public const string UndefinedProperty = "NGR0127";
    public const string DuplicatePropertyFlag = "NGR0128";
    public const string SemanticsOnAlias = "NGR0129";
    public const string PropertyOnAlternative = "NGR0130";
    public const string SequenceArgumentNeedsList = "NGR0131";
    public const string InvalidValueProperty = "NGR0132";
    public const string OptionalArgumentNeedsOptionalField = "NGR0133";
    public const string InvalidTemplateClause = "NGR0134";
    public const string UnknownHole = "NGR0135";
    public const string HoleNotARule = "NGR0136";
    public const string TemplateDoesNotParse = "NGR0137";
    public const string TemplateDeclares = "NGR0138";
    public const string HoleShapeMismatch = "NGR0139";
    public const string UnsupportedHoleShape = "NGR0140";
    public const string DuplicateQuoteHelper = "NGR0141";
    public const string DuplicateQuoteParameter = "NGR0142";
    public const string UnknownQuoteHelper = "NGR0143";
    public const string AmbiguousQuoteHelper = "NGR0144";
    public const string QuoteHelperArgumentCount = "NGR0145";
    public const string QuoteHelperShape = "NGR0146";
    public const string QuoteHelperCategory = "NGR0147";
    public const string QuoteMatchSelector = "NGR0148";
    public const string QuoteMatchCapture = "NGR0149";
    public const string FunctionType = "NGR0150";
    public const string UnknownFunction = "NGR0151";
    public const string FunctionArgumentCount = "NGR0152";
    public const string FunctionArgumentType = "NGR0153";
    public const string EvaluationLimit = "NGR0154";
    public const string EvaluationError = "NGR0155";
    public const string DuplicateFunction = "NGR0158";
    public const string CharacterLevelCall = "NGR0159";
    public const string TransparentShape = "NGR0160";
    public const string TransparentPostfix = "NGR0161";
    public const string TransparentDecorated = "NGR0162";
    public const string UnknownContext = "NGR0163";
    public const string DuplicateContext = "NGR0164";
    public const string ContextInToken = "NGR0165";
    public const string NullableContextRepeat = "NGR0166";
    public const string GeneratedNameCollision = "NGR0201";
    public const string KindNameCollision = "NGR0202";
    public const string ReservedName = "NGR0203";
    public const string SemanticsNameCollision = "NGR0204";
}
