namespace Nitrogen.Grammar;

public static partial class GrammarExtraction
{
    sealed record ExtractionName(string Name, GrammarSpan Span, string? ImplicitPoint = null);

    // Keep the point identity, not only its module: moving one of several points changes
    // implicit lookup only for alternatives that belong to that particular point.
    static IEnumerable<ExtractionName> ExtractionNames(GrammarAnalysis analysis, ModuleDecl module, string text)
    {
        foreach (var rule in module.Rules)
        {
            var names = rule switch
            {
                TokenRule token => ExpressionNames(token.Body, null),
                SyntaxRule syntax => ExpressionNames(syntax.Body, null),
                ExtensibleRule extensible => extensible.Alternatives.SelectMany(a =>
                    ExpressionNames(a.Body, module.Name + "." + rule.Name)),
                _ => Array.Empty<ExtractionName>(),
            };
            foreach (var name in names) yield return name;
        }
        foreach (var extend in module.Extends)
        {
            int end = ExtractionOpeningBrace(text, extend.Span.Start).Start;
            var token = GrammarLexer.Tokenize(text.Substring(extend.Span.Start, end - extend.Span.Start)).Last(t => t.Value == extend.Target);
            yield return new(extend.Target, new(extend.Span.Start + token.Start, token.Length));
            string? point = analysis.Resolve(extend.Target, extend.Span, module, null)?.Key;
            foreach (var alternative in extend.Alternatives)
                foreach (var name in ExpressionNames(alternative.Body, point)) yield return name;
        }
        foreach (var builtin in module.Builtins)
            if (builtin.Scope is { } scope) yield return new(scope.Name, scope.Span);
    }

    static IEnumerable<ExtractionName> ExpressionNames(Expr expression, string? implicitPoint)
    {
        if (expression is ReferenceExpr reference) yield return new(reference.Name, reference.Span, implicitPoint);
        foreach (var child in Children(expression))
            foreach (var name in ExpressionNames(child, implicitPoint)) yield return name;
    }

    static string? PointModule(string? point) => point is null ? null : point.Substring(0, point.LastIndexOf('.'));

    static GrammarToken ExtractionOpeningBrace(string text, int start)
    {
        // Tokenize only the module/extension header; semantic C# blocks are opaque to this lexer.
        while (true)
        {
            var token = GrammarLexer.Next(text, ref start);
            if (token.Kind is TokenKind.LBrace or TokenKind.End) return token;
        }
    }
}
