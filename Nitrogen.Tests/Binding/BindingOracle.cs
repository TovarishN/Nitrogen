using Nitrogen.Binding;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// The binding oracles of spec §6 for any language (issue 237): clean files bind silently; every
/// name error the hand compiler reports on a mutant is a binding diagnostic at the mutated name;
/// definitions, references and renames round-trip.
/// </summary>
/// <param name="startFor">The start rule for a corpus path.</param>
/// <param name="nameError">The hand parser and compiler on (path, text): whether they reject it for a name, and their message.</param>
internal sealed class BindingOracle(Language language, Func<string, Rule> startFor, Func<string, string, (bool IsNameError, string Message)> nameError)
{
    public const string Suffix = "_zz";

    /// <summary>One file parsed and bound as a one-document project.</summary>
    public sealed class Bound : IDisposable
    {
        public Bound(Language language, Rule start, string path, string text)
        {
            Path = path;
            Text = text;
            Parsed = language.Parse(text, start);
            Project = new Project(language);
            Binding = Project.Set(path, Parsed.Tree);
        }

        public string Path { get; }

        public string Text { get; }

        public ParseResult Parsed { get; }

        public Project Project { get; }

        public FileBinding Binding { get; }

        public IReadOnlyList<BindingDiagnostic> Diagnostics => Project.Diagnostics(Path);

        public string Describe(BindingDiagnostic diagnostic) =>
            $"{diagnostic} '{Text.Substring(diagnostic.Span.Start, diagnostic.Span.Length)}'";

        public void Dispose() => Parsed.Dispose();
    }

    sealed record Mutant(string Text, string Description, HashSet<int> Expected);

    public Bound Bind(string path, string text) => new(language, startFor(path), path, text);

    public string[] CleanFileDiagnostics(string path)
    {
        using var bound = Bind(path, File.ReadAllText(path));
        Assert.True(bound.Parsed.Success, $"{path} does not parse");
        return bound.Diagnostics.Select(bound.Describe).ToArray();
    }

    public (int Checked, List<string> Failures) CompilerAgreement(IEnumerable<string> corpus)
    {
        var failures = new List<string>();
        int checkedMutants = 0;
        foreach (string path in corpus)
        {
            using var bound = Bind(path, File.ReadAllText(path));
            foreach (var mutant in Mutants(bound))
            {
                var (isNameError, error) = nameError(path, mutant.Text);
                if (!isNameError) continue;
                checkedMutants++;
                using var mutated = Bind(path, mutant.Text);
                bool found = mutated.Diagnostics.Any(d =>
                    d.Code is BindingCodes.Unresolved or BindingCodes.Duplicate or BindingCodes.NotVisible
                    && mutant.Expected.Contains(d.Span.Start));
                if (!found)
                    failures.Add($"{path}: {mutant.Description}: compiler '{error}', binder [{string.Join("; ", mutated.Diagnostics.Select(mutated.Describe))}]");
            }
        }
        return (checkedMutants, failures);
    }

    public (int References, List<string> Failures) RoundTrips(IEnumerable<string> corpus)
    {
        var failures = new List<string>();
        int references = 0;
        foreach (string path in corpus)
        {
            using var bound = Bind(path, File.ReadAllText(path));
            var project = bound.Project;
            var uses = new Dictionary<Symbol, IReadOnlyList<Reference>>();
            foreach (var reference in bound.Binding.References.Where(project.IsEffective))
            {
                references++;
                var resolved = project.Resolve(reference);
                if (!project.DefinitionAt(path, reference.NameSpan.Start).SequenceEqual(resolved))
                    failures.Add($"{path}: DefinitionAt differs from Resolve for {reference}");
                foreach (var symbol in resolved)
                {
                    if (symbol.Name != reference.Name) failures.Add($"{path}: {reference} resolved to {symbol}");
                    if (!uses.TryGetValue(symbol, out var list)) uses[symbol] = list = project.ReferencesTo(symbol);
                    if (!list.Contains(reference)) failures.Add($"{path}: ReferencesTo({symbol}) misses {reference}");
                }
            }
        }
        return (references, failures);
    }

