using System.Text.RegularExpressions;
using Gravity.MotionDSL.Compiler;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

/// <summary>PV checks against the hand pipeline on mutants of the corpus (issue 241, spec §6).</summary>
public class PolicyValueOracleTests(ITestOutputHelper output)
{
    /// <summary>The pipeline messages this plan's checks stand for; the others belong to later plans.</summary>
    internal static readonly Regex[] Covered =
    [
        new("^actuate (rate_limit|scale|smoothing) "), new("^actuate clamp "), new("^noise '.*' must be non-negative$"),
        new("^latency .* is not a valid range$"), new("^clock (hold|steps) must"), new("^clock mode "),
        new("has weight zero; delete the line"), new("^a track (weight|sharpness) "), new("^joint weight for "),
        new("^regulariser "), new("^joint_limit margin "), new("^a goal_track weight "), new("^goal_track halving "),
        new("^goal_hit weight must"), new("^start '.*' probability must"), new("^settle and joint_noise "),
        new("^reference start range "), new("^crumple (cut|shove) "), new("^termination '.*' threshold must be positive$"),
        new("^termination height is a fraction"), new("^success tracking error bar"), new("^success supine: "),
        new("^success: tolerance"), new("^perturb (push max|every) "), new("^randomize "), new("^train (worlds|timesteps|network) "),
        new("^observations clip "), new("schedule fraction must be in"), new("is a progress and must go"), new("^family_mix must go"),
        new("^algorithm '.*' is not supported"), new("^evaluate start '.*' is not a start kind"),
        new("^Expected an integer"), new("^Unknown number suffix "),
        new("^policy (is missing|needs at least one)"), new("^goal '.*' is missing '"), new("^v1 supports exactly one goal"),
        new("^actuate block needs"), new("^perturb (block needs a push line|takes exactly one push line)"), new("^command block needs"),
        new("^train is missing"), new("^evaluate is missing"), new("^scenarios needs at least one"),
        new("needs a command block and a step clock$"), new("is a gait term and needs a step clock$"), new("^a step clock has no reference"),
        new("^goal_track, goal_hit, gait terms and a command block need"), new("^start standing needs"), new("^termination '.*' needs `clock steps`"),
        new("^success tracking needs"), new("^randomize needs `clock steps`"), new("^a step clock needs a command block$"), new("^observation '.*' needs "),
        new("has no (goal_track|track) term$"), new("^one goal_hit term per goal$"), new("' does not take '"), new("^'.*' needs '"),
        new("gate requires 'height'"), new("needs a velocity channel among"), new("scores a family the command block draws"), new("^start crumple stands"),
        new("start probabilities sum to"), new("^families must include velocity$"), new("^family probabilities sum to"), new("^velocity is the remainder"),
        new("^rewards clip "), new("schedules need a command block$"), new("^family '.*' (probability must be positive|fade-in start)"),
        new("^box .* must satisfy"), new("^resample every "),
        new("^scenarios need `seeds N`$"), new("^use either `episodes`"), new("^`seeds` is for scenario evaluation"),
        new("^evaluate episodes must be positive$"), new("^scenarios require (start standing|a command block)$"),
        new("^report '.*' (is not a scenario report|needs scenarios)"), new("is not a start case of goal"),
        new("^evaluate asks for perturbation"), new("^evaluate randomize needs"), new("^compose needs "),
        new("^compose '.*': (pose '.*' is owned twice|stand_to_walk, hand_over and settle_cap|retries must not|command '.*' sets '.*' to a non-finite)"),
    ];

    internal sealed record Mutant(string Kind, string Text, TextSpan Site, string Description);

    static IReadOnlyList<string> Corpus() => MotionCorpus.PolicyFiles().Concat(MotionCorpus.ComposeFiles()).ToList();

    [Fact]
    public void A_clean_corpus_file_has_no_value_errors()
    {
        foreach (string path in Corpus())
        {
            using var typed = new PolicyValueChecksTests.Typed(path, File.ReadAllText(path));
            Assert.Empty(typed.Semantics.Diagnostics());
        }
    }

