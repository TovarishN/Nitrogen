using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Nitrogen.Semantic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Nitrogen.LanguageService;

/// <summary>
/// Source locations of statically named semantic registrations. Roslyn resolves the C# types and helper
/// methods; only constant strings, parameter substitution and concatenation are evaluated. Source code
/// is never executed. Unknown or computed identities have no source target.
/// </summary>
internal sealed class SemanticSourceIndex
{
    internal sealed record Entry(string Path, AssistName Name);

    sealed record StringValue(string Text, ExpressionSyntax Origin, bool Substituted = false);

    internal static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(AssemblyPath(typeof(OperationSignature).Assembly)).Where(File.Exists).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray<MetadataReference>());

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Nitrogen.Cli extracts assemblies for Roslyn. Other single-file hosts without extraction have no source index.")]
    static string AssemblyPath(Assembly assembly) => assembly.Location;

    public IReadOnlyList<Entry> Entries { get; }

    public SemanticSourceIndex(IReadOnlyDictionary<string, string> sources)
    {
        var trees = sources.Select(pair => CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key)).ToArray();
        var compilation = CSharpCompilation.Create("Navigation", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var entries = new List<Entry>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var expression in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                if (expression is not (BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax)) continue;
                if (Identity(expression, model, new Dictionary<ISymbol, StringValue>(SymbolEqualityComparer.Default), 0) is not { } identity) continue;
                if (identity.Value.Origin.SyntaxTree != tree || !expression.Span.Contains(identity.Value.Origin.Span)) continue;
                var span = identity.Value.Origin.Span;
                entries.Add(new Entry(tree.FilePath, new AssistName(new TextSpan(span.Start, span.Length), identity.Value.Text, identity.Kind)));
            }
        }
        Entries = entries.Distinct().ToArray();

        (StringValue Value, AssistNameKind Kind)? Identity(ExpressionSyntax expression, SemanticModel model,
            Dictionary<ISymbol, StringValue> parameters, int depth)
        {
            if (depth > 8) return null;
            var operation = model.GetOperation(expression);
            if (operation is IObjectCreationOperation creation && creation.Type?.ToDisplayString() == "Nitrogen.Semantic.OperationSignature")
                return Argument(creation.Arguments, "id", model, parameters) is { } id ? (id, AssistNameKind.Operation) : null;
            if (operation is not IInvocationOperation invocation) return null;
            var method = invocation.TargetMethod;
            if (method.ContainingType.ToDisplayString() == "Nitrogen.Semantic.SemanticType" && method.Name == "Named")
            {
                if (Argument(invocation.Arguments, "module", model, parameters) is not { } module ||
                    Argument(invocation.Arguments, "name", model, parameters) is not { } name) return null;
                var origin = name.Substituted ? name : module;
                return (origin with { Text = module.Text + "." + name.Text }, AssistNameKind.Type);
            }
            if (method.ReturnType.ToDisplayString() is not ("Nitrogen.Semantic.OperationSignature" or "Nitrogen.Semantic.SemanticType")) return null;
            if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax declaration) return null;
            var body = declaration.ExpressionBody?.Expression ??
                (declaration.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax returned ? returned.Expression : null);
            if (body is null) return null;
            var substituted = new Dictionary<ISymbol, StringValue>(SymbolEqualityComparer.Default);
            foreach (var argument in invocation.Arguments)
            {
                if (argument.Parameter is not { } parameter) continue;
                var text = argument.IsImplicit && argument.Value.ConstantValue is { HasValue: true, Value: string constant }
                    ? new StringValue(constant, expression)
                    : argument.Value.Syntax is ExpressionSyntax value ? String(value, model, parameters) : null;
                if (text is not null) substituted[parameter] = text with { Substituted = true };
            }
            return Identity(body, compilation.GetSemanticModel(declaration.SyntaxTree), substituted, depth + 1);
        }
    }

    static StringValue? Argument(IEnumerable<IArgumentOperation> arguments, string parameter, SemanticModel model,
        Dictionary<ISymbol, StringValue> parameters) =>
        arguments.FirstOrDefault(a => a.Parameter?.Name == parameter)?.Value.Syntax is ExpressionSyntax expression
            ? String(expression, model, parameters) : null;

    static StringValue? String(ExpressionSyntax expression, SemanticModel model, Dictionary<ISymbol, StringValue> parameters)
    {
        if (model.GetConstantValue(expression) is { HasValue: true, Value: string constant }) return new StringValue(constant, expression);
        if (model.GetSymbolInfo(expression).Symbol is { } symbol && parameters.TryGetValue(symbol, out var value)) return value;
        if (expression is ParenthesizedExpressionSyntax parenthesized) return String(parenthesized.Expression, model, parameters);
        if (expression is not BinaryExpressionSyntax binary || !binary.IsKind(SyntaxKind.AddExpression) ||
            String(binary.Left, model, parameters) is not { } left || String(binary.Right, model, parameters) is not { } right) return null;
        var origin = right.Substituted ? right : left;
        return origin with { Text = left.Text + right.Text };
    }
}