    public (int Renamed, List<string> Failures) Renames(IEnumerable<string> corpus)
    {
        var failures = new List<string>();
        int renamed = 0;
        foreach (string path in corpus)
        {
            string text = File.ReadAllText(path);
            using var bound = Bind(path, text);
            foreach (var declaration in Sample(bound.Binding.Declarations, 5))
            {
                renamed++;
                var expected = bound.Project.ReferencesTo(declaration).Select(r => r.Index)
                    // A reference on the declaration's own name (a node that declares and references) is renamed with it.
                    .Concat(bound.Binding.References.Where(r => r.NameSpan == declaration.NameSpan).Select(r => r.Index))
                    .ToHashSet();
                // A qualified reference under a changed one looks into what that one named (issue 239): it changes with it.
                foreach (var reference in bound.Binding.References)
                    if (reference.Qualifier is not null && Under(bound.Binding, reference, expected)) expected.Add(reference.Index);
                using var mutated = Bind(path, Rename(text, declaration.NameSpan, declaration.Name + Suffix));
                if (mutated.Binding.References.Count != bound.Binding.References.Count)
                {
                    failures.Add($"{path}: renaming {declaration} changed the reference count");
                    continue;
                }
                var changed = new HashSet<int>();
                for (int i = 0; i < bound.Binding.References.Count; i++)
                    if (Key(bound.Project, bound.Binding.References[i], declaration.NameSpan) != Key(mutated.Project, mutated.Binding.References[i], null))
                        changed.Add(i);
                if (!changed.SetEquals(expected))
                    failures.Add($"{path}: renaming {declaration} changed [{string.Join(",", changed.Order())}], expected [{string.Join(",", expected.Order())}]");
            }
        }
        return (renamed, failures);
    }

    static bool Under(FileBinding binding, Reference reference, HashSet<int> changed)
    {
        for (int e = reference.Enclosing; e >= 0; e = binding.References[e].Enclosing)
            if (changed.Contains(e)) return true;
        return false;
    }

    public static string Report(string what, List<string> failures) =>
        $"{failures.Count} {what}:\n" + string.Join("\n", failures.Take(40));

    /// <summary>
    /// Per file: renamed references, renamed declarations (their uses are expected to fail) and
    /// declarations renamed onto an earlier sibling (a duplicate). Evenly sampled, deterministic.
    /// </summary>
    static IEnumerable<Mutant> Mutants(Bound bound)
    {
        string text = bound.Text;
        var references = bound.Binding.References.Where(bound.Project.IsEffective).ToList();
        foreach (var reference in Sample(references, 12))
            yield return new Mutant(Rename(text, reference.NameSpan, reference.Name + Suffix),
                $"rename reference {reference}", [reference.NameSpan.Start]);

        foreach (var declaration in Sample(bound.Binding.Declarations, 6))
        {
            var uses = bound.Project.ReferencesTo(declaration);
            if (uses.Count == 0) continue;
            yield return new Mutant(Rename(text, declaration.NameSpan, declaration.Name + Suffix),
                $"rename declaration {declaration}",
                uses.Select(r => Shifted(r.NameSpan.Start, declaration.NameSpan)).ToHashSet());
        }

        var siblings = bound.Binding.Declarations
            .GroupBy(d => (d.Kind, d.Scope))
            .SelectMany(g => g.Zip(g.Skip(1)))
            .Where(pair => pair.First.Name != pair.Second.Name)
            .ToList();
        foreach (var (first, second) in Sample(siblings, 4))
            yield return new Mutant(Rename(text, second.NameSpan, first.Name),
                $"rename {second} onto {first.Name}", [second.NameSpan.Start]);
    }

    public static IEnumerable<T> Sample<T>(IReadOnlyList<T> items, int count) =>
        items.Count <= count ? items : Enumerable.Range(0, count).Select(i => items[i * items.Count / count]);

    public static string Rename(string text, TextSpan span, string name) => text[..span.Start] + name + text[span.End..];

    /// <summary>A position in the original text, moved to the text where <paramref name="renamed"/> gained the suffix.</summary>
    static int Shifted(int position, TextSpan renamed) => position > renamed.Start ? position + Suffix.Length : position;

    /// <summary>What a reference resolves to, as declaration positions in the renamed text (or built-in names).</summary>
    static string Key(Project project, Reference reference, TextSpan? renamed)
    {
        if (!project.IsEffective(reference)) return "-";
        return string.Join(",", project.Resolve(reference).Select(s =>
            s.IsBuiltin ? "builtin " + s.Name
            : (renamed is { } span ? Shifted(s.NameSpan.Start, span) : s.NameSpan.Start).ToString()));
    }
}
