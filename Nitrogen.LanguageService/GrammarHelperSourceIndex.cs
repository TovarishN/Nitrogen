using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

/// <summary>Resolves C# in grammar semantics through the compiler's source mappings, without executing helpers.</summary>
internal sealed class GrammarHelperSourceIndex
{
    sealed record Entry(string Path, DocumentRange Range, Lazy<IReadOnlyList<DocumentLocation>> Targets);

    readonly List<Entry> _entries = [];

    public GrammarHelperSourceIndex(IReadOnlyDictionary<string, string> texts, IReadOnlyList<string> grammars,
        IReadOnlyList<string> sources, string generatedNamespace, IReadOnlyList<string> usings)
    {
        var generated = GrammarCompiler.Compile(grammars.Where(texts.ContainsKey)
            .Select(path => new GrammarInput(path, texts[path], generatedNamespace)));
        if (generated.HasErrors) return;
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var generatedTrees = generated.Sources.Select(source => CSharpSyntaxTree.ParseText(source.Code, options, source.HintName)).ToArray();
        var sourceTrees = sources.Where(texts.ContainsKey).Select(path => CSharpSyntaxTree.ParseText(texts[path], options, path));
        string[] imports = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];
        var globals = CSharpSyntaxTree.ParseText(string.Concat(imports.Concat(usings).Distinct().Select(name => $"global using {name};\n")), options);
        var compilation = CSharpCompilation.Create("GrammarHelperNavigation", generatedTrees.Concat(sourceTrees).Append(globals),
            SemanticSourceIndex.References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var grammarPaths = grammars.ToHashSet(StringComparer.Ordinal);
        var sourcePaths = sources.ToHashSet(StringComparer.Ordinal);
        foreach (var tree in generatedTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var mappings = tree.GetLineMappings().ToArray();
            var generatedText = tree.GetText();
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var mapped = name.Identifier.GetLocation().GetMappedLineSpan();
                if (!mapped.IsValid || !grammarPaths.Contains(mapped.Path)) continue;
                // Enhanced #line directives can map a token at the fragment boundary to the whole
                // expression. Recover its exact position from the directive's character offset instead.
                var physical = tree.GetLineSpan(name.Identifier.Span).StartLinePosition;
                var mapping = mappings.FirstOrDefault(mapping => mapping.MappedSpan.Path == mapped.Path &&
                    physical.Line >= mapping.Span.Start.Line && physical.Line <= mapping.Span.End.Line);
                if (mapping.CharacterOffset is not int characterOffset) continue;
                int generatedStart = generatedText.Lines.GetPosition(mapping.Span.Start) + characterOffset - 1;
                var sourceLines = new LineMap(texts[mapped.Path]);
                int sourceStart = sourceLines.OffsetOf(new DocumentPosition(mapping.MappedSpan.StartLinePosition.Line,
                    mapping.MappedSpan.StartLinePosition.Character)) + name.Identifier.SpanStart - generatedStart;
                int length = name.Identifier.Span.Length;
                if (sourceStart < 0 || sourceStart + length > texts[mapped.Path].Length ||
                    texts[mapped.Path].Substring(sourceStart, length) != name.Identifier.Text) continue;
                var range = sourceLines.RangeOf(new TextSpan(sourceStart, length));
                Microsoft.CodeAnalysis.SyntaxNode expression = name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : name;
                if (expression.Parent is InvocationExpressionSyntax call && call.Expression == expression) expression = call;
                var symbol = model.GetSymbolInfo(expression).Symbol;
                if (symbol is null) continue; // Ambiguous or unresolved calls must not guess an overload.
                var locations = symbol.OriginalDefinition.Locations.Where(location => location.IsInSource).ToArray();
                if (locations.Length == 0) continue;
                // Write generated source only when its definition is requested, rather than on each index rebuild.
                _entries.Add(new Entry(mapped.Path, range, new Lazy<IReadOnlyList<DocumentLocation>>(() =>
                    locations.Select(Target).OfType<DocumentLocation>().Distinct().ToArray())));
            }
        }

        DocumentLocation? Target(Location location)
        {
            if (location.SourceTree is not { } source) return null;
            string? path = sourcePaths.Contains(source.FilePath) ? source.FilePath
                : generatedTrees.Contains(source) ? NavigationSourceFiles.Materialize(source.FilePath, source.GetText().ToString()) : null;
            if (path is null) return null;
            var lines = new LineMap(source.GetText().ToString());
            return new DocumentLocation(new Uri(path).AbsoluteUri,
                lines.RangeOf(new TextSpan(location.SourceSpan.Start, location.SourceSpan.Length)));
        }
    }

    public IReadOnlyList<DocumentLocation> Definition(string path, DocumentPosition position) =>
        _entries.Where(entry => entry.Path == path && Contains(entry.Range, position))
            .SelectMany(entry => entry.Targets.Value).Distinct().ToArray();

    static bool Contains(DocumentRange range, DocumentPosition position) =>
        (position.Line > range.Start.Line || position.Line == range.Start.Line && position.Character >= range.Start.Character) &&
        (position.Line < range.End.Line || position.Line == range.End.Line && position.Character < range.End.Character);
}