    [Fact]
    public void The_pipeline_and_the_value_checks_agree()
    {
        if (Corpus().Count == 0) return;
        var reached = new Dictionary<string, int>();
        var failures = new List<string>();
        int typed = 0;
        foreach (string path in Corpus())
        {
            string text = File.ReadAllText(path);
            foreach (var mutant in Mutants(path, text))
            {
                var (failed, message) = Pipeline(path, mutant.Text);
                List<Nitrogen.Semantics.SemanticDiagnostic> values;
                using (var file = new PolicyValueChecksTests.Typed(path, mutant.Text))
                    values = file.Semantics.Diagnostics().Where(d => d.Code.StartsWith("PV", StringComparison.Ordinal)).ToList();
                if (failed && Covered.Any(r => r.IsMatch(message)))
                {
                    reached[mutant.Kind] = reached.GetValueOrDefault(mutant.Kind) + 1;
                    if (!values.Any(d => d.Message == message && Overlaps(d.Span, mutant.Site)))
                        failures.Add($"{Path.GetFileName(path)}: {mutant.Description}: pipeline '{message}', editor [{string.Join("; ", values)}]");
                }
                if (values.Count > 0)
                {
                    typed++;
                    if (!failed) failures.Add($"{Path.GetFileName(path)}: {mutant.Description}: editor [{string.Join("; ", values)}], the pipeline accepts it");
                }
            }
        }
        output.WriteLine(string.Join(", ", reached.OrderBy(r => r.Key).Select(r => $"{r.Key} {r.Value}")) + $"; {typed} mutants with value errors");
        foreach (string kind in new[]
        {
            "sign flipped", "zero", "above one", "fractional", "range swapped", "bad suffix",
            "block removed", "tracks removed", "clock swapped", "probability nudged", "rewards clip lowered",
            "schedule added", "standing start", "success tracking", "tilt termination",
            "perturb on", "evaluate randomize", "report scenario", "evaluate start changed",
        })
            Assert.True(reached.GetValueOrDefault(kind) >= 3, $"'{kind}' reached a covered pipeline error only {reached.GetValueOrDefault(kind)} times");
        Assert.True(failures.Count == 0, $"{failures.Count} disagreements:\n" + string.Join("\n", failures.Take(40)));
    }

    /// <summary>Numbers flipped in sign, zeroed, pushed above one or made fractional; ranges swapped; counts given a bad suffix.</summary>
    internal static List<Mutant> Mutants(string path, string text)
    {
        var mutants = new List<Mutant>();
        using var parsed = NitrogenPolicyParser.Language.Parse(text,
            path.EndsWith(".compose", StringComparison.Ordinal) ? PolicyModule.ComposeDocument : PolicyModule.PolicyDocument);
        var tree = parsed.Tree;
        var nums = new List<int>();
        var counts = new List<int>();
        var ranges = new List<(TextSpan Low, TextSpan High)>();
        for (int node = 0; node < tree.NodeCount; node++)
        {
            int kind = tree.Kind(node);
            if (kind == PolicyKinds.Num) nums.Add(node);
            else if (kind == PolicyKinds.Timesteps) counts.Add(new TimestepsNode(tree, node).Value.Index);
            else if (kind == PolicyKinds.ScheduleItem && new ScheduleItemNode(tree, node).Over is { HasValue: true } over) counts.Add(tree.Child(over.Value.Index, 1));
            else if (kind == PolicyKinds.Latency) ranges.Add((new LatencyNode(tree, node).Low.Span, new LatencyNode(tree, node).High.Span));
            else if (kind == PolicyKinds.CrumpleShove) ranges.Add((new CrumpleShoveNode(tree, node).Low.Span, new CrumpleShoveNode(tree, node).High.Span));
            else if (kind == PolicyKinds.WindowRange) ranges.Add((new WindowRangeNode(tree, node).Low.Span, new WindowRangeNode(tree, node).High.Span));
            else if (kind == PolicyKinds.Push) ranges.Add((new PushNode(tree, node).Low.Span, new PushNode(tree, node).High.Span));
            else if (kind == PolicyKinds.RandomizeRange) ranges.Add((new RandomizeRangeNode(tree, node).Low.Span, new RandomizeRangeNode(tree, node).High.Span));
        }

        string Show(TextSpan span) => text.Substring(span.Start, span.Length);
        foreach (int node in BindingOracle.Sample(nums, 8))
        {
            var span = tree.Span(node);
            string shown = Show(span);
            string flipped = shown.StartsWith('-') ? shown.TrimStart('-').TrimStart() : "-" + shown;
            mutants.Add(new Mutant("sign flipped", BindingOracle.Rename(text, span, flipped), span, $"'{shown}' → '{flipped}'"));
            mutants.Add(new Mutant("zero", BindingOracle.Rename(text, span, "0"), span, $"'{shown}' → 0"));
            mutants.Add(new Mutant("above one", BindingOracle.Rename(text, span, "1.5"), span, $"'{shown}' → 1.5"));
            if (shown.All(char.IsAsciiDigit))
                mutants.Add(new Mutant("fractional", BindingOracle.Rename(text, span, shown + ".5"), span, $"'{shown}' → {shown}.5"));
        }
        foreach (var (low, high) in BindingOracle.Sample(ranges, 4))
        {
            string swapped = text[..low.Start] + Show(high) + text[low.End..high.Start] + Show(low) + text[high.End..];
            mutants.Add(new Mutant("range swapped", swapped, new TextSpan(low.Start, high.End - low.Start), $"'{Show(low)}..{Show(high)}' swapped"));
        }
        foreach (int token in BindingOracle.Sample(counts, 4))
        {
            var span = tree.Span(token);
            string shown = Show(span);
            string digits = shown.TrimEnd('k', 'M');
            mutants.Add(new Mutant("bad suffix", BindingOracle.Rename(text, span, digits + "G"), span, $"'{shown}' → {digits}G"));
        }
        StructuralMutants(text, tree, mutants);
        return mutants;
    }

