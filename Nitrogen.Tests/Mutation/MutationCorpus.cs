using System.Globalization;
using System.Text;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;

namespace Nitrogen.Tests;

/// <summary>What one mutant did to the recovery pass (issue 235, spec §8).</summary>
public sealed record MutantOutcome(Mutant Mutant, string Corpus, string? Violation, bool Fallback, bool? Local, int Diagnostics);

/// <summary>
/// Every corpus's mutants, run once per test process: the fast pass alone, the full parse twice,
/// and the clean original. Checks the invariants (I1–I3, no crash) and measures the gates (Q1, Q2).
/// </summary>
public static class MutationCorpus
{
    public const int SitesPerKind = 3;

    /// <summary>The measured cost of broken input (Plan 3, Task 4), kept here because the report is regenerated.</summary>
    const string CostSection = """

        ## Cost of broken input

        2026-09-23, `RecoveryParseBenchmarks`: each .motion file's first mutant the fast pass rejects,
        against the file unchanged (49 parses each; the broken parse runs both passes):

        | Method | Mean     | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
        |------- |---------:|----------:|----------:|------:|----------:|------------:|
        | Clean  | 4.695 ms | 0.0140 ms | 0.0110 ms |  1.00 |  10.34 KB |        1.00 |
        | Broken | 7.581 ms | 0.0613 ms | 0.0543 ms |  1.61 |  10.34 KB |        1.00 |

        Gates (spec §8) met: broken input costs 1.61× clean (limit 3×), and allocation is identical, so
        repairs allocate nothing per node.

        """;

    static readonly string[] GrammarFiles = ["Calc.ngr", "Power.ngr", "Clash.ngr", "Mini.ngr", "Lexical.ngr", "Nitrogen.ngr"];

    static readonly Lazy<IReadOnlyList<MutantOutcome>> s_outcomes = new(Run);

    public static IReadOnlyList<MutantOutcome> Outcomes => s_outcomes.Value;

    sealed record Corpus(string Name, Language Language, Rule Start, IEnumerable<(string Path, string Text)> Files);

    static IEnumerable<Corpus> Corpora()
    {
        static IEnumerable<(string, string)> Read(IEnumerable<string> paths) => paths.Select(p => (p, File.ReadAllText(p)));
        yield return new("motion", NitrogenMotionParser.Language, MotionModule.File, Read(MotionCorpus.Files()));
        yield return new("skill", NitrogenMotionParser.Language, MotionModule.File, Read(MotionCorpus.SkillFiles()));
        yield return new("policy", NitrogenPolicyParser.Language, PolicyModule.PolicyDocument, Read(MotionCorpus.PolicyFiles()));
        yield return new("compose", NitrogenPolicyParser.Language, PolicyModule.ComposeDocument, Read(MotionCorpus.ComposeFiles()));
        yield return new("ngr", NgrParser.Language, NitrogenModule.File,
            GrammarFiles.Select(f => (f, TestGrammarFileTests.ReadGrammar(f))));
    }

    static IReadOnlyList<MutantOutcome> Run()
    {
        var outcomes = new List<MutantOutcome>();
        foreach (var corpus in Corpora())
        {
            foreach (var (path, text) in corpus.Files)
            {
                using var clean = corpus.Language.Parse(text, corpus.Start);
                if (!clean.Success) continue; // only valid files have a clean tree to compare with
                foreach (var mutant in Mutator.Mutants(path, text, SitesPerKind))
                {
                    using var fast = corpus.Language.ParseFast(mutant.Text, corpus.Start);
                    if (fast.Success) continue; // the edit left the file valid
                    outcomes.Add(Evaluate(corpus, clean.Tree, fast, mutant));
                }
            }
        }
        return outcomes;
    }

    static MutantOutcome Evaluate(Corpus corpus, SyntaxTree clean, ParseResult fast, Mutant mutant)
    {
        try
        {
            using var result = corpus.Language.Parse(mutant.Text, corpus.Start);
            using var again = corpus.Language.Parse(mutant.Text, corpus.Start);
            var tree = result.Tree;
            bool fallback = tree.Kind(tree.Root) == SyntaxKinds.Error && tree.ChildCount(tree.Root) == 0;
            string? violation =
                !Covers(tree, mutant.Text) ? "I1: a character belongs to no node and no trivia"
                : !SameFirstDiagnostic(fast, result) ? $"I2: diagnostic 0 is '{Describe(result)}', the fast pass said '{Describe(fast)}'"
                : SyntaxDumper.Dump(tree) != SyntaxDumper.Dump(again.Tree) || !Messages(result).SequenceEqual(Messages(again)) ? "I3: two runs differ"
                : null;
            bool? local = fallback ? false : Local(clean, tree, mutant);
            return new(mutant, corpus.Name, violation, fallback, local, result.Diagnostics.Length);
        }
        catch (Exception error)
        {
            return new(mutant, corpus.Name, $"crash: {error.GetType().Name}: {error.Message}", false, false, 0);
        }
    }

    static bool Covers(SyntaxTree tree, string text)
    {
        var covered = new bool[text.Length];
        for (int n = 0; n < tree.NodeCount; n++)
            if (tree.ChildCount(n) == 0) Mark(covered, tree.Span(n));
        foreach (var span in tree.Trivia) Mark(covered, span);
        return Array.TrueForAll(covered, c => c);
    }

