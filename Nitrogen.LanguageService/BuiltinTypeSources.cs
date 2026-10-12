using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nitrogen.LanguageService;

internal static class BuiltinTypeSources
{
    static readonly Lazy<IReadOnlyDictionary<string, DocumentLocation>> Definitions = new(Build);

    public static DocumentLocation? Definition(string id) => Definitions.Value.GetValueOrDefault(id);

    static IReadOnlyDictionary<string, DocumentLocation> Build()
    {
        using var stream = typeof(BuiltinTypeSources).Assembly.GetManifestResourceStream("Navigation.SemanticType.cs")!;
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        var result = new Dictionary<string, DocumentLocation>(StringComparer.Ordinal);
        if (NavigationSourceFiles.Materialize("SemanticType.cs", text) is not { } path) return result;
        var index = new SemanticSourceIndex(new Dictionary<string, string> { [path] = text });
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var lines = new LineMap(text);
        foreach (var entry in index.Entries.Where(entry => entry.Name.Kind == AssistNameKind.Type))
        {
            var declaration = root.FindToken(entry.Name.Span.Start).Parent?.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
            if (declaration is null) continue;
            var span = declaration.Identifier.Span;
            result[entry.Name.Id] = new DocumentLocation(new Uri(path).AbsoluteUri,
                lines.RangeOf(new TextSpan(span.Start, span.Length)));
        }
        return result;
    }
}
