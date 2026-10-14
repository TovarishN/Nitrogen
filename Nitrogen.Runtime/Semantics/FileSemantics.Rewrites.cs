using System.Collections.Immutable;
using Nitrogen.Semantic;

namespace Nitrogen.Semantics;

/// <summary>Uses of rewrite rules (spec: quasiquote rewrites): each use's per-use instantiation of its template.</summary>
public sealed partial class FileSemantics
{
    /// <summary>A rewrite's use as a per-use instantiation of its template: its semantics, the template's root, and its rule.</summary>
    internal sealed record RewriteInstance(FileSemantics File, int Root, DeclarativeRule Rule, QuoteHelperSource? Source);
    internal sealed record RewriteSource(FileSemantics File, int Node);
    Dictionary<SyntaxTree, FileSemantics> _quoteOwners = new();

    readonly Dictionary<int, RewriteInstance?> _instances = new();
    readonly HashSet<int> _cyclic = new();
    readonly Dictionary<int, QuoteDiagnostic> _instanceErrors = new();
    readonly List<QuoteInstantiation> _ownedQuotes = [];
    internal QuoteDiagnostic? RewriteError(int node) => _instanceErrors.GetValueOrDefault(node);

    internal void ReleaseQuotes()
    {
        foreach (var instance in _instances.Values) instance?.File.ReleaseQuotes();
        foreach (var quote in _ownedQuotes) quote.Dispose();
        _ownedQuotes.Clear();
    }
    List<SemanticDiagnostic>? _expansions;

    /// <summary>In an instantiation: the rules being expanded around it, to stop a rewrite that reaches itself.</summary>
    ImmutableHashSet<DeclarativeRule> _chain = ImmutableHashSet<DeclarativeRule>.Empty;

    /// <summary>In an instantiation: the use's document, and for each Hole node the use's child it stands for.</summary>
    internal IReadOnlyDictionary<int, RewriteSource>? Holes { get; private set; }

    /// <summary>Whether <paramref name="node"/> uses a rewrite that reaches itself (NH0008 when lowered).</summary>
    internal bool IsCyclicRewrite(int node) => Instance(node) is null && _cyclic.Contains(node);

    /// <summary>
    /// The instantiation of the rewrite <paramref name="node"/> uses, made once; null when its kind has none, its
    /// template can't be used, or it is cyclic.
    /// </summary>
    internal RewriteInstance? Instance(int node)
    {
        if (_language.Declarative.RuleFor(Tree.Kind(node))?.Rule is not { Form: DeclarativeForm.Rewrite, Rewrite: { } spec } rule)
            return null;
        if (_instances.TryGetValue(node, out var known)) return known;
        RewriteInstance? instance = null;
        if (_chain.Contains(rule)) _cyclic.Add(node);
        else
        {
            _quoteOwners[Tree] = this;
            var arguments = new Dictionary<string, QuoteArgument>();
            foreach (var hole in spec.Holes)
            {
                int child = Tree.Child(node, hole.Child);
                arguments[hole.Name] = hole.IsList
                    ? QuoteArgument.List(Enumerable.Range(0, (Tree.ChildCount(child) + hole.Stride - 1) / hole.Stride)
                        .Select(i => new QuoteNode(Tree, Tree.Child(child, i * hole.Stride))).ToArray())
                    : QuoteArgument.Node(new QuoteNode(Tree, child));
            }
            SyntaxTree? template;
            var selectedSource = spec.HelperSource;
            if (spec.MatchHelperName is { } helperName)
            {
                var helper = _language.Modules.SelectMany(module => module.QuoteHelpers).Single(h => h.Name == helperName);
                var plan = _language.MatchPlan(helperName);
                if (plan.Errors.Count > 0) return Failure(plan.Errors[0].Diagnostic);
                template = plan.Fallback!.Syntax;
                var selector = ((QuoteArgument.Scalar)arguments[helper.Match!.Selector]).Value;
                foreach (var arm in plan.Arms)
                {
                    var match = arm.Pattern.MatchResolved(selector, ResolveQuoteNode);
                    if (match.Diagnostics.Count > 0) return Failure(match.Diagnostics[0]);
                    if (!match.Matched) continue;
                    foreach (var capture in match.Captures) arguments.Add(capture.Key, capture.Value);
                    template = arm.Result.Syntax;
                    selectedSource = arm.Source;
                    break;
                }
            }
            else template = _language.RewriteTemplate(rule).Tree;
            if (template is null) { _instances[node] = null; return null; }
            var constructed = new QuoteInstantiator(template, arguments, proxies: true).Build();
            if (!constructed.Success) return Failure(constructed.Diagnostics[0]);
            _ownedQuotes.Add(constructed);
            var project = new Nitrogen.Binding.Project(_language);
            project.Set(Language.RewritePath, constructed.Tree!);
            var file = new ProjectSemantics(project)[Language.RewritePath];
            file.Holes = constructed.ProxyNodes.ToDictionary(pair => pair.Key,
                pair => new RewriteSource(_quoteOwners[pair.Value.Tree], pair.Value.Node));
            file._quoteOwners = _quoteOwners;
            _quoteOwners[constructed.Tree!] = file;
            file._chain = _chain.Add(rule);
            instance = new RewriteInstance(file, constructed.Tree!.Root, rule, selectedSource);
        }
        _instances[node] = instance;
        return instance;

        RewriteInstance? Failure(QuoteDiagnostic diagnostic)
        {
            _instanceErrors[node] = diagnostic;
            _instances[node] = null;
            return null;
        }
    }

