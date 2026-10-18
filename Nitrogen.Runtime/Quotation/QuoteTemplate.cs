using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>An independently owned constructed syntax tree. Dispose after its destination project is finished with it.</summary>
public sealed partial class QuoteInstantiation : IDisposable
{
    internal QuoteInstantiation(SyntaxTree? tree, IReadOnlyList<QuoteDiagnostic> diagnostics,
        IReadOnlyDictionary<int, SourceOrigin> origins, IReadOnlyDictionary<int, int> proxies)
    {
        Tree = tree;
        if (tree is not null) tree.QuotationIdentity = Guid.NewGuid();
        Diagnostics = diagnostics;
        Origins = origins;
        Proxies = proxies;
    }
    public bool Success => Tree is not null;
    public SyntaxTree? Tree { get; }
    public IReadOnlyList<QuoteDiagnostic> Diagnostics { get; }
    public IReadOnlyDictionary<int, SourceOrigin> Origins { get; }
    internal IReadOnlyDictionary<int, int> Proxies { get; }
    internal IReadOnlyDictionary<int, QuoteNode> ProxyNodes { get; init; } = new Dictionary<int, QuoteNode>();
    public void Dispose() => Tree?.Dispose();
}

/// <summary>Owns parsed template storage. Templates become invalid when this result is disposed.</summary>
public sealed class QuoteCompilation : IDisposable
{
    internal QuoteCompilation(ParseResult? parsed, IReadOnlyList<QuoteDiagnostic> diagnostics,
        IReadOnlyDictionary<string, QuoteHole> holes, QuoteOptions? options = null)
    {
        Diagnostics = diagnostics;
        if (parsed is not null) Template = new QuoteTemplate(parsed, holes, options);
    }
    public bool Success => Template is not null;
    public QuoteTemplate? Template { get; }
    public IReadOnlyList<QuoteDiagnostic> Diagnostics { get; }
    public void Dispose() => Template?.DisposeStorage();
}

/// <summary>A parsed, reusable quotation. Instantiate copies syntax into independently owned storage.</summary>
public sealed class QuoteTemplate
{
    readonly ParseResult _parsed;
    readonly IReadOnlyDictionary<string, QuoteHole> _holes;
    readonly QuoteOptions? _options;
    bool _disposed;
    internal QuoteTemplate(ParseResult parsed, IReadOnlyDictionary<string, QuoteHole> holes, QuoteOptions? options)
    {
        _options = options;
        _parsed = parsed;
        _holes = holes;
    }

    internal SyntaxTree Syntax => _parsed.Tree;

    internal void DisposeStorage()
    {
        if (_disposed) return;
        _disposed = true;
        _parsed.Dispose();
    }

    public QuoteInstantiation Instantiate(IReadOnlyDictionary<string, QuoteArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var diagnostics = new List<QuoteDiagnostic>();
        var copied = new Dictionary<string, QuoteArgument>(StringComparer.Ordinal);
        if (_disposed) diagnostics.Add(new("NQ0003", default, "The quotation compilation has been disposed."));
        else
        {
            foreach (string name in arguments.Keys)
                if (!_holes.ContainsKey(name)) diagnostics.Add(new("NQ0002", default, "Unknown quotation argument.", name));
            foreach (var (name, hole) in _holes)
            {
                if (!arguments.TryGetValue(name, out var argument) ||
                    (hole.IsList ? argument is not QuoteArgument.Many : argument is not QuoteArgument.Scalar))
                {
                    diagnostics.Add(new("NQ0002", default, "Missing argument or incorrect scalar/list shape.", name));
                    continue;
                }
                var nodes = argument is QuoteArgument.Many many ? many.Values.ToArray() : [((QuoteArgument.Scalar)argument).Value];
                foreach (var value in nodes)
                {
                    if (value is null || value.Tree.IsDisposed || (uint)value.Node >= (uint)value.Tree.NodeCount)
                    {
                        diagnostics.Add(new("NQ0003", default, "Invalid or disposed source node.", name));
                        continue;
                    }
                    if (!ReferenceEquals(value.Tree.Language, _parsed.Tree.Language) ||
                        !_parsed.Tree.Language!.AcceptsQuoteNode(hole.Category, value.Tree.Kind(value.Node)))
                        diagnostics.Add(new("NQ0004", value.Tree.Span(value.Node), "Source node category or language snapshot does not match.", name));
                    else if (!ValidSource(value.Tree, value.Node))
                        diagnostics.Add(new("NQ0003", value.Tree.Span(value.Node), "Recovered, ambiguous, or template syntax cannot be inserted.", name));
                }
                copied[name] = hole.IsList ? QuoteArgument.List(nodes) : QuoteArgument.Node(nodes[0]);
            }
        }
        return diagnostics.Count == 0 ? new QuoteInstantiator(_parsed.Tree, copied, proxies: false, _options?.TemplateOrigin).Build()
            : new(null, diagnostics.ToArray(), new Dictionary<int, SourceOrigin>(), new Dictionary<int, int>());
    }

    internal static bool ValidSource(SyntaxTree tree, int node)
    {
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.TryPop(out int current))
        {
            if (tree.Flags(current) != NodeFlags.None || tree.Kind(current) is
                SyntaxKinds.Hole or SyntaxKinds.ListHole or SyntaxKinds.Error or SyntaxKinds.Ambiguous ||
                !tree.SkippedTrivia(current).IsEmpty) return false;
            for (int i = 0; i < tree.ChildCount(current); i++)
                if (tree.Child(current, i) is var child && child != SyntaxTree.NoNode) stack.Push(child);
        }
        return true;
    }
}
