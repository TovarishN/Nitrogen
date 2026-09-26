using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.Semantics;

namespace Nitrogen.MotionDsl;

/// <summary>
/// Whole-block and cross-block rules (issue 241, Plan 2): what PolicyAstMapper requires of a block
/// and what PolicyCompiler checks across blocks. A goal is read as the mapper maps it: a later
/// block or setting replaces an earlier one, goals and references accumulate, families merge by
/// name. Each …Problem method returns the pipeline's message, or null.
/// </summary>
public static partial class PolicyValues
{
    /// <summary>What the compiler knows about one goal. Node indices are -1 when absent.</summary>
    public sealed record GoalFacts(string Name, int Clock, bool StepClock, int Command, IReadOnlyList<string>? Channels,
        IReadOnlyList<(string Name, float? Probability)> Families, int Reward, int Start, IReadOnlyCollection<string> References)
    {
        public bool HasClock => Clock >= 0;

        public bool RefGoal => HasClock && !StepClock;

        public bool StepGoal => HasClock && StepClock;
    }

    static readonly Dictionary<string, string[]> GaitParams = new(StringComparer.Ordinal)
    {
        ["alive"] = [], ["stride"] = ["sigma", "gate_sigma_sq"], ["foot_clearance"] = ["target"],
        ["knee_scaffold"] = ["full_flex", "ankle_full_flex"], ["base_height"] = [], ["orientation"] = [],
        ["torso_pose"] = [], ["foot_slip"] = [], ["forbidden_contact"] = [], ["vertical_vel"] = [],
        ["roll_pitch_rate"] = [], ["arm_pose"] = [], ["step_cost"] = [], ["stand_still"] = [],
        ["leg_pose"] = ["free"], ["leg_parallel"] = ["free"], ["com_balance"] = ["free"], ["termination"] = [],
    };

    static float? Amount(FileSemantics s, NumNode num) => s.Get(num.Index, PolicyModule.P_Num_Amount);

    static float? Amount(FileSemantics s, int num) => s.Get(num, PolicyModule.P_Num_Amount);

    static int Enclosing(SyntaxTree tree, int node, int kind)
    {
        for (int p = tree.Parent(node); p >= 0; p = tree.Parent(p))
            if (tree.Kind(p) == kind) return p;
        return -1;
    }

    static int ClockGroup(int kind) => kind == PolicyKinds.RefClock ? PolicyKinds.StepsClock : kind;

    /// <summary>Whether no later sibling in the node's list is of its kind: the mapper keeps the last.</summary>
    public static bool Live(FileSemantics s, int node)
    {
        var tree = s.Tree;
        int list = tree.Parent(node), kind = ClockGroup(tree.Kind(node));
        bool after = false;
        for (int i = 0; i < tree.ChildCount(list); i++)
        {
            int child = tree.Child(list, i);
            if (child == node) after = true;
            else if (after && ClockGroup(tree.Kind(child)) == kind) return false;
        }
        return true;
    }

    public static GoalFacts Goal(FileSemantics s, int goal)
    {
        var tree = s.Tree;
        var node = new GoalNode(tree, goal);
        int clock = -1, command = -1, reward = -1, start = -1;
        foreach (var item in node.Items)
        {
            if (item.Kind == PolicyKinds.StepsClock || item.Kind == PolicyKinds.RefClock) clock = item.Index;
            else if (item.Kind == PolicyKinds.Command) command = item.Index;
            else if (item.Kind == PolicyKinds.Reward) reward = item.Index;
            else if (item.Kind == PolicyKinds.Start) start = item.Index;
        }
        bool step = clock >= 0 && (tree.Kind(clock) == PolicyKinds.StepsClock || new RefClockNode(tree, clock).Mode.ToString() == "steps");
        List<string>? channels = null;
        var families = new List<(string Name, float? Probability)>();
        if (command >= 0)
            foreach (var item in new CommandNode(tree, command).Items)
            {
                if (item.Kind == PolicyKinds.Channels)
                {
                    channels = new List<string>();
                    foreach (var channel in new ChannelsNode(tree, item.Index).Names) channels.Add(channel.Name.ToString());
                }
                else if (item.Kind == PolicyKinds.Families)
                    foreach (var family in new FamiliesNode(tree, item.Index).Items)
                    {
                        string name = family.Name.ToString();
                        int at = families.FindIndex(f => f.Name == name);
                        if (at >= 0) families[at] = (name, Amount(s, family.Probability));
                        else families.Add((name, Amount(s, family.Probability)));
                    }
            }
        return new GoalFacts(node.Name.ToString(), clock, step, command, channels, families, reward, start,
            References(tree, Enclosing(tree, goal, PolicyKinds.Policy)));
    }

