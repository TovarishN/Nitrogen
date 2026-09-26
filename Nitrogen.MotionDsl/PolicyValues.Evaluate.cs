using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.Semantics;

namespace Nitrogen.MotionDsl;

/// <summary>
/// PV0008 (issue 241, Plan 3): PolicyCompiler.CompileEvaluate, which checks the evaluate block
/// against the policy's first goal. The block is read as the mapper reads it: the last episodes,
/// seeds, start, report and perturb toggle win; scenarios accumulate.
/// </summary>
public static partial class PolicyValues
{
    sealed record EvaluateFacts(bool HasEpisodes, int Episodes, int Seeds, bool Scripted, string? Start, List<string>? Report);

    static EvaluateFacts Evaluate(FileSemantics s, int evaluate)
    {
        var tree = s.Tree;
        bool hasEpisodes = false, scripted = false;
        int episodes = 0, seeds = 0;
        string? start = null;
        List<string>? report = null;
        foreach (var item in new EvaluateNode(tree, evaluate).Items)
        {
            if (item.Kind == PolicyKinds.EvaluateCount)
            {
                var count = new EvaluateCountNode(tree, item.Index);
                int value = Whole(Amount(s, count.Value));
                if (tree.GetText(count.Setting.Index).ToString() == "episodes") { hasEpisodes = true; episodes = value; }
                else seeds = value;
            }
            else if (item.Kind == PolicyKinds.Scenarios && new ScenariosNode(tree, item.Index).Names.Count > 0) scripted = true;
            else if (item.Kind == PolicyKinds.EvaluateStart) start = new EvaluateStartNode(tree, item.Index).Case.ToString();
            else if (item.Kind == PolicyKinds.Report)
            {
                report = new List<string>();
                foreach (var word in new ReportNode(tree, item.Index).Items) report.Add(word.ToString());
            }
        }
        return new EvaluateFacts(hasEpisodes, episodes, seeds, scripted, start, report);
    }

    /// <summary>The start kind a start word compiles to: supine and spawn are one kind.</summary>
    static string KindOf(string start) => start == "spawn" ? "supine" : start;

    /// <summary>The kinds of the goal's live start block.</summary>
    static HashSet<string> StartKinds(FileSemantics s, GoalFacts goal)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (goal.Start < 0) return kinds;
        foreach (var item in new StartNode(s.Tree, goal.Start).Cases)
        {
            if (item.Kind == PolicyKinds.SpawnCase) kinds.Add("supine");
            else if (item.Kind == PolicyKinds.CrumpleCase) kinds.Add("crumple");
            else if (item.Kind == PolicyKinds.StandingCase) kinds.Add("standing");
            else if (item.Kind == PolicyKinds.ReferenceCase) kinds.Add("reference");
        }
        return kinds;
    }

    static bool GoalHasPerturb(FileSemantics s, int goal)
    {
        foreach (var item in new GoalNode(s.Tree, goal).Items)
            if (item.Kind == PolicyKinds.Perturb) return true;
        return false;
    }

    public static string? EvaluateCountProblem(FileSemantics s, int evaluate)
    {
        if (!Live(s, evaluate)) return null;
        var e = Evaluate(s, evaluate);
        if (!e.HasEpisodes && e.Seeds == 0) return null; // the mapper reports the absence
        if (e.Scripted && e.Seeds <= 0) return "scenarios need `seeds N`";
        if (e.Scripted && e.Episodes > 0) return "use either `episodes` or `seeds` + `scenarios`";
        if (!e.Scripted && e.Seeds > 0) return "`seeds` is for scenario evaluation; declare `scenarios`";
        if (!e.Scripted && e.Episodes <= 0) return "evaluate episodes must be positive";
        return null;
    }

    public static string? ScenarioProblem(FileSemantics s, int evaluate, bool command)
    {
        if (!Live(s, evaluate) || FirstGoal(s, evaluate) is not { } goal) return null;
        var e = Evaluate(s, evaluate);
        if (!e.Scripted || e.Start is null || !StartKind(e.Start)) return null;
        if (!command) return e.Start != "standing" ? "scenarios require start standing" : null;
        return goal.Command < 0 ? "scenarios require a command block" : null;
    }

    public static string? ReportProblem(FileSemantics s, int report)
    {
        int evaluate = Enclosing(s.Tree, report, PolicyKinds.Evaluate);
        if (!Live(s, report) || evaluate < 0) return null;
        var e = Evaluate(s, evaluate);
        foreach (string r in e.Report ?? [])
        {
            bool scenario = r is "falls" or "tracking_error";
            if (e.Scripted && !scenario) return $"report '{r}' is not a scenario report (falls, tracking_error)";
            if (!e.Scripted && scenario) return $"report '{r}' needs scenarios";
        }
        return null;
    }

    public static string? StartCaseProblem(FileSemantics s, int start)
    {
        if (!Live(s, start) || FirstGoal(s, start) is not { Start: >= 0 } goal) return null;
        string word = new EvaluateStartNode(s.Tree, start).Case.ToString();
        if (!StartKind(word) || StartKinds(s, goal).Contains(KindOf(word))) return null;
        return $"evaluate start '{word}' is not a start case of goal '{goal.Name}'";
    }

    public static string? PerturbToggleProblem(FileSemantics s, int toggle)
    {
        var tree = s.Tree;
        var node = new ToggleNode(tree, toggle);
        if (tree.GetText(node.Setting.Index).ToString() != "perturb" || tree.GetText(node.State.Index).ToString() != "on") return null;
        foreach (var item in new EvaluateNode(tree, Enclosing(tree, toggle, PolicyKinds.Evaluate)).Items)
            if (item.Index > toggle && item.Kind == PolicyKinds.Toggle && tree.GetText(new ToggleNode(tree, item.Index).Setting.Index).ToString() == "perturb")
                return null; // a later perturb toggle wins
        int policy = Enclosing(tree, toggle, PolicyKinds.Policy);
        int goal = -1;
        foreach (var item in new PolicyNode(tree, policy).Items)
            if (item.Kind == PolicyKinds.Goal) { goal = item.Index; break; }
        return goal >= 0 && !GoalHasPerturb(s, goal) ? "evaluate asks for perturbation the goal does not declare (no perturb block)" : null;
    }

    public static string? EvaluateRandomizeProblem(FileSemantics s, int randomize) =>
        Enclosing(s.Tree, randomize, PolicyKinds.Evaluate) >= 0 && FirstGoal(s, randomize) is { Command: < 0 }
            ? "evaluate randomize needs a command block"
            : null;
}
