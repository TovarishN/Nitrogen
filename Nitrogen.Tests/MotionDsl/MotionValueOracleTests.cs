using System.Globalization;
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

/// <summary>Value checks against the skill compiler on value-directed mutants of the corpus (issue 240, spec §8).</summary>
public class MotionValueOracleTests(ITestOutputHelper output)
{
    /// <summary>The MV codes that may stand for each compiler code this plan covers.</summary>
    internal static readonly Dictionary<SkillDiagnosticCode, string[]> Covered = new()
    {
        [SkillDiagnosticCode.InvalidExpression] = ["MV0001", "MV0004"],
        [SkillDiagnosticCode.InvalidLifecycle] = ["MV0002", "MV0005"],
        [SkillDiagnosticCode.InvalidPhase] = ["MV0001", "MV0002", "MV0006"],
        [SkillDiagnosticCode.InvalidPhaseOverride] = ["MV0001", "MV0002", "MV0009"],
        [SkillDiagnosticCode.InvalidTimeRange] = ["MV0003"],
        [SkillDiagnosticCode.IncompletePhasePose] = ["MV0007"],
        [SkillDiagnosticCode.InvalidPhaseTarget] = ["MV0008"],
        [SkillDiagnosticCode.InvalidSourceGraph] = ["MV0010"],
    };

    internal sealed record Mutant(string Kind, string Text, TextSpan Site, string Description);

    [Fact]
    public void The_compiler_and_the_value_checks_agree()
    {
        if (MotionCorpus.SkillFiles().Count == 0) return;
        var reached = new Dictionary<string, int>();
        var failures = new List<string>();
        int typed = 0;
        foreach (string path in MotionCorpus.SkillFiles())
        {
            string text = File.ReadAllText(path);
            foreach (var mutant in Mutants(path, text))
            {
                var (code, failed, message) = Compile(mutant.Text);
                var values = Typing(path, mutant.Text).Where(d => d.Code.StartsWith("MV", StringComparison.Ordinal)).ToList();
                if (code is { } c && Covered.TryGetValue(c, out var codes))
                {
                    reached[mutant.Kind] = reached.GetValueOrDefault(mutant.Kind) + 1;
                    if (!values.Any(d => codes.Contains(d.Code) && Overlaps(d.Span, mutant.Site)))
                        failures.Add($"{System.IO.Path.GetFileName(path)}: {mutant.Description}: compiler {c} '{message}', typing [{string.Join("; ", values)}]");
                }
                if (values.Count > 0)
                {
                    typed++;
                    if (!failed)
                        failures.Add($"{System.IO.Path.GetFileName(path)}: {mutant.Description}: typing [{string.Join("; ", values)}], the compiler accepts it");
                }
            }
        }
        output.WriteLine(string.Join(", ", reached.OrderBy(r => r.Key).Select(r => $"{r.Key} {r.Value}")) + $"; {typed} mutants with value errors");
        foreach (string kind in new[]
        {
            "not constant", "negative", "track swapped", "range swapped", "default outside", "divide by zero",
            "length shortened", "first transition", "transition removed", "motor removed", "motor duplicated",
            "target expression", "rest later", "override window", "override too long",
            "priority fraction", "dynamic weight", "additive absolute", "source cycle", "nested self", "nested cycle",
        })
            Assert.True(reached.GetValueOrDefault(kind) >= 3, $"'{kind}' reached the compiler's value codes only {reached.GetValueOrDefault(kind)} times");
        Assert.True(failures.Count == 0, $"{failures.Count} disagreements:\n" + string.Join("\n", failures.Take(40)));
    }