    /// <summary>The goal a node lies in, or null.</summary>
    public static GoalFacts? GoalOf(FileSemantics s, int node)
    {
        int goal = Enclosing(s.Tree, node, PolicyKinds.Goal);
        return goal < 0 ? null : Goal(s, goal);
    }

    /// <summary>The policy's first goal, which PolicyCompiler compiles, or null.</summary>
    public static GoalFacts? FirstGoal(FileSemantics s, int node)
    {
        int policy = s.Tree.Kind(node) == PolicyKinds.Policy ? node : Enclosing(s.Tree, node, PolicyKinds.Policy);
        if (policy < 0) return null;
        foreach (var item in new PolicyNode(s.Tree, policy).Items)
            if (item.Kind == PolicyKinds.Goal) return Goal(s, item.Index);
        return null;
    }

    static HashSet<string> References(SyntaxTree tree, int policy)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (policy >= 0)
            foreach (var item in new PolicyNode(tree, policy).Items)
                if (item.Kind == PolicyKinds.Reference) names.Add(new ReferenceNode(tree, item.Index).Name.ToString());
        return names;
    }

    static List<SyntaxNode> Terms(SyntaxTree tree, int reward)
    {
        var terms = new List<SyntaxNode>();
        if (reward >= 0) foreach (var term in new RewardNode(tree, reward).Terms) terms.Add(term);
        return terms;
    }

    /// <summary>A reward term's kind as the compiler names it: its keyword, or for simple and gait terms their word.</summary>
    static string TermKind(SyntaxTree tree, SyntaxNode term) =>
        term.Kind == PolicyKinds.TrackTerm ? "track"
        : term.Kind == PolicyKinds.JointLimitTerm ? "joint_limit"
        : term.Kind == PolicyKinds.GoalTrackTerm ? "goal_track"
        : tree.GetText(tree.Child(term.Index, 0)).ToString();

    static float? TermWeight(FileSemantics s, SyntaxNode term)
    {
        var tree = s.Tree;
        if (term.Kind == PolicyKinds.TrackTerm) return Amount(s, new TrackTermNode(tree, term.Index).Weight);
        if (term.Kind == PolicyKinds.JointLimitTerm) return Amount(s, new JointLimitTermNode(tree, term.Index).Weight);
        if (term.Kind == PolicyKinds.GoalTrackTerm) return Amount(s, new GoalTrackTermNode(tree, term.Index).Weight);
        if (term.Kind == PolicyKinds.SimpleTerm) return Amount(s, new SimpleTermNode(tree, term.Index).Weight);
        return Amount(s, new GaitTermNode(tree, term.Index).Weight);
    }

    // ---- PV0001: structure ----

    public static bool PolicyHas(FileSemantics s, int policy, string what)
    {
        var tree = s.Tree;
        foreach (var item in new PolicyNode(tree, policy).Items)
        {
            if (what is "rig" or "scene")
            {
                if (item.Kind == PolicyKinds.Requires && tree.GetText(new RequiresNode(tree, item.Index).Target.Index).ToString() == what) return true;
            }
            else if (item.Kind == (what switch { "observe" => PolicyKinds.Observe, "goal" => PolicyKinds.Goal, "train" => PolicyKinds.Train, _ => PolicyKinds.Evaluate }))
                return true;
        }
        return false;
    }

    public static bool GoalHas(FileSemantics s, int goal, string what)
    {
        foreach (var item in new GoalNode(s.Tree, goal).Items)
        {
            bool match = what switch
            {
                "clock" => item.Kind == PolicyKinds.StepsClock || item.Kind == PolicyKinds.RefClock,
                "reward" => item.Kind == PolicyKinds.Reward,
                "start" => item.Kind == PolicyKinds.Start,
                "terminate" => item.Kind == PolicyKinds.Terminate,
                _ => item.Kind == PolicyKinds.Success,
            };
            if (match) return true;
        }
        return false;
    }

    /// <summary>PolicyCompiler compiles one goal: the second one is where it reports the rest.</summary>
    public static string? SecondGoalProblem(FileSemantics s, int goal)
    {
        int policy = Enclosing(s.Tree, goal, PolicyKinds.Policy);
        var goals = new List<int>();
        foreach (var item in new PolicyNode(s.Tree, policy).Items)
            if (item.Kind == PolicyKinds.Goal) goals.Add(item.Index);
        return goals.Count > 1 && goals[1] == goal
            ? $"v1 supports exactly one goal per policy; '{new PolicyNode(s.Tree, policy).Name}' has {goals.Count}"
            : null;
    }

    public static bool FirstPush(FileSemantics s, int push)
    {
        var pushes = new PerturbNode(s.Tree, Enclosing(s.Tree, push, PolicyKinds.Perturb)).Pushes;
        return pushes.Count == 0 || pushes[0].Index == push;
    }

    public static bool CommandHas(FileSemantics s, int command, string what)
    {
        var tree = s.Tree;
        int resample = -1, standing = -1;
        bool channels = false, families = false;
        foreach (var item in new CommandNode(tree, command).Items)
        {
            if (item.Kind == PolicyKinds.Channels) channels = true;
            else if (item.Kind == PolicyKinds.Families && new FamiliesNode(tree, item.Index).Items.Count > 0) families = true;
            else if (item.Kind == PolicyKinds.Resample) resample = item.Index;
            else if (item.Kind == PolicyKinds.StandingHeight) standing = item.Index;
        }
        return what switch
        {
            "channels" => channels,
            "families" => families,
            "resample" => resample >= 0 && Amount(s, new ResampleNode(tree, resample).High) is float high && high > 0f,
            _ => standing >= 0 && Amount(s, new StandingHeightNode(tree, standing).Value) is float height && height > 0f,
        };
    }

    public static string? TrainMissing(FileSemantics s, int train)
    {
        var tree = s.Tree;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in new TrainNode(tree, train).Items)
        {
            if (item.Kind == PolicyKinds.Algorithm) seen.Add("algorithm");
            else if (item.Kind == PolicyKinds.TrainCount) seen.Add(tree.GetText(new TrainCountNode(tree, item.Index).Setting.Index).ToString());
            else if (item.Kind == PolicyKinds.Timesteps) seen.Add("timesteps");
            else if (item.Kind == PolicyKinds.Network) seen.Add("network");
            else if (item.Kind == PolicyKinds.LearningRate) seen.Add("learning_rate");
            else if (item.Kind == PolicyKinds.ScheduleItem) seen.Add(tree.GetText(new ScheduleItemNode(tree, item.Index).Setting.Index).ToString());
            else if (item.Kind == PolicyKinds.Normalize) { seen.Add("normalize observations clip"); seen.Add("normalize rewards clip"); }
        }
        foreach (string what in new[] { "algorithm", "worlds", "seed", "timesteps", "network", "learning_rate", "entropy", "std_cap",
                     "normalize observations clip", "normalize rewards clip" })
            if (!seen.Contains(what)) return $"train is missing '{what}'";
        return null;
    }

    public static string? EvaluateMissing(FileSemantics s, int evaluate)
    {
        var tree = s.Tree;
        bool episodes = false, start = false, report = false;
        int seeds = 0;
        foreach (var item in new EvaluateNode(tree, evaluate).Items)
        {
            if (item.Kind == PolicyKinds.EvaluateCount)
            {
                var count = new EvaluateCountNode(tree, item.Index);
                if (tree.GetText(count.Setting.Index).ToString() == "episodes") episodes = true;
                else seeds = Whole(Amount(s, count.Value));
            }
            else if (item.Kind == PolicyKinds.EvaluateStart) start = true;
            else if (item.Kind == PolicyKinds.Report) report = true;
        }
        if (!episodes && seeds == 0) return "evaluate is missing 'episodes (or seeds)'";
        if (!start) return "evaluate is missing 'start'";
        return report ? null : "evaluate is missing 'report'";
    }

    /// <summary>PolicyParser checks the running total, so an empty list after a full one passes.</summary>
    public static bool ScenariosNamed(FileSemantics s, int scenarios)
    {
        var tree = s.Tree;
        foreach (var item in new EvaluateNode(tree, Enclosing(tree, scenarios, PolicyKinds.Evaluate)).Items)
        {
            if (item.Kind != PolicyKinds.Scenarios) continue;
            if (new ScenariosNode(tree, item.Index).Names.Count > 0) return true;
            if (item.Index == scenarios) return false;
        }
        return true;
    }

    // ---- PV0005: clock mode ----

    public static bool RefGoal(FileSemantics s, int node) => GoalOf(s, node)?.RefGoal ?? false;

    public static bool StepGoal(FileSemantics s, int node) => GoalOf(s, node)?.StepGoal ?? false;

    public static string? StepClockProblem(FileSemantics s, int clock) =>
        Live(s, clock) && GoalOf(s, clock) is { StepClock: true, Command: < 0 } ? "a step clock needs a command block" : null;

    public static string? CommandClockProblem(FileSemantics s, int command) =>
        Live(s, command) && RefGoal(s, command) ? "goal_track, goal_hit, gait terms and a command block need `clock steps`" : null;

    public static string? ObservationClockProblem(FileSemantics s, int node, string block)
    {
        if (FirstGoal(s, node) is not { HasClock: true } goal) return null;
        if (block is "clock" or "reference_delta" && goal.StepClock)
            return $"observation '{(block == "clock" ? "clock" : "referencedelta")}' needs a reference clock; this goal uses `clock steps`";
        if (block == "command" && !goal.StepClock)
            return "observation 'command' needs a command block and `clock steps`";
        return null;
    }

    // ---- PV0006: reward structure ----

    public static string? TermPresenceProblem(FileSemantics s, int goal)
    {
        var facts = Goal(s, goal);
        if (!facts.HasClock || facts.Reward < 0) return null;
        var kinds = Terms(s.Tree, facts.Reward).Select(t => t.Kind).ToList();
        if (facts.StepClock) return kinds.Contains(PolicyKinds.GoalTrackTerm) ? null : $"goal '{facts.Name}' has no goal_track term";
        return kinds.Contains(PolicyKinds.TrackTerm) ? null : $"goal '{facts.Name}' has no track term";
    }

    public static bool FirstGoalHit(FileSemantics s, int term)
    {
        var tree = s.Tree;
        foreach (var other in Terms(tree, Enclosing(tree, term, PolicyKinds.Reward)))
        {
            if (other.Index == term) return true;
            if (other.Kind == PolicyKinds.SimpleTerm && TermKind(tree, other) == "goal_hit") return false;
        }
        return true;
    }

    public static string? GaitParamProblem(FileSemantics s, int term)
    {
        var tree = s.Tree;
        var gait = new GaitTermNode(tree, term);
        string kind = tree.GetText(gait.Term.Index).ToString();
        if (!GaitParams.TryGetValue(kind, out var allowed)) return null;
        var keys = new List<string>();
        foreach (var parameter in gait.Params)
        {
            string key = tree.GetText(parameter.Name.Index).ToString();
            if (!keys.Contains(key)) keys.Add(key);
        }
        foreach (string key in keys)
            if (!allowed.Contains(key))
                return $"'{kind}' does not take '{key}' (accepts: {(allowed.Length == 0 ? "no parameters" : string.Join(", ", allowed))})";
        foreach (string key in allowed)
            if (!keys.Contains(key) && key != "ankle_full_flex") return $"'{kind}' needs '{key}'";
        return null;
    }

    /// <summary>A term's <c>when</c> word, or null.</summary>
    static string? GateWord(SyntaxTree tree, int term)
    {
        for (int i = 0; i < tree.ChildCount(term); i++)
        {
            int child = tree.Child(term, i);
            if (tree.Kind(child) == PolicyKinds.Gate) return tree.GetText(new GateNode(tree, child).Name.Index).ToString();
        }
        return null;
    }

    /// <summary>Gates: height_commanded on gait terms and regularisers; moving and stand_still on gait terms.</summary>
    public static string? GateProblem(FileSemantics s, int term, string kind, bool gait)
    {
        string? gate = GateWord(s.Tree, term);
        var channels = GoalOf(s, term) is { Command: >= 0 } goal ? goal.Channels : null;
        if (gate == "height_commanded" && (channels is null || !channels.Contains("height")))
            return "a `when height_commanded` gate requires 'height' among the command channels";
        if (gait && (gate == "moving" || kind == "stand_still") && (channels is null || !channels.Any(c => c.StartsWith("v_", StringComparison.Ordinal) || c == "yaw_rate")))
            return $"'{kind}' needs a velocity channel among the command channels";
        return null;
    }

    public static string? GoalTrackFamilyProblem(FileSemantics s, int term)
    {
        if (GoalOf(s, term) is not { StepGoal: true } goal) return null;
        string family = new GoalTrackTermNode(s.Tree, term).Family.ToString();
        if (goal.Command >= 0 && goal.Families.Count == 0) return null; // the mapper reports the empty command first
        bool drawn = goal.Command >= 0 && goal.Families.Any(f => f.Name == family && f.Probability > 0f);
        return drawn ? null : $"goal_track {family} scores a family the command block draws with probability 0";
    }

    public static bool HasReferences(FileSemantics s, int node) =>
        References(s.Tree, Enclosing(s.Tree, node, PolicyKinds.Policy)).Count > 0;

    // ---- PV0007: sums and totals ----

    public static string? StartSumProblem(FileSemantics s, int start)
    {
        var tree = s.Tree;
        if (!Live(s, start)) return null;
        float sum = 0f;
        foreach (var item in new StartNode(tree, start).Cases)
        {
            if (Amount(s, tree.Child(item.Index, 1)) is not float p) return null; // every case's probability is child 1
            sum += p;
        }
        return MathF.Abs(sum - 1f) > 0.001f
            ? $"goal '{new GoalNode(tree, Enclosing(tree, start, PolicyKinds.Goal)).Name}': start probabilities sum to {F(sum)}, must be 1.0"
            : null;
    }

    public static string? FamiliesProblem(FileSemantics s, int families)
    {
        if (!Live(s, families) || GoalOf(s, families) is not { } goal) return null;
        if (!goal.Families.Any(f => f.Name == "velocity")) return "families must include velocity";
        float sum = 0f;
        foreach (var (_, p) in goal.Families)
        {
            if (p is not float probability) return null;
            sum += probability;
        }
        return MathF.Abs(sum - 1f) > 0.01f ? $"family probabilities sum to {F(sum)}, must be 1.0" : null;
    }

    public static string? BoxProblem(FileSemantics s, int entry)
    {
        var tree = s.Tree;
        var box = new BoxEntryNode(tree, entry);
        float? lo = Amount(s, box.StartLow), hi = Amount(s, box.StartHigh), capLo = lo, capHi = hi;
        if (box.Cap.HasValue)
        {
            capLo = Amount(s, tree.Child(box.Cap.Value.Index, 1));
            capHi = Amount(s, tree.Child(box.Cap.Value.Index, 3));
        }
        return lo >= hi || capLo >= capHi ? $"box {box.Channel} must satisfy lo < hi at both ends" : null;
    }

    public static string? RewardClipProblem(FileSemantics s, int normalize)
    {
        if (FirstGoal(s, normalize) is not { HasClock: true, Reward: >= 0 } goal) return null;
        if (Amount(s, new NormalizeNode(s.Tree, normalize).Rewards) is not float clip) return null;
        var terms = Terms(s.Tree, goal.Reward);
        var tracks = terms.Where(t => t.Kind == PolicyKinds.TrackTerm).Select(t => TermWeight(s, t) ?? 0f).ToList();
        var goalTracks = terms.Where(t => t.Kind == PolicyKinds.GoalTrackTerm).Select(t => TermWeight(s, t) ?? 0f).ToList();
        float? goalHit = terms.Where(t => t.Kind == PolicyKinds.SimpleTerm && TermKind(s.Tree, t) == "goal_hit").Select(t => TermWeight(s, t)).FirstOrDefault();
        float max = tracks.Sum(w => w) + (goal.StepClock ? goalTracks.Sum(w => w) : 0f) + (goal.StepClock ? goalHit ?? 0f : 0f);
        return clip < max ? $"rewards clip {F(clip)} is below the summed tracking weights {F(max)}; normalised rewards would be truncated" : null;
    }

    public static bool FirstGoalHasCommand(FileSemantics s, int node) => FirstGoal(s, node) is not { } goal || goal.Command >= 0;
}