    static string Insert(string text, int at, string inserted) => text[..at] + inserted + text[at..];

    /// <summary>
    /// Plan 2's structural mutants: a block or setting removed, a goal's tracking terms removed, the clock
    /// swapped, a start probability nudged, the reward clip lowered, a command schedule added, a start
    /// case made standing, success made tracking, a tilt termination added, a command channel dropped.
    /// Removals are sited at their owner, which is where an absence is reported; the rest at the
    /// whole document, since their errors land anywhere in it.
    /// </summary>
    static void StructuralMutants(string text, Nitrogen.SyntaxTree tree, List<Mutant> mutants)
    {
        var document = new TextSpan(0, text.Length);
        string Show(TextSpan span) => text.Substring(span.Start, span.Length);
        var removals = new List<(TextSpan Item, TextSpan Owner)>();
        var rewards = new List<(List<TextSpan> Tracks, TextSpan Goal)>();
        var clocks = new List<int>();
        var probabilities = new List<int>();
        var clips = new List<int>();
        var trains = new List<int>();
        var evaluates = new List<int>();
        var cases = new List<int>();
        var successes = new List<int>();
        var terminates = new List<int>();
        var channels = new List<int>();
        string? reference = null;
        for (int node = 0; node < tree.NodeCount; node++)
        {
            int kind = tree.Kind(node);
            if (kind == PolicyKinds.Policy || kind == PolicyKinds.Compose || kind == PolicyKinds.Goal || kind == PolicyKinds.Train || kind == PolicyKinds.Evaluate || kind == PolicyKinds.Command)
            {
                if (kind == PolicyKinds.Train) trains.Add(node);
                if (kind == PolicyKinds.Evaluate) evaluates.Add(node);
                int list = kind == PolicyKinds.Policy || kind == PolicyKinds.Compose || kind == PolicyKinds.Goal ? tree.Child(node, 3) : tree.Child(node, 2);
                for (int i = 0; i < tree.ChildCount(list); i++)
                {
                    int item = tree.Child(list, i);
                    if (tree.Kind(item) != PolicyKinds.Reference) removals.Add((tree.Span(item), tree.Span(node)));
                }
            }
            else if (kind == PolicyKinds.Reward)
            {
                int goal = tree.Parent(node);
                while (tree.Kind(goal) != PolicyKinds.Goal) goal = tree.Parent(goal);
                // Every goal in the corpus has several track terms: remove them all, so the goal loses its tracking.
                bool step = false;
                foreach (var item in new GoalNode(tree, goal).Items) if (item.Kind == PolicyKinds.StepsClock) step = true;
                var tracks = new List<TextSpan>();
                foreach (var term in new RewardNode(tree, node).Terms)
                    if (term.Kind == (step ? PolicyKinds.GoalTrackTerm : PolicyKinds.TrackTerm)) tracks.Add(term.Span);
                if (tracks.Count > 0) rewards.Add((tracks, tree.Span(goal)));
            }
            else if (kind == PolicyKinds.StepsClock || kind == PolicyKinds.RefClock) clocks.Add(node);
            else if (kind == PolicyKinds.SpawnCase || kind == PolicyKinds.CrumpleCase || kind == PolicyKinds.StandingCase || kind == PolicyKinds.ReferenceCase)
            {
                probabilities.Add(tree.Child(node, 1));
                if (kind != PolicyKinds.StandingCase) cases.Add(node);
            }
            else if (kind == PolicyKinds.Normalize) clips.Add(new NormalizeNode(tree, node).Rewards.Index);
            else if (kind == PolicyKinds.Success) successes.Add(node);
            else if (kind == PolicyKinds.Terminate) terminates.Add(node);
            else if (kind == PolicyKinds.Channels && new ChannelsNode(tree, node).Names.Count >= 2) channels.Add(node);
            else if (kind == PolicyKinds.Reference) reference ??= new ReferenceNode(tree, node).Name.ToString();
        }

        foreach (var (item, owner) in BindingOracle.Sample(removals, 6))
            mutants.Add(new Mutant("block removed", text[..item.Start] + text[item.End..], owner, $"'{Show(item).Split('\n')[0]}' removed"));
        foreach (var (tracks, goal) in rewards)
        {
            string removed = text;
            for (int i = tracks.Count - 1; i >= 0; i--) removed = removed[..tracks[i].Start] + removed[tracks[i].End..];
            mutants.Add(new Mutant("tracks removed", removed, goal, $"{tracks.Count} tracking terms removed"));
        }
        foreach (int clock in clocks)
        {
            if (tree.Kind(clock) == PolicyKinds.RefClock)
                mutants.Add(new Mutant("clock swapped", BindingOracle.Rename(text, tree.Span(clock), "clock steps 100"), document, "clock → steps"));
            else if (reference is not null)
                mutants.Add(new Mutant("clock swapped", BindingOracle.Rename(text, tree.Span(clock), $"clock {reference} time_driven"), document, "clock → reference"));
        }
        foreach (int num in BindingOracle.Sample(probabilities, 3))
        {
            var span = tree.Span(num);
            if (!float.TryParse(Show(span), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p)) continue;
            string nudged = (p + 0.25f).ToString(System.Globalization.CultureInfo.InvariantCulture);
            mutants.Add(new Mutant("probability nudged", BindingOracle.Rename(text, span, nudged), document, $"'{Show(span)}' → {nudged}"));
        }
        foreach (int clip in clips)
            mutants.Add(new Mutant("rewards clip lowered", BindingOracle.Rename(text, tree.Span(clip), "0.01"), document, "rewards clip → 0.01"));
        foreach (int train in trains)
        {
            int close = tree.Span(train).End - 1;
            mutants.Add(new Mutant("schedule added", Insert(text, close, "command_box 0 to 1 from 0\n    "), document, "command_box schedule added"));
        }
        foreach (int item in BindingOracle.Sample(cases, 3))
        {
            var span = tree.Span(item);
            string standing = "standing " + Show(tree.Span(tree.Child(item, 1)));
            mutants.Add(new Mutant("standing start", BindingOracle.Rename(text, span, standing), document, $"'{Show(span).Split('\n')[0]}' → {standing}"));
        }
        foreach (int success in successes)
            mutants.Add(new Mutant("success tracking", BindingOracle.Rename(text, tree.Span(success), "success tracking { error < 0.1 } at end"), document, "success → tracking"));
        foreach (int terminate in terminates)
        {
            int at = text.IndexOf('{', tree.Span(terminate).Start) + 1;
            mutants.Add(new Mutant("tilt termination", Insert(text, at, " tilt > 1"), document, "tilt termination added"));
        }
        foreach (int list in channels)
        {
            var names = new ChannelsNode(tree, list).Names;
            var last = names[names.Count - 1].Span;
            mutants.Add(new Mutant("channel dropped", text[..last.Start] + text[last.End..], document, $"channel '{Show(last)}' dropped"));
        }
        foreach (int evaluate in evaluates)
        {
            var span = tree.Span(evaluate);
            int close = span.End - 1;
            mutants.Add(new Mutant("perturb on", Insert(text, close, "perturb on\n    "), document, "evaluate perturb on"));
            mutants.Add(new Mutant("evaluate randomize", Insert(text, close, "randomize { pose_jitter 0.1 }\n    "), document, "evaluate randomize added"));
            foreach (var item in new EvaluateNode(tree, evaluate).Items)
            {
                if (item.Kind == PolicyKinds.Report)
                    mutants.Add(new Mutant("report scenario", BindingOracle.Rename(text, item.Span, "report falls"), document, "report → falls"));
                if (item.Kind == PolicyKinds.EvaluateStart)
                {
                    var case_ = new EvaluateStartNode(tree, item.Index).Case;
                    string other = case_.ToString() == "standing" ? "reference" : "standing";
                    mutants.Add(new Mutant("evaluate start changed", BindingOracle.Rename(text, case_.Span, other), document, $"evaluate start → {other}"));
                }
            }
        }
    }

    /// <returns>Whether the hand pipeline (MotionLexer, PolicyParser, then PolicyCompiler or ComposeCompiler) fails, and its message without " at line:col".</returns>
    internal static (bool Failed, string Message) Pipeline(string path, string text)
    {
        try
        {
            var parser = new PolicyParser(new MotionLexer(text).Tokenize());
            if (path.EndsWith(".compose", StringComparison.Ordinal)) ComposeCompiler.Compile(parser.ParseCompose());
            else PolicyCompiler.Compile(parser.ParsePolicy());
            return (false, "");
        }
        catch (Exception e)
        {
            return (true, Regex.Replace(e.Message, @" at \d+:\d+$", ""));
        }
    }

    internal static bool Overlaps(TextSpan a, TextSpan b) => a.Start <= b.End && b.Start <= a.End;
}
