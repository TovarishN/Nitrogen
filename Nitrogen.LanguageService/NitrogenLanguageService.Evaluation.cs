using System.Diagnostics;
using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>
/// Statement values as inlay hints: a language whose entry has an evaluation lowers its statements and
/// projects each through the profile's handlers. Nothing is shown while the document has an error, and
/// evaluation stops starting statements once its budget is spent.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    sealed record HintCacheEntry(int DocumentVersion, int ProjectVersion, LanguageEntry Language, IReadOnlyList<ValueHint> Hints);

    readonly Dictionary<string, HintCacheEntry> _hints = new(StringComparer.Ordinal);

    /// <summary>How long one document's statements may run; the first always runs, later ones start only within it.</summary>
    internal TimeSpan EvaluationBudget { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The values of the document's statements within <paramref name="range"/>, or of a C# host's tagged strings at host positions; empty when its language shows none.</summary>
    public IReadOnlyList<ValueHint> ValueHints(string uri, DocumentRange range)
    {
        if (_hosts.TryGetValue(uri, out var host)) return HostValueHints(host).Where(h => Within(h.At, range)).ToList();
        if (!_documents.TryGetValue(uri, out var document) || document.Language.Evaluation is not { } evaluation) return [];
        var project = _projects[document.Language];
        if (!_hints.TryGetValue(uri, out var hit) || hit.DocumentVersion != document.Version ||
            hit.ProjectVersion != project.Version || !ReferenceEquals(hit.Language, document.Language))
        {
            hit = new HintCacheEntry(document.Version, project.Version, document.Language, Evaluate(document, evaluation));
            _hints[uri] = hit;
        }
        return hit.Hints.Where(h => Within(h.At, range)).ToList();
    }

    IReadOnlyList<ValueHint> Evaluate(Document document, BoundEvaluation evaluation)
    {
        if (Diagnostics(document.Uri).Any(d => d.Severity == ServiceSeverity.Error)) return [];
        var profile = evaluation.Profile;
        var file = SemanticsOf(document.Language)[document.Uri];
        var lowered = HirLowering.LowerSelected(file, profile.StatementKinds, Guid.NewGuid());
        if (lowered.Diagnostics.Count > 0) return [];

        var hints = new List<ValueHint>();
        var clock = Stopwatch.StartNew();
        foreach (var root in lowered.Roots)
        {
            if (hints.Count > 0 && clock.Elapsed >= EvaluationBudget) break;
            var at = document.Lines.PositionOf(StatementEnd(file.Tree, root.Origins[0], profile.StatementKinds, document.Text));
            hints.Add(Hint(at, root, evaluation));
        }
        return hints;
    }

    static ValueHint Hint(DocumentPosition at, HirNode root, BoundEvaluation evaluation)
    {
        var (value, failure) = Project(root, evaluation);
        return value is not null ? new ValueHint(at, "= " + value, null, false) : Failure(at, failure!);
    }

    /// <summary>A lowered node's value through the evaluation, formatted; or, when it has none, why.</summary>
    static (string? Value, string? Failure) Project(HirNode node, BoundEvaluation evaluation)
    {
        try
        {
            var inputs = HirTraversal.PreOrder(node).OfType<HirSymbolRef>()
                .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
                .Select(s => (Symbol: s, Value: evaluation.Profile.Builtin(s))).Where(p => p.Value is not null)
                .ToDictionary(p => p.Symbol, p => p.Value!);
            var result = HirProjector.Project(node, evaluation.Registry, inputs);
            return result.Value is { } value
                ? (evaluation.Profile.Format(value), null)
                : (null, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }
        catch (Exception error) // a profile's Builtin or Format threw; handlers' own exceptions are NP0005 diagnostics
        {
            return (null, $"{error.GetType().Name}: {error.Message}");
        }
    }

    static ValueHint Failure(DocumentPosition at, string reason) => new(at, "= ⚠", reason, true);

    /// <summary>The end of the statement the origin lies in, before its trailing whitespace: where its value is shown.</summary>
    static int StatementEnd(SyntaxTree tree, SourceOrigin origin, IReadOnlySet<int> statementKinds, string text)
    {
        int node = origin.Node;
        while (node >= 0 && !statementKinds.Contains(tree.Kind(node))) node = tree.Parent(node);
        var span = node >= 0 ? tree.Span(node) : origin.Span;
        int end = span.End;
        while (end > span.Start && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    static bool Within(DocumentPosition at, DocumentRange range) =>
        (at.Line, at.Character).CompareTo((range.Start.Line, range.Start.Character)) >= 0 &&
        (at.Line, at.Character).CompareTo((range.End.Line, range.End.Character)) <= 0;
}