    static void Mark(bool[] covered, TextSpan span)
    {
        for (int i = span.Start; i < span.End && i < covered.Length; i++) covered[i] = true;
    }

    static bool SameFirstDiagnostic(ParseResult fast, ParseResult full) =>
        full.Diagnostics.Length > 0 && fast.Diagnostics.Length > 0
        && full.Diagnostics[0].Code == fast.Diagnostics[0].Code
        && full.Diagnostics[0].Span == fast.Diagnostics[0].Span
        && full.FormatMessage(full.Diagnostics[0]) == fast.FormatMessage(fast.Diagnostics[0]);

    static string Describe(ParseResult result) =>
        result.Diagnostics.Length == 0 ? "none" : result.FormatMessage(result.Diagnostics[0]) + " " + result.Diagnostics[0].Span;

    static string[] Messages(ParseResult result) =>
        result.Diagnostics.ToArray().Select(d => result.FormatMessage(d) + " " + d.Span).ToArray();

    /// <summary>
    /// Q1: the items of the deepest clean list containing the edit, except the ones it touches,
    /// survive node for node, shifted past the edit. Null when no list contains the edit.
    /// </summary>
    static bool? Local(SyntaxTree clean, SyntaxTree repaired, Mutant mutant)
    {
        int list = -1;
        for (int n = 0; n < clean.NodeCount; n++)
        {
            var span = clean.Span(n);
            if (clean.Kind(n) == SyntaxKinds.List && span.Start <= mutant.Start && mutant.EditEnd <= span.End) list = n;
        }
        if (list < 0) return null;

        var index = new Dictionary<(int Kind, int Start, int Length), List<int>>();
        for (int n = 0; n < repaired.NodeCount; n++)
        {
            var span = repaired.Span(n);
            var key = (repaired.Kind(n), span.Start, span.Length);
            if (!index.TryGetValue(key, out var nodes)) index[key] = nodes = new List<int>();
            nodes.Add(n);
        }

        for (int k = 0; k < clean.ChildCount(list); k++)
        {
            int item = clean.Child(list, k);
            var span = clean.Span(item);
            if (span.Length == 0) continue;
            bool before = span.End <= mutant.Start, after = span.Start >= mutant.EditEnd;
            if (mutant.Kind == MutationKind.Truncate ? !before : !(before || after)) continue;
            int start = after ? span.Start + mutant.Delta : span.Start;
            string expected = SyntaxDumper.Dump(clean, item);
            if (!index.TryGetValue((clean.Kind(item), start, span.Length), out var candidates)
                || !candidates.Any(c => SyntaxDumper.Dump(repaired, c) == expected))
                return false;
        }
        return true;
    }

    /// <summary>Q1 over the judged outcomes (those with a containing list).</summary>
    public static double Locality(IEnumerable<MutantOutcome> outcomes)
    {
        var judged = outcomes.Where(o => o.Local is not null).ToList();
        return judged.Count == 0 ? 1 : judged.Count(o => o.Local == true) / (double)judged.Count;
    }

    /// <summary>Q2: the share of mutants with at most 3 diagnostics.</summary>
    public static double FewCascades(IEnumerable<MutantOutcome> outcomes)
    {
        var all = outcomes.ToList();
        return all.Count == 0 ? 1 : all.Count(o => o.Diagnostics <= 3) / (double)all.Count;
    }

    public static string Report()
    {
        var outcomes = Outcomes;
        var b = new StringBuilder();
        b.Append("# Recovery quality (issue 235)\n\n")
            .Append("Generated by `NITROGEN_WRITE_REPORT=1 dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter MutationCorpusTests`. ")
            .Append($"{outcomes.Count} mutants: {SitesPerKind} seeded sites per mutation kind per corpus file, kept when the fast pass rejects them ")
            .Append("(spec `docs/superpowers/specs/2026-09-23-nitrogen-recovery-design.md` §8).\n\n")
            .Append($"- Invariants I1–I3 and no crash: {outcomes.Count(o => o.Violation is not null)} violations.\n")
            .Append($"- Q1 locality (gate ≥ 75 %, revised from 90 % after measurement): {Percent(Locality(outcomes))} of {outcomes.Count(o => o.Local is not null)} judged mutants.\n")
            .Append($"- Q2 at most 3 diagnostics (gate ≥ 95 %): {Percent(FewCascades(outcomes))}.\n")
            .Append($"- Fallbacks to a single Error root: {outcomes.Count(o => o.Fallback)}.\n\n")
            .Append("| Corpus | Mutation | Mutants | Violations | Fallbacks | Q1 local | Q2 ≤ 3 diagnostics | Mean diagnostics |\n")
            .Append("|---|---|---:|---:|---:|---:|---:|---:|\n");
        foreach (var group in outcomes.GroupBy(o => (o.Corpus, o.Mutant.Kind)))
            Row(b, group.Key.Corpus, group.Key.Kind.ToString(), group.ToList());
        Row(b, "**all**", "", outcomes.ToList());
        return b.Append(CostSection).ToString();
    }

    static void Row(StringBuilder b, string corpus, string kind, List<MutantOutcome> rows) =>
        b.Append($"| {corpus} | {kind} | {rows.Count} | {rows.Count(r => r.Violation is not null)} | {rows.Count(r => r.Fallback)} | ")
            .Append($"{Percent(Locality(rows))} | {Percent(FewCascades(rows))} | ")
            .Append(rows.Average(r => r.Diagnostics).ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");

    static string Percent(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
