using Gravity.MotionDSL.Compiler;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantics;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

/// <summary>Motion typing against the corpus and the hand compiler: spec §7 items 1–3 (issue 239).</summary>
public class MotionTypingOracleTests(ITestOutputHelper output)
{
    static List<SemanticDiagnostic> Typing(string path, string text)
    {
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set(path, parsed.Tree);
        return new ProjectSemantics(project)[path].Diagnostics().ToList();
    }

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.MotionFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void A_clean_corpus_file_types_without_diagnostics(string path)
    {
        if (path.Length == 0) return;
        Assert.Empty(Typing(path, File.ReadAllText(path)).Select(d => $"{d} '{File.ReadAllText(path).Substring(d.Span.Start, Math.Min(d.Span.Length, 60))}'"));
    }

    [Fact]
    public void The_compiler_and_typing_agree_on_type_errors()
    {
        if (MotionCorpus.SkillFiles().Count == 0) return;
        int compilerErrors = 0, typingErrors = 0;
        var failures = new List<string>();
        foreach (string path in MotionCorpus.SkillFiles())
        {
            string text = File.ReadAllText(path);
            foreach (var (mutant, site, description) in Mutants(text))
            {
                var compiler = Compile(mutant);
                var typing = Typing(path, mutant).Where(d => d.Code is "MT0001" or "MT0003").ToList();
                if (compiler.TypeMismatch)
                {
                    compilerErrors++;
                    if (!typing.Any(d => Overlaps(d.Span, site)))
                        failures.Add($"{System.IO.Path.GetFileName(path)}: {description}: compiler '{compiler.Message}', typing [{string.Join("; ", typing)}]");
                }
                if (typing.Count > 0)
                {
                    typingErrors++;
                    if (!compiler.Failed)
                        failures.Add($"{System.IO.Path.GetFileName(path)}: {description}: typing [{string.Join("; ", typing)}], the compiler accepts it");
                }
            }
        }
        output.WriteLine($"{compilerErrors} compiler type errors, {typingErrors} mutants with typing errors");
        Assert.True(compilerErrors >= 200, $"only {compilerErrors} mutants gave a compiler type error");
        Assert.True(failures.Count == 0, $"{failures.Count} disagreements:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void Seconds_turned_to_degrees_in_a_track_time_are_MT0002()
    {
        if (MotionCorpus.SkillFiles().Count == 0) return;
        int sites = 0;
        var failures = new List<string>();
        foreach (string path in MotionCorpus.SkillFiles())
        {
            string text = File.ReadAllText(path);
            var spans = new List<TextSpan>();
            using (var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File))
            {
                var tree = parsed.Tree;
                for (int node = 0; node < tree.NodeCount; node++)
                    if (tree.Kind(node) == MotionKinds.Num && tree.Parent(node) is int parent && parent >= 0
                        && tree.Kind(parent) == MotionKinds.Track && tree.GetText(node).EndsWith("s", StringComparison.Ordinal))
                        spans.Add(tree.Span(node));
            }
            foreach (var span in BindingOracle.Sample(spans, 4))
            {
                sites++;
                string mutant = text[..(span.End - 1)] + "deg" + text[span.End..];
                var site = new TextSpan(span.Start, span.Length + 2);
                if (!Typing(path, mutant).Any(d => d.Code == "MT0002" && Overlaps(d.Span, site)))
                    failures.Add($"{System.IO.Path.GetFileName(path)}: '{text.Substring(span.Start, span.Length)}' → deg gave no MT0002");
            }
        }
        output.WriteLine($"{sites} track times turned to degrees");
        Assert.True(sites >= 20, $"only {sites} track times");
        Assert.True(failures.Count == 0, $"{failures.Count} misses:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void Every_nested_mapping_in_the_corpus_resolves_in_the_called_skill()
    {
        if (MotionCorpus.SkillFiles().Count == 0) return;
        int mappings = 0;
        var failures = new List<string>();
        foreach (string path in MotionCorpus.SkillFiles())
        {
            using var parsed = NitrogenMotionParser.Language.Parse(File.ReadAllText(path), MotionModule.File);
            var project = new Project(NitrogenMotionParser.Language);
            var binding = project.Set(path, parsed.Tree);
            foreach (var reference in binding.References)
            {
                int kind = parsed.Tree.Kind(reference.Node);
                if (kind != MotionKinds.ArgumentMapping && kind != MotionKinds.InputMapping) continue;
                if (Inside(parsed.Tree, reference.Node, MotionKinds.Mpc)) continue; // mpc inputs are untyped
                mappings++;
                var symbols = project.Resolve(reference);
                if (symbols.Count != 1 || symbols[0].Kind != "value")
                    failures.Add($"{System.IO.Path.GetFileName(path)}: {reference} resolves to [{string.Join(", ", symbols)}]");
            }
        }
        output.WriteLine($"{mappings} nested mappings");
        Assert.True(mappings >= 5, $"only {mappings} nested mappings");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Type-directed mutants of a skill file, sampled per kind:
    /// <list type="bullet">
    /// <item>a number becomes <c>true</c> or <c>not</c> of itself;</item>
    /// <item>a boolean becomes <c>1</c>;</item>
    /// <item>a call gains an argument.</item>
    /// </list>
    /// </summary>
    static List<(string Text, TextSpan Site, string Description)> Mutants(string text)
    {
        var numbers = new List<TextSpan>();
        var booleans = new List<TextSpan>();
        var calls = new List<TextSpan>();
        using (var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File))
        {
            var tree = parsed.Tree;
            for (int node = 0; node < tree.NodeCount; node++)
            {
                int kind = tree.Kind(node);
                if (kind == MotionKinds.Num) numbers.Add(tree.Span(node));
                else if (kind == MotionKinds.True || kind == MotionKinds.False) booleans.Add(tree.Span(node));
                else if (kind == MotionKinds.Call) calls.Add(tree.Span(node));
            }
        }

        var mutants = new List<(string, TextSpan, string)>();
        foreach (var span in BindingOracle.Sample(numbers, 12))
        {
            string original = text.Substring(span.Start, span.Length);
            mutants.Add((BindingOracle.Rename(text, span, "true"), new TextSpan(span.Start, 4), $"'{original}' → true"));
            mutants.Add((BindingOracle.Rename(text, span, "not " + original), new TextSpan(span.Start, span.Length + 4), $"'{original}' → not"));
        }
        foreach (var span in BindingOracle.Sample(booleans, 6))
            mutants.Add((BindingOracle.Rename(text, span, "1"), new TextSpan(span.Start, 1), $"'{text.Substring(span.Start, span.Length)}' → 1"));
        foreach (var span in BindingOracle.Sample(calls, 6))
        {
            int close = span.End - 1;
            int open = text.IndexOf('(', span.Start);
            string extra = string.IsNullOrWhiteSpace(text[(open + 1)..close]) ? "1" : ", 1";
            mutants.Add((text[..close] + extra + text[close..], new TextSpan(span.Start, span.Length + extra.Length),
                $"'{text.Substring(span.Start, span.Length)}' + an argument"));
        }
        return mutants;
    }

    static (bool TypeMismatch, bool Failed, string Message) Compile(string text)
    {
        try
        {
            MotionCompiler.Compile(new MotionParser(new MotionLexer(text).Tokenize()).ParseFile());
            return (false, false, "");
        }
        catch (SkillCompileException e)
        {
            return (e.Code == SkillDiagnosticCode.TypeMismatch, true, e.Message);
        }
        catch (Exception e)
        {
            return (false, true, e.Message);
        }
    }

    static bool Overlaps(TextSpan a, TextSpan b) => a.Start <= b.End && b.Start <= a.End;

    static bool Inside(SyntaxTree tree, int node, int kind)
    {
        for (int p = tree.Parent(node); p >= 0; p = tree.Parent(p))
            if (tree.Kind(p) == kind) return true;
        return false;
    }
}
