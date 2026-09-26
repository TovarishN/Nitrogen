using Nitrogen.Binding;

namespace Nitrogen.LanguageService;

/// <summary>The outline (issue 238): declarations nested under the nearest declaration whose node contains theirs.</summary>
internal static class OutlineBuilder
{
    public static List<OutlineSymbol> Build(Document document, Project project)
    {
        var tree = document.Parsed.Tree;
        var presentation = document.Language.Presentation;
        var roots = new List<OutlineSymbol>();
        var open = new Stack<(TextSpan Span, List<OutlineSymbol> Children)>();
        var declarations = project[document.Uri].Declarations
            .OrderBy(d => tree.Span(d.Node).Start)
            .ThenByDescending(d => tree.Span(d.Node).Length);
        foreach (var declaration in declarations)
        {
            var span = tree.Span(declaration.Node);
            while (open.Count > 0 && !(open.Peek().Span.Start <= span.Start && span.End <= open.Peek().Span.End)) open.Pop();
            var children = new List<OutlineSymbol>();
            var symbol = new OutlineSymbol(declaration.Name, declaration.Kind, presentation.StyleOf(declaration.Kind).Outline,
                document.Lines.RangeOf(span), document.Lines.RangeOf(declaration.NameSpan), children);
            (open.Count > 0 ? open.Peek().Children : roots).Add(symbol);
            open.Push((span, children));
        }
        return roots;
    }
}
