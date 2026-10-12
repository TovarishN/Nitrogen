using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nitrogen.LanguageService;

/// <summary>Recognizes statically recoverable calls to the actual Nitrogen API without executing C#.</summary>
internal static class CSharpQuotations
{
    internal sealed record Hole(QuoteHole Shape, TextSpan Span);
    internal sealed record Quote(string Category, EmbeddedString Text, IReadOnlyDictionary<string, Hole> Holes);

    internal static IReadOnlyList<Quote> Find(string path, IReadOnlyDictionary<string, string> sources)
    {
        var trees = sources.Select(p => CSharpSyntaxTree.ParseText(p.Value, path: p.Key)).ToArray();
        var compilation = CSharpCompilation.Create("QuoteAssistance", trees, SemanticSourceIndex.References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var tree = trees.FirstOrDefault(t => t.FilePath == path);
        if (tree is null) return [];
        var model = compilation.GetSemanticModel(tree);
        var found = new List<Quote>();
        foreach (var syntax in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetOperation(syntax) is not IInvocationOperation call ||
                call.TargetMethod.Name != "CompileQuote" || call.TargetMethod.ContainingType.ToDisplayString() != "Nitrogen.Language") continue;
            ExpressionSyntax? Argument(string name) => call.Arguments.FirstOrDefault(a => a.Parameter?.Name == name)?.Value.Syntax as ExpressionSyntax;
            if (Argument("category") is not { } category || Constant(category) is not { } categoryText ||
                Argument("text") is not { } template || Decode(template, new HashSet<ISymbol>(SymbolEqualityComparer.Default)) is not { } decoded ||
                Argument("holes") is not BaseObjectCreationExpressionSyntax creation) continue;
            var initializer = creation switch
            {
                ObjectCreationExpressionSyntax explicitNew => explicitNew.Initializer,
                ImplicitObjectCreationExpressionSyntax implicitNew => implicitNew.Initializer,
                _ => null,
            };
            if (initializer is null) continue;
            var holes = new Dictionary<string, Hole>(StringComparer.Ordinal);
            bool supported = true;
            foreach (var element in initializer.Expressions)
            {
                if (element is not AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax key, Right: InvocationExpressionSyntax value } ||
                    key.ArgumentList.Arguments.Count != 1 || Constant(key.ArgumentList.Arguments[0].Expression) is not { } name ||
                    model.GetOperation(value) is not IInvocationOperation holeCall ||
                    holeCall.TargetMethod.ContainingType.ToDisplayString() != "Nitrogen.QuoteHole" ||
                    holeCall.TargetMethod.Name is not ("Node" or "List") ||
                    holeCall.Arguments.FirstOrDefault()?.Value.ConstantValue is not { HasValue: true, Value: string holeCategory } ||
                    holes.ContainsKey(name))
                {
                    supported = false;
                    break;
                }
                holes[name] = new(new(holeCategory, holeCall.TargetMethod.Name == "List"), new TextSpan(key.SpanStart, key.Span.Length));
            }
            if (supported) found.Add(new(categoryText, decoded, holes));
        }
        return found;

        string? Constant(ExpressionSyntax expression) => model.GetConstantValue(expression) is { HasValue: true, Value: string text } ? text : null;

        EmbeddedString? Decode(ExpressionSyntax expression, HashSet<ISymbol> visited)
        {
            if (expression.SyntaxTree != tree || Constant(expression) is null) return null;
            if (expression is LiteralExpressionSyntax) return EmbeddedStrings.Decode(sources[path], expression.SpanStart);
            if (expression is ParenthesizedExpressionSyntax parenthesized) return Decode(parenthesized.Expression, visited);
            if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) &&
                Decode(binary.Left, new(visited, SymbolEqualityComparer.Default)) is { } left &&
                Decode(binary.Right, new(visited, SymbolEqualityComparer.Default)) is { } right)
                return new("quote", left.Value + right.Value, left.Map.Take(left.Value.Length).Concat(right.Map).ToArray(), right.End)
                {
                    Parts = left.SourceParts.Concat(right.SourceParts.Select(p => p with { ValueStart = p.ValueStart + left.Value.Length })).ToArray(),
                };
            if (model.GetSymbolInfo(expression).Symbol is { } symbol && visited.Add(symbol) &&
                symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } value })
                return Decode(value, visited);
            return null;
        }
    }
}
