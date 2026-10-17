namespace Nitrogen.Grammar;

/// <summary>
/// The post-parse check both <c>.ngr</c> parsers share (spec: grammar functions): <c>if</c>, comprehensions, <c>()</c> and
/// computed class bounds are compile-time forms, so only a grammar function body may contain them. Call arguments are
/// compile-time values and are not checked here.
/// </summary>
public static class GrammarFunctionSyntax
{
    public static GrammarParseResult Check(GrammarFile file)
    {
        try
        {
            return new GrammarParseResult(CheckOrThrow(file), default);
        }
        catch (GrammarSyntaxException error)
        {
            return new GrammarParseResult(null, new[] { new GrammarDiagnostic(GrammarCodes.Syntax, GrammarSeverity.Error, error.Message, error.Span) });
        }
    }

    internal static GrammarFile CheckOrThrow(GrammarFile file)
    {
        foreach (var module in file.Modules)
        {
            foreach (var rule in module.Rules)
                switch (rule)
                {
                    case TokenRule token: Reject(token.Body); break;
                    case SyntaxRule syntax: Reject(syntax.Body); break;
                    case ExtensibleRule extensible:
                        foreach (var alternative in extensible.Alternatives) Reject(alternative.Body);
                        break;
                }
            foreach (var extend in module.Extends)
                foreach (var alternative in extend.Alternatives) Reject(alternative.Body);
        }
        return file;
    }

    static void Reject(Expr expr)
    {
        string? form = expr switch
        {
            IfExpr => "'if'",
            ComprehensionExpr { IsChoice: true } => "'choice'",
            ComprehensionExpr => "'seq'",
            ValueClassExpr => "a computed class bound",
            SequenceExpr { Items.Count: 0 } => "'()'",
            _ => null,
        };
        if (form is not null) throw new GrammarSyntaxException(form + " is only allowed in a grammar function body", expr.Span);
        foreach (var child in GrammarExtraction.Children(expr)) Reject(child);
    }
}