    /// <summary>
    /// Value-directed mutants, sampled per kind. Numbers in constant slots become <c>time</c> or
    /// negative; track and range bounds swap; a default moves outside its range; a number is divided
    /// by zero.
    /// </summary>
    internal static List<Mutant> Mutants(string path, string text)
    {
        var mutants = new List<Mutant>();
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set(path, parsed.Tree);
        var file = new ProjectSemantics(project)[path];
        var tree = parsed.Tree;
        var slots = new HashSet<int>
        {
            MotionKinds.Track, MotionKinds.LifecycleField, MotionKinds.SkillLength, MotionKinds.Transition, MotionKinds.PhaseHold,
            MotionKinds.OverrideDelay, MotionKinds.OverrideTransition, MotionKinds.Completion, MotionKinds.Timeout, MotionKinds.Bounds,
        };
        var inSlots = new List<int>();
        var inSkills = new List<int>();
        var tracks = new List<int>();
        var declarations = new List<int>();
        var sources = new List<int>();
        var lengths = new List<int>();
        var priorities = new List<int>();
        var outputs = new List<int>();
        var nesteds = new List<int>();
        for (int node = 0; node < tree.NodeCount; node++)
        {
            int kind = tree.Kind(node);
            if (kind == MotionKinds.Num)
            {
                int owner = file.ParentOf(node);
                if (owner >= 0 && slots.Contains(tree.Kind(owner))) inSlots.Add(node);
                if (MotionValues.Inside(file, node, MotionKinds.Skill)) inSkills.Add(node);
            }
            else if (kind == MotionKinds.Track) tracks.Add(node);
            else if (kind == MotionKinds.ValueDeclaration) declarations.Add(node);
            else if (kind == MotionKinds.Phases) sources.Add(node);
            else if (kind == MotionKinds.SkillLength) lengths.Add(node);
            else if (kind == MotionKinds.PriorityMode) priorities.Add(node);
            else if (kind == MotionKinds.Output) outputs.Add(node);
            else if (kind == MotionKinds.Nested) nesteds.Add(node);
        }

        string Show(TextSpan span) => text.Substring(span.Start, span.Length);
        foreach (int node in BindingOracle.Sample(inSlots, 6))
        {
            var span = tree.Span(node);
            mutants.Add(new Mutant("not constant", BindingOracle.Rename(text, span, "time"), new TextSpan(span.Start, 4), $"'{Show(span)}' → time"));
            if (file.Get(node, MotionModule.P_Expr_Constant) is float value && value > 0f)
                mutants.Add(new Mutant("negative", BindingOracle.Rename(text, span, "-" + Show(span)), new TextSpan(span.Start, span.Length + 1), $"'{Show(span)}' → negative"));
        }
        foreach (int node in BindingOracle.Sample(inSkills, 4))
        {
            var span = tree.Span(node);
            mutants.Add(new Mutant("divide by zero", text[..span.End] + " / 0" + text[span.End..], new TextSpan(span.Start, span.Length + 4), $"'{Show(span)}' / 0"));
        }
        foreach (int node in BindingOracle.Sample(tracks, 4))
        {
            var track = new TrackNodeSemantics(file, node);
            if (track.Start.Constant is float from && track.End.Constant is float to && from < to)
                mutants.Add(new Mutant("track swapped", Swap(text, track.Start.Span, track.End.Span), track.Span, $"track {Show(track.Start.Span)}..{Show(track.End.Span)} swapped"));
        }
        foreach (int node in BindingOracle.Sample(declarations, 4))
        {
            var declaration = new ValueDeclarationNodeSemantics(file, node);
            if (declaration.Limits is not { } range || range.Low.Constant is not float low || range.High.Constant is not float high) continue;
            if (low < high)
                mutants.Add(new Mutant("range swapped", Swap(text, range.Low.Span, range.High.Span), declaration.Span, $"range of '{declaration.Name}' swapped"));
            if (declaration.Default is { } value && value.Constant is float)
                mutants.Add(new Mutant("default outside", BindingOracle.Rename(text, value.Span, (high + 1f).ToString(CultureInfo.InvariantCulture)),
                    declaration.Span, $"default of '{declaration.Name}' → {high + 1f}"));
        }
        foreach (int node in BindingOracle.Sample(lengths, 4))
        {
            var value = new SkillLengthNode(tree, node).Value;
            mutants.Add(new Mutant("length shortened", BindingOracle.Rename(text, value.Span, "0.01"), value.Span, $"length {Show(value.Span)} → 0.01"));
        }
        foreach (int node in BindingOracle.Sample(sources, 4))
            PhaseMutants(text, tree, node, mutants, Show);
        foreach (int node in BindingOracle.Sample(priorities, 4))
            GraphModeMutants(text, tree, node, mutants, Show);
        foreach (int node in BindingOracle.Sample(outputs, 4))
            CycleMutants(text, tree, node, mutants);
        foreach (int node in BindingOracle.Sample(nesteds, 4))
            NestedCycleMutant(text, tree, node, mutants);
        return mutants;
    }

