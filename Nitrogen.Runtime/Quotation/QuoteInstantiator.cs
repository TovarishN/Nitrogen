using System.Text;
using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>Constructs syntax structurally; supplied operands are never reparsed.</summary>
internal sealed class QuoteInstantiator(SyntaxTree template, IReadOnlyDictionary<string, QuoteArgument> arguments, bool proxies, SourceOrigin? templateOrigin = null)
{
    sealed record Built(int Kind, int Start, int Length, NodeFlags Flags, IReadOnlyList<Built> Children,
        SourceOrigin? Origin = null, QuoteNode? Proxy = null);
    sealed class Failure(QuoteDiagnostic diagnostic) : Exception
    {
        internal QuoteDiagnostic Diagnostic { get; } = diagnostic;
    }

    readonly StringBuilder _text = new();

    internal QuoteInstantiation Build()
    {
        try
        {
            var root = Copy(template, template.Root, true, templateOrigin);
            var arena = new BuildArena();
            int oldRoot = Store(root, arena);
            var tree = TreeFreezer.Freeze(arena, oldRoot, _text.ToString(), template.Language);
            var origins = new Dictionary<int, SourceOrigin>();
            var children = new Dictionary<int, int>();
            var sources = new Dictionary<int, QuoteNode>();
            int index = 0;
            Visit(root);
            return new(tree, [], origins, children) { ProxyNodes = sources };

            void Visit(Built node)
            {
                int n = index++;
                if (node.Origin is { } origin) origins[n] = origin;
                if (node.Proxy is { } child)
                {
                    children[n] = child.Node;
                    sources[n] = child;
                }
                foreach (var item in node.Children) Visit(item);
            }
        }
        catch (Failure failure) { return new(null, [failure.Diagnostic], new Dictionary<int, SourceOrigin>(), new Dictionary<int, int>()); }
    }

    static int Store(Built node, BuildArena arena)
    {
        var children = node.Children.Select(child => Store(child, arena)).ToArray();
        int from = arena.ChildStackCount;
        foreach (int child in children) arena.PushChild(child);
        int first = arena.AppendChildLog(from, children.Length);
        arena.ChildStackCount = from;
        return arena.NewNode(node.Kind, node.Start, node.Length, node.Flags, first, children.Length);
    }

    Built Insert(QuoteNode source, string name)
    {
        if (!proxies) return Copy(source.Tree, source.Node, false, source.Origin);
        int start = _text.Length;
        _text.Append('$').Append(name);
        return new(SyntaxKinds.Hole, start, _text.Length - start, NodeFlags.None, [], source.Origin, source);
    }

    Built Copy(SyntaxTree source, int node, bool holes, SourceOrigin? origin)
    {
        int kind = source.Kind(node), start = _text.Length;
        var span = source.Span(node);
        if (holes && kind == SyntaxKinds.Hole)
        {
            string name = source.GetText(node)[1..].ToString();
            return arguments.TryGetValue(name, out var value) && value is QuoteArgument.Scalar scalar
                ? Insert(scalar.Value, name) : throw new Failure(new("NQ0002", span, "A scalar input is required.", name));
        }
        if (holes && kind == SyntaxKinds.ListHole)
            throw new Failure(new("NQ0001", span, "A list marker must occupy a supported list item site."));
        var children = new List<Built>();
        bool expand = holes && kind == SyntaxKinds.List && Enumerable.Range(0, source.ChildCount(node))
            .Any(i => source.Kind(source.Child(node, i)) == SyntaxKinds.ListHole);
        if (expand)
        {
            if (!source.TemplateLists.TryGetValue(node, out var shape))
                throw new Failure(new("NQ0001", span, "Unsupported destination list shape."));
            int stride = shape.Separator is null ? 1 : 2;
            for (int i = 0; i < source.ChildCount(node); i += stride)
            {
                int child = source.Child(node, i);
                if (source.Kind(child) == SyntaxKinds.ListHole)
                {
                    string name = source.GetText(child)[3..].ToString();
                    if (!arguments.TryGetValue(name, out var argument) || argument is not QuoteArgument.Many list)
                        throw new Failure(new("NQ0002", source.Span(child), "A list input is required.", name));
                    foreach (var item in list.Values)
                    {
                        Separator();
                        children.Add(Insert(item, name));
                    }
                }
                else
                {
                    Separator();
                    children.Add(Copy(source, child, true, origin));
                }
            }
            int itemCount = stride == 1 ? children.Count : (children.Count + 1) / 2;
            if (itemCount < shape.MinimumCount)
                throw new Failure(new("NQ0005", span, "The instantiated list requires at least one item."));

            void Separator()
            {
                if (children.Count == 0) return;
                if (shape.Separator is { } separator)
                {
                    int at = _text.Length;
                    _text.Append(separator);
                    children.Add(new(SyntaxKinds.Literal, at, separator.Length, NodeFlags.None, []));
                }
                _text.Append(' ');
            }
        }
        else
        {
            int cursor = span.Start;
            for (int i = 0; i < source.ChildCount(node); i++)
            {
                int child = source.Child(node, i);
                var childSpan = source.Span(child);
                if (childSpan.Start > cursor) _text.Append(source.Text, cursor, childSpan.Start - cursor);
                // With only an explicit subtree origin, descendants conservatively retain that
                // identity rather than inventing source offsets or node identities.
                children.Add(Copy(source, child, holes, origin));
                cursor = Math.Max(cursor, childSpan.End);
            }
            if (cursor < span.End) _text.Append(source.Text, cursor, span.End - cursor);
        }
        return new(kind, start, _text.Length - start, source.Flags(node), children, origin);
    }
}
