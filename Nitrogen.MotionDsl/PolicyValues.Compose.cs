using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.Semantics;

namespace Nitrogen.MotionDsl;

/// <summary>
/// Compose's rules (issue 241, Plan 3): PolicyAstMapper's requirements and ComposeCompiler's
/// checks. ComposeCompiler's messages name the composition and carry no position.
/// </summary>
public static partial class PolicyValues
{
    static string ComposeName(FileSemantics s, int node) =>
        new ComposeNode(s.Tree, s.Tree.Kind(node) == PolicyKinds.Compose ? node : Enclosing(s.Tree, node, PolicyKinds.Compose)).Name.ToString();

    public static bool ComposeHas(FileSemantics s, int compose, string what)
    {
        foreach (var item in new ComposeNode(s.Tree, compose).Items)
        {
            if (what == "scene" && item.Kind == PolicyKinds.ComposeScene) return true;
            if (what == "walk" && item.Kind == PolicyKinds.Walk) return true;
            if (what == "getup" && item.Kind == PolicyKinds.Getup) return true;
        }
        return false;
    }

    /// <summary>A pose some earlier getup, or an earlier word of this one, already owns (ignoring case).</summary>
    public static string? PoseProblem(FileSemantics s, int pose)
    {
        var tree = s.Tree;
        string word = new PoseNameNode(tree, pose).Name.ToString().ToLowerInvariant();
        foreach (var item in new ComposeNode(tree, Enclosing(tree, pose, PolicyKinds.Compose)).Items)
        {
            if (item.Kind != PolicyKinds.Getup) continue;
            foreach (var other in new GetupNode(tree, item.Index).Poses)
            {
                if (other.Index == pose) return null;
                if (other.Name.ToString().ToLowerInvariant() == word)
                    return $"compose '{ComposeName(s, pose)}': pose '{word}' is owned twice; one slot per pose";
            }
        }
        return null;
    }

    /// <summary>A rule setting's final value: rules blocks carry over, so the last line of the setting decides.</summary>
    public static string? RuleProblem(FileSemantics s, int rule)
    {
        var tree = s.Tree;
        var node = new RuleNode(tree, rule);
        string setting = tree.GetText(node.Setting.Index).ToString();
        foreach (var item in new ComposeNode(tree, Enclosing(tree, rule, PolicyKinds.Compose)).Items)
        {
            if (item.Kind != PolicyKinds.Rules) continue;
            foreach (var other in new RulesNode(tree, item.Index).Items)
                if (other.Index > rule && tree.GetText(other.Setting.Index).ToString() == setting) return null;
        }
        if (Amount(s, node.Value) is not float value) return null;
        if (setting == "retries")
            return Integer(value) && value < 0f ? $"compose '{ComposeName(s, rule)}': retries must not be negative" : null;
        return value <= 0f ? $"compose '{ComposeName(s, rule)}': stand_to_walk, hand_over and settle_cap must be positive" : null;
    }

    public static string? NonFiniteProblem(FileSemantics s, int channel)
    {
        var tree = s.Tree;
        var node = new CommandChannelNode(tree, channel);
        if (Amount(s, node.Value) is not float value || float.IsFinite(value)) return null;
        string command = new ComposeCommandNode(tree, Enclosing(tree, channel, PolicyKinds.ComposeCommand)).Name.ToString();
        return $"compose '{ComposeName(s, channel)}': command '{command}' sets '{node.Channel}' to a non-finite value";
    }
}