    static string Insert(string text, int at, string inserted) => text[..at] + inserted + text[at..];

    static int SkillOf(SyntaxTree tree, int node)
    {
        while (tree.Kind(node) != MotionKinds.Skill) node = tree.Parent(node);
        return node;
    }

    /// <summary>A priority made a fraction, a priority child made weighted by an observation, or made additive.</summary>
    static void GraphModeMutants(string text, SyntaxTree tree, int node, List<Mutant> mutants, Func<TextSpan, string> show)
    {
        var mode = new PriorityModeNode(tree, node);
        mutants.Add(new Mutant("priority fraction", BindingOracle.Rename(text, mode.Value.Span, "0.5"), mode.Value.Span, $"priority {show(mode.Value.Span)} → 0.5"));
        mutants.Add(new Mutant("dynamic weight", BindingOracle.Rename(text, mode.Span, "weighted weight time"), mode.Span, $"'{show(mode.Span)}' → weighted weight time"));
        mutants.Add(new Mutant("additive absolute", BindingOracle.Rename(text, mode.Span, "additive weight 1"), mode.Span, $"'{show(mode.Span)}' → additive weight 1"));
    }

    /// <summary>Before a skill's output: two blends that name each other, and a nested source naming its own skill.</summary>
    static void CycleMutants(string text, SyntaxTree tree, int output, List<Mutant> mutants)
    {
        int at = tree.Span(output).Start;
        string name = new SkillNode(tree, SkillOf(tree, output)).Name.ToString();
        const string cycle = "source cycle_a: blend {\n        cycle_b priority 1\n    }\n    source cycle_b: blend {\n        cycle_a priority 1\n    }\n    ";
        mutants.Add(new Mutant("source cycle", Insert(text, at, cycle), new TextSpan(at, cycle.Length), $"skill '{name}' gains cycle_a <-> cycle_b"));
        string self = $"source self_nest: nested {name} {{\n    }}\n    ";
        mutants.Add(new Mutant("nested self", Insert(text, at, self), new TextSpan(at, self.Length), $"skill '{name}' nests itself"));
    }

    /// <summary>The nested skill nests its nester back. The site spans both nested sources, whichever the cycle is reported at.</summary>
    static void NestedCycleMutant(string text, SyntaxTree tree, int node, List<Mutant> mutants)
    {
        var nested = new NestedNode(tree, node);
        string target = nested.SkillName.ToString();
        int outer = SkillOf(tree, node);
        string outerName = new SkillNode(tree, outer).Name.ToString();
        int inner = -1;
        for (int n = 0; n < tree.NodeCount && inner < 0; n++)
            if (tree.Kind(n) == MotionKinds.Skill && new SkillNode(tree, n).Name.ToString() == target) inner = n;
        if (inner < 0 || inner == outer) return;
        int output = -1;
        foreach (var item in new SkillNode(tree, inner).Items)
            if (item.Kind == MotionKinds.Output && output < 0) output = item.Index;
        if (output < 0) return;
        int at = tree.Span(output).Start;
        string back = $"source back_nest: nested {outerName} {{\n    }}\n    ";
        var span = nested.Span;
        int start = at <= span.Start ? span.Start + back.Length : span.Start;
        int low = Math.Min(at, start), high = Math.Max(at + back.Length, start + span.Length);
        mutants.Add(new Mutant("nested cycle", Insert(text, at, back), new TextSpan(low, high - low), $"skill '{target}' nests '{outerName}' back"));
    }

    static string Remove(string text, TextSpan span) => text[..span.Start] + text[span.End..];

