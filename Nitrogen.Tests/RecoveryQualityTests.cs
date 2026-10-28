using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nitrogen.Tests.Recovery;
using Nitrogen.Tests.RecoveryCSharp;
using Nitrogen.Tests.Lists;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

/// <summary>Deterministic quality measurements; passing invariants do not imply recovery parity.</summary>
public class RecoveryQualityTests(ITestOutputHelper output)
{
    record Fixture(string Id, Language Language, Rule Start, string Text, int ItemKind, bool CSharp = false);
    record Edit(string Family, int Start, int Length, string Replacement)
    {
        public string Apply(string text) => text[..Start] + Replacement + text[(Start + Length)..];
    }
    sealed class Score
    {
        public int Cases { get; set; }
        public int Eligible { get; set; }
        public int Preserved { get; set; }
        public int Diagnostics { get; set; }
        public int ErrorRoots { get; set; }
        public int RoslynPreserved { get; set; }
        public int RoslynDiagnostics { get; set; }
    }
    record Interval(int Start, int Length) { public int End => Start + Length; }

    static readonly Language Recovery = new LanguageBuilder().Add(RecoveryModule.Instance).Build();
    static readonly Language Lists = new LanguageBuilder().Add(ListsModule.Instance).Build();
    static readonly Language CSharp = new LanguageBuilder().Add(RecoveryCSharpModule.Instance).Build();
    static readonly CSharpParseOptions Options = new(LanguageVersion.CSharp4);

    // Original local baselines, except the two explicitly attributed Nitride declarations below.
    static readonly Fixture[] Fixtures =
    [
        new("items", Recovery, RecoveryModule.File, "item a { x = 1; y = 2; } item b { z = 3; }", RecoveryKinds.Field),
        new("lists", Lists, ListsModule.File, "set a (1, 2, 3); set b (4, 5);", ListsKinds.Set),
        new("csharp-siblings", CSharp, RecoveryCSharpModule.File, "class C { int first; object second; A third; } class D { int last; }", RecoveryCSharpKinds.Field, true),
        new("csharp-unicode", CSharp, RecoveryCSharpModule.File, "// Привет 😀\nclass C { int first; object second; }", RecoveryCSharpKinds.Field, true),
        // Nitride fixtures/recovery/manifest.json: csharp4:frozen:grammar-0 and grammar-2.
        new("nitride-csharp4-grammar-0", CSharp, RecoveryCSharpModule.File, "class C { A field; }", RecoveryCSharpKinds.Field, true),
        new("nitride-csharp4-grammar-2", CSharp, RecoveryCSharpModule.File, "class C { int field; }", RecoveryCSharpKinds.Field, true),
    ];

