using System.Collections.Immutable;
using Nitrogen.Semantic;

namespace Nitrogen.Semantics;

/// <summary>Uses of rewrite rules (spec: quasiquote rewrites): each use's per-use instantiation of its template.</summary>
public sealed partial class FileSemantics
{
    /// <summary>A rewrite's use as a per-use instantiation of its template: its semantics, the template's root, and its rule.</summary>
    internal sealed record RewriteInstance(FileSemantics File, int Root, DeclarativeRule Rule);

    readonly Dictionary<int, RewriteInstance?> _instances = new();
    readonly HashSet<int> _cyclic = new();
    List<SemanticDiagnostic>? _expansions;

    /// <summary>In an instantiation: the rules being expanded around it, to stop a rewrite that reaches itself.</summary>
    ImmutableHashSet<DeclarativeRule> _chain = ImmutableHashSet<DeclarativeRule>.Empty;

    /// <summary>In an instantiation: the use's document, and for each Hole node the use's child it stands for.</summary>
    internal (FileSemantics File, IReadOnlyDictionary<int, int> Children)? Holes { get; private set; }

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
        else if (_language.RewriteTemplate(rule).Parsed is { } parsed)
        {
            var template = parsed.Tree;
            var project = new Nitrogen.Binding.Project(_language);
            project.Set("rewrite", template);
            var file = new ProjectSemantics(project)["rewrite"];
            var children = new Dictionary<int, int>();
            for (int hole = 0; hole < template.NodeCount; hole++)
                if (template.Kind(hole) == SyntaxKinds.Hole)
                {
                    string name = template.GetText(hole)[1..].ToString();
                    children[hole] = Tree.Child(node, spec.Holes.First(h => h.Name == name).Child);
                }
            file.Holes = (this, children);
            file._chain = _chain.Add(rule);
            instance = new RewriteInstance(file, template.Root, rule);
        }
        _instances[node] = instance;
        return instance;
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
                foreach (var diagnostic in file._project.Project.Diagnostics(file.Path))
                    found.Add(new SemanticDiagnostic(diagnostic.Code, Tree.Span(node), $"in the expansion of {name}: {diagnostic.Message}"));
                foreach (var diagnostic in file.Diagnostics())
                    found.Add(new SemanticDiagnostic(diagnostic.Code, Tree.Span(node), $"in the expansion of {name}: {diagnostic.Message}"));
            }
        return _expansions = found;
    }
}