    /// <summary>
    /// Structural mutants of one phase source: a transition on the first phase, one removed from a
    /// later phase, a motor removed from or repeated in a pose, a target made an expression or
    /// <c>rest</c>, and an override window pushed past its transition.
    /// </summary>
    static void PhaseMutants(string text, SyntaxTree tree, int source, List<Mutant> mutants, Func<TextSpan, string> show)
    {
        var phases = new PhasesNode(tree, source).Items;
        if (phases.Count == 0) return;
        var first = phases[0];
        if (MotionValues.Parts(tree, first.Index).Transition is null)
        {
            int at = text.IndexOf('{', first.Name.Span.End) + 1;
            mutants.Add(new Mutant("first transition", text[..at] + " transition 1s easing linear" + text[at..], first.Span, $"phase '{first.Name}' gains a transition"));
        }
        for (int i = 1; i < phases.Count; i++)
        {
            var phase = phases[i];
            var parts = MotionValues.Parts(tree, phase.Index);
            if (parts.Transition is { } transition)
                mutants.Add(new Mutant("transition removed", Remove(text, transition.Span), phase.Span, $"phase '{phase.Name}' loses its transition"));
            if (parts.Pose is { } pose && pose.Targets.Count >= 2)
            {
                var last = pose.Targets[pose.Targets.Count - 1];
                mutants.Add(new Mutant("motor removed", Remove(text, last.Span), phase.Span, $"phase '{phase.Name}' drops {show(last.Slot.Span)}"));
            }
            if (parts.Pose is { } later && later.Targets.Count > 0 && later.Targets[0].Value.Kind != MotionKinds.RestValue)
            {
                var value = later.Targets[0].Value;
                mutants.Add(new Mutant("rest later", BindingOracle.Rename(text, value.Span, "rest"), value.Span, $"phase '{phase.Name}' target {show(value.Span)} → rest"));
            }
            foreach (var @override in parts.Overrides)
            {
                int at = text.IndexOf('{', @override.Slot.Span.End) + 1;
                mutants.Add(new Mutant("override window", text[..at] + " delay 99s" + text[at..], @override.Span, $"override {show(@override.Slot.Span)} gains delay 99s"));
                foreach (var item in @override.Items)
                {
                    if (item.Kind != MotionKinds.OverrideTransition) continue;
                    var value = new OverrideTransitionNode(tree, item.Index).Value;
                    mutants.Add(new Mutant("override too long", BindingOracle.Rename(text, value.Span, "99s"), @override.Span, $"override {show(@override.Slot.Span)} transition → 99s"));
                }
            }
        }
        foreach (var phase in phases)
        {
            if (MotionValues.Parts(tree, phase.Index).Pose is not { } pose || pose.Targets.Count == 0) continue;
            var target = pose.Targets[pose.Targets.Count - 1];
            mutants.Add(new Mutant("motor duplicated", text[..target.Span.End] + " " + show(target.Span) + text[target.Span.End..],
                new TextSpan(target.Span.End + 1, target.Span.Length), $"phase '{phase.Name}' repeats {show(target.Slot.Span)}"));
            if (target.Value.Kind != MotionKinds.RestValue)
                mutants.Add(new Mutant("target expression", BindingOracle.Rename(text, target.Value.Span, show(target.Value.Span) + " + 0"),
                    target.Value.Span, $"phase '{phase.Name}' target {show(target.Value.Span)} + 0"));
        }
    }

    static string Swap(string text, TextSpan first, TextSpan second) =>
        text[..first.Start] + text.Substring(second.Start, second.Length) + text[first.End..second.Start]
        + text.Substring(first.Start, first.Length) + text[second.End..];

    internal static List<SemanticDiagnostic> Typing(string path, string text)
    {
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set(path, parsed.Tree);
        return new ProjectSemantics(project)[path].Diagnostics().ToList();
    }

    /// <returns>The compiler's first error code (null when none, or not a skill error), whether it failed, and its message.</returns>
    internal static (SkillDiagnosticCode? Code, bool Failed, string Message) Compile(string text)
    {
        try
        {
            MotionCompiler.Compile(new MotionParser(new MotionLexer(text).Tokenize()).ParseFile());
            return (null, false, "");
        }
        catch (SkillCompileException e)
        {
            return (e.Code, true, e.Message);
        }
        catch (Exception e)
        {
            return (null, true, e.Message);
        }
    }

    internal static bool Overlaps(TextSpan a, TextSpan b) => a.Start <= b.End && b.Start <= a.End;
}