    static IEnumerable<Edit> Mutations(Fixture fixture)
    {
        var text = fixture.Text;
        var tokens = fixture.CSharp
            ? CSharpSyntaxTree.ParseText(text, Options).GetRoot().DescendantTokens().Where(t => t.Span.Length > 0)
                .Select(t => new Interval(t.SpanStart, t.Span.Length)).ToArray()
            : Regex.Matches(text, @"[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[^\s]")
                .Select(m => new Interval(m.Index, m.Length)).ToArray();
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            string spelling = text.Substring(t.Start, t.Length);
            yield return new("delete", t.Start, t.Length, "");
            yield return new("insert", t.Start, 0, "?? ");
            yield return new("duplicate", t.Start, 0, spelling + " ");
            yield return new("truncate", t.Start, text.Length - t.Start, "");
            if (spelling is "}" or ")" or "]") yield return new("missing-closer", t.Start, t.Length, "");
            if (i + 1 < tokens.Length)
            {
                var next = tokens[i + 1];
                yield return new("swap", t.Start, next.End - t.Start,
                    text.Substring(next.Start, next.Length) + text[t.End..next.Start] + spelling);
            }
        }
    }

    static string Fingerprint(SyntaxTree tree, int node)
    {
        if (node == SyntaxTree.NoNode) return "_";
        var b = new StringBuilder().Append('(').Append(tree.Kind(node)).Append(':').Append(tree.Flags(node));
        if (tree.ChildCount(node) == 0) b.Append(':').Append(tree.GetText(node));
        for (int i = 0; i < tree.ChildCount(node); i++) b.Append(' ').Append(Fingerprint(tree, tree.Child(node, i)));
        return b.Append(')').ToString();
    }

    static string RoslynFingerprint(Microsoft.CodeAnalysis.SyntaxNode node) => string.Join("|", node.DescendantNodesAndTokensAndSelf()
        .Select(n => n.IsToken ? $"t{n.RawKind}:{n.AsToken().IsMissing}:{n.AsToken().Text}" : $"n{n.RawKind}"));

    // An insertion exactly at an item's boundary can change its recognition, so exclude touching edits.
    static bool Unaffected(Interval span, Edit edit) => span.End < edit.Start || span.Start > edit.Start + edit.Length;
    static int Map(int start, Edit edit) => start > edit.Start + edit.Length ? start + edit.Replacement.Length - edit.Length : start;

    static void Coverage(SyntaxTree tree)
    {
        // Trivia is the complement of leaves. Require every non-whitespace gap to be explicitly skipped,
        // or a comment; retaining Text alone must not count as complete syntax coverage.
        var leaves = Enumerable.Range(0, tree.NodeCount).Where(n => tree.ChildCount(n) == 0)
            .Select(tree.Span).Where(s => s.Length > 0).ToArray();
        var spans = leaves.Concat(tree.Trivia.ToArray()).OrderBy(s => s.Start).ThenBy(s => s.End).ToArray();
        int end = 0;
        foreach (var span in spans)
        {
            Assert.InRange(span.Start, end, tree.Text.Length);
            Assert.Equal(end, span.Start);
            end = span.End;
        }
        Assert.Equal(tree.Text.Length, end);
        foreach (var skipped in tree.SkippedSpans.ToArray()) Assert.InRange(skipped.End, skipped.Start, tree.Text.Length);
        foreach (var trivia in tree.Trivia.ToArray())
        {
            char[] gap = tree.Text.Substring(trivia.Start, trivia.Length).ToCharArray();
            foreach (var skipped in tree.SkippedSpans.ToArray())
                for (int i = Math.Max(trivia.Start, skipped.Start); i < Math.Min(trivia.End, skipped.End); i++) gap[i - trivia.Start] = ' ';
            string remaining = Regex.Replace(new string(gap), @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
            Assert.True(string.IsNullOrWhiteSpace(remaining), $"Unaccounted source at {trivia}: {remaining}");
        }
    }

    [Fact]
    public void Mutation_corpus_measures_recovery_and_checks_invariants()
    {
        var scores = new SortedDictionary<string, Score>();
        var losses = new List<object>();
        foreach (var f in Fixtures)
        {
            if (Environment.GetEnvironmentVariable("NITROGEN_RECOVERY_TRACE") is { Length: > 0 } fixtureTrace)
                File.WriteAllText(fixtureTrace, f.Id + " baseline");
            using var baseline = f.Language.Parse(f.Text, f.Start);
            using var reuseBase = f.Language.ParseIncremental(f.Text, f.Start);
            Assert.True(baseline.Success, f.Id);
            using var recovering = f.Language.ParseRecovering(f.Text, f.Start);
            TreeAssert.Same(baseline, recovering);
            var items = Enumerable.Range(0, baseline.Tree.NodeCount).Where(n => baseline.Tree.Kind(n) == f.ItemKind).ToArray();
            var roslynBase = f.CSharp ? CSharpSyntaxTree.ParseText(f.Text, Options).GetRoot() : null;
            if (roslynBase is not null) Assert.Empty(roslynBase.GetDiagnostics());
            foreach (var edit in Mutations(f))
            {
                if (Environment.GetEnvironmentVariable("NITROGEN_RECOVERY_TRACE") is { Length: > 0 } trace)
                    File.WriteAllText(trace, JsonSerializer.Serialize(new { f.Id, Edit = edit }));
                string text = edit.Apply(f.Text);
                using var parsed = f.Language.Parse(text, f.Start);
                using var unseeded = f.Language.ParseUnseeded(text, f.Start);
                TreeAssert.Same(unseeded, parsed);
                using var repeat = f.Language.Parse(text, f.Start);
                TreeAssert.Same(parsed, repeat);
                Coverage(parsed.Tree);
                using var incremental = f.Language.ParseIncremental(text, f.Start);
                TreeAssert.Same(parsed, incremental);
                using var reparsed = f.Language.Reparse(reuseBase, text, [new TextChange(edit.Start, edit.Length, edit.Replacement.Length)]);
                TreeAssert.Same(parsed, reparsed);
                using var corrected = f.Language.Reparse(reparsed, f.Text, [TextChange.Between(text, f.Text)]);
                TreeAssert.Same(baseline, corrected);
                string key = (f.CSharp ? "csharp-subset/" : "local/") + edit.Family;
                if (!scores.TryGetValue(key, out var score)) scores[key] = score = new();
                score.Cases++;
                score.Diagnostics += parsed.Diagnostics.Length;
                if (parsed.Tree.Kind(parsed.Tree.Root) == SyntaxKinds.Error) score.ErrorRoots++;
                var roslyn = f.CSharp ? CSharpSyntaxTree.ParseText(text, Options).GetRoot() : null;
                score.RoslynDiagnostics += roslyn?.GetDiagnostics().Count() ?? 0;
                foreach (int item in items)
                {
                    var old = baseline.Tree.Span(item);
                    if (!Unaffected(new(old.Start, old.Length), edit)) continue;
                    score.Eligible++;
                    int start = Map(old.Start, edit);
                    string fingerprint = Fingerprint(baseline.Tree, item);
                    bool kept = Enumerable.Range(0, parsed.Tree.NodeCount).Any(n => parsed.Tree.Kind(n) == f.ItemKind
                        && parsed.Tree.Span(n) == new TextSpan(start, old.Length) && Fingerprint(parsed.Tree, n) == fingerprint);
                    if (kept) score.Preserved++;
                    else losses.Add(new { Fixture = f.Id, edit.Family, edit.Start, edit.Length, edit.Replacement, ItemStart = old.Start });
                    if (roslynBase is null) continue;
                    var original = roslynBase.DescendantNodes().OfType<FieldDeclarationSyntax>().Single(n => n.SpanStart == old.Start && n.Span.Length == old.Length);
                    if (roslyn!.DescendantNodes().OfType<FieldDeclarationSyntax>().Any(n => n.SpanStart == start && n.Span.Length == old.Length
                        && RoslynFingerprint(n) == RoslynFingerprint(original))) score.RoslynPreserved++;
                }
            }
        }
        string report = JsonSerializer.Serialize(new { Schema = 1, Roslyn = "4.12.0", LanguageVersion = "CSharp4", Scores = scores, Losses = losses }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("NITROGEN_RECOVERY_REPORT") is { Length: > 0 } path) File.WriteAllText(path, report);
        Assert.Equal(12, scores.Count);
        Assert.All(scores.Values, s => Assert.True(s.Cases > 0 && s.Eligible > 0));
        var frozen = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RecoveryQuality", "baseline.json")));
        foreach (var (family, actual) in scores)
        {
            var expected = frozen.GetProperty("Scores").GetProperty(family);
            Assert.Equal(expected.GetProperty("Cases").GetInt32(), actual.Cases);
            Assert.Equal(expected.GetProperty("Eligible").GetInt32(), actual.Eligible);
            Assert.True(actual.Preserved >= expected.GetProperty("Preserved").GetInt32(), $"{family}: preservation regressed to {actual.Preserved}");
            Assert.True(actual.Diagnostics <= expected.GetProperty("Diagnostics").GetInt32(), $"{family}: diagnostics increased to {actual.Diagnostics}");
            Assert.True(actual.ErrorRoots <= expected.GetProperty("ErrorRoots").GetInt32(), $"{family}: whole-file fallback increased");
            Assert.Equal(expected.GetProperty("RoslynPreserved").GetInt32(), actual.RoslynPreserved);
            Assert.Equal(expected.GetProperty("RoslynDiagnostics").GetInt32(), actual.RoslynDiagnostics);
        }
    }
}

