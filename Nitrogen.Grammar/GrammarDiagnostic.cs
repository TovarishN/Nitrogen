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
    public const string GeneratedNameCollision = "NGR0201";
    public const string KindNameCollision = "NGR0202";
    public const string ReservedName = "NGR0203";
    public const string SemanticsNameCollision = "NGR0204";
}