    QuoteNode ResolveQuoteNode(QuoteNode value)
    {
        var seen = new HashSet<(SyntaxTree, int)>();
        while (!value.Tree.IsDisposed && (uint)value.Node < (uint)value.Tree.NodeCount &&
            _quoteOwners.TryGetValue(value.Tree, out var owner))
        {
            if (!seen.Add((value.Tree, value.Node))) return value with { Node = -1 };
            if (value.Tree.Kind(value.Node) != SyntaxKinds.Hole) return value;
            if (owner.Holes is null || !owner.Holes.TryGetValue(value.Node, out var source)) return value with { Node = -1 };
            value = new(source.File.Tree, source.Node, value.Origin);
        }
        return value with { Node = -1 };
    }

    /// <summary>The unqualified name of the rule <paramref name="node"/> uses, for messages about its expansion.</summary>
    internal string RewriteName(int node)
    {
        string name = _language.GetKindName(Tree.Kind(node));
        return name[(name.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// Each rewrite use's expansion diagnostics, at the use, prefixed with the rule's name: the instantiation's binding
    /// diagnostics (a name the template refers to resolves in its own project only) and its semantic diagnostics.
    /// </summary>
    List<SemanticDiagnostic> ExpansionDiagnostics()
    {
        if (_expansions is not null) return _expansions;
        var found = new List<SemanticDiagnostic>();
        for (int node = 0; node < Tree.NodeCount; node++)
            if (Instance(node) is { } instance)
            {
                string name = RewriteName(node);
                var file = instance.File;
                var helper = instance.Source;
                IReadOnlyList<CompositionDiagnosticLocation> related = helper is null ? [] :
                    new[] { new CompositionDiagnosticLocation(helper.Path, new(helper.Start, helper.Length)) };
                foreach (var diagnostic in file._project.Project.Diagnostics(file.Path))
                    found.Add(new SemanticDiagnostic(diagnostic.Code, Tree.Span(node), $"in the expansion of {name}: {diagnostic.Message}") { Related = related });
                foreach (var diagnostic in file.Diagnostics())
                    found.Add(new SemanticDiagnostic(diagnostic.Code, Tree.Span(node), $"in the expansion of {name}: {diagnostic.Message}")
                    { Related = diagnostic.Related.Concat(related).Distinct().ToArray() });
            }
        return _expansions = found;
    }
}
