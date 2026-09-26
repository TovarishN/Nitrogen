using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantics;

namespace Nitrogen.MotionDsl;

/// <summary>
/// The source graph (issue 240, Plan 3): MV0010's rules from <c>SkillCompiler</c>'s blend compile,
/// <c>OrderSources</c> and <c>ValidateNestedSkillGraph</c>. Names resolve by text, first declaration
/// first, as the compiler's dictionaries do; unknown and duplicate names are binding's to report.
/// </summary>
public static partial class MotionValues
{
    static readonly HashSet<string> AbsoluteChannels = new(StringComparer.Ordinal) { "target_angle", "kp", "kd", "max_speed", "max_torque" };

    /// <summary>A skill's sources by name in declaration order, then <c>output</c>; edges are blend and output children.</summary>
    sealed record SourceGraph(List<string> Order, Dictionary<string, int> Sources, Dictionary<string, List<string>> Edges);

    /// <summary>A priority the compiler accepts: a finite whole number within <c>int</c>'s range.</summary>
    public static bool Integer(float? value) =>
        value is float v && float.IsFinite(v) && v == MathF.Truncate(v) && v >= int.MinValue && v <= int.MaxValue;

    static List<string> Children(SyntaxList<BlendChildNode> children)
    {
        var names = new List<string>();
        foreach (var child in children) names.Add(child.Source.ToString());
        return names;
    }

    static SourceGraph Graph(SyntaxTree tree, int skill)
    {
        var order = new List<string>();
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int output = -1;
        foreach (var item in new SkillNode(tree, skill).Items)
        {
            if (item.Kind == MotionKinds.Output)
            {
                if (output < 0) output = item.Index;
                continue;
            }
            if (item.Kind != MotionKinds.Source) continue;
            var source = new SourceNode(tree, item.Index);
            string name = source.Name.ToString();
            if (!sources.TryAdd(name, item.Index)) continue;
            order.Add(name);
            edges[name] = source.Body.Kind == MotionKinds.BlendSource ? Children(new BlendSourceNode(tree, source.Body.Index).Children) : [];
        }
        order.Add("output");
        edges["output"] = output >= 0 ? Children(new OutputNode(tree, output).Children) : [];
        return new SourceGraph(order, sources, edges);
    }

    /// <summary>
    /// The cycle that the edge <paramref name="from"/> → <paramref name="to"/> closes, as the compiler
    /// prints it (<c>a -> b -> a</c>), when <paramref name="from"/> is the cycle's first-declared node,
    /// so each cycle is reported once; otherwise null. The breadth-first search visits only nodes
    /// declared no earlier than <paramref name="from"/>.
    /// </summary>
    static string? Cycle(List<string> order, Dictionary<string, List<string>> edges, string from, string to)
    {
        int first = order.IndexOf(from);
        if (first < 0 || order.IndexOf(to) < first) return null;
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal) { [to] = null };
        var queue = new Queue<string>();
        queue.Enqueue(to);
        while (queue.Count > 0)
        {
            string node = queue.Dequeue();
            if (node == from)
            {
                var path = new List<string>();
                for (string? step = node; step is not null; step = previous[step]) path.Add(step);
                path.Reverse();
                return string.Join(" -> ", path.Prepend(from));
            }
            if (!edges.TryGetValue(node, out var targets)) continue;
            foreach (string next in targets)
            {
                if (previous.ContainsKey(next) || order.IndexOf(next) < first) continue;
                previous[next] = node;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>The channels a source writes, as <c>WrittenChannels</c>: its tracks' channels in order, <c>target_angle</c> for a phase pose, a blend's children's; none for nested or mpc sources.</summary>
    static IEnumerable<string> Channels(SyntaxTree tree, SourceGraph graph, string name, HashSet<string> seen)
    {
        if (!seen.Add(name) || !graph.Sources.TryGetValue(name, out int source)) yield break;
        var body = new SourceNode(tree, source).Body;
        if (body.Kind == MotionKinds.Tracks)
        {
            foreach (var track in new TracksNode(tree, body.Index).Items)
                yield return tree.GetText(track.Target.Channel.Index).ToString();
        }
        else if (body.Kind == MotionKinds.Phases)
        {
            var phases = new PhasesNode(tree, body.Index).Items;
            if (phases.Count > 0 && Parts(tree, phases[0].Index).Pose is { } pose && pose.Targets.Count > 0) yield return "target_angle";
        }
        else if (body.Kind == MotionKinds.BlendSource)
        {
            foreach (string child in graph.Edges[name])
            foreach (string channel in Channels(tree, graph, child, seen))
                yield return channel;
        }
    }

    /// <summary>MV0010 on an additive blend child: its source, through any blends, writes no absolute channel.</summary>
    public static string? AdditiveProblem(FileSemantics semantics, int child)
    {
        var tree = semantics.Tree;
        var node = new BlendChildNode(tree, child);
        if (node.Mode.Kind != MotionKinds.AdditiveMode) return null;
        var graph = Graph(tree, Enclosing(tree, child, MotionKinds.Skill));
        string name = node.Source.ToString();
        foreach (string channel in Channels(tree, graph, name, new HashSet<string>(StringComparer.Ordinal)))
            if (AbsoluteChannels.Contains(channel)) return $"additive source '{name}' writes absolute channel {channel}.";
        return null;
    }

    /// <summary>MV0010 on a blend child: the edge from its blend (or the output) to its source closes no cycle.</summary>
    public static string? SourceCycle(FileSemantics semantics, int child)
    {
        var tree = semantics.Tree;
        var graph = Graph(tree, Enclosing(tree, child, MotionKinds.Skill));
        int owner = tree.Parent(child);
        while (tree.Kind(owner) != MotionKinds.BlendSource && tree.Kind(owner) != MotionKinds.Output) owner = tree.Parent(owner);
        string from = "output";
        if (tree.Kind(owner) == MotionKinds.BlendSource)
        {
            int source = Enclosing(tree, owner, MotionKinds.Source);
            from = new SourceNode(tree, source).Name.ToString();
            if (graph.Sources[from] != source) return null; // a duplicate source: binding reports it
        }
        return Cycle(graph.Order, graph.Edges, from, new BlendChildNode(tree, child).Source.ToString()) is { } path ? $"source cycle: {path}" : null;
    }

    /// <summary>MV0010 on a nested source: the skill it nests does not lead back to its own skill, among the file's skills.</summary>
    public static string? NestedCycle(FileSemantics semantics, int nested)
    {
        var tree = semantics.Tree;
        int skill = Enclosing(tree, nested, MotionKinds.Skill);
        var order = new List<string>();
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in new FileNode(tree, Enclosing(tree, skill, MotionKinds.File)).Blocks)
        {
            if (block.Kind != MotionKinds.Skill) continue;
            string name = new SkillNode(tree, block.Index).Name.ToString();
            if (!first.TryAdd(name, block.Index)) continue;
            order.Add(name);
            var targets = new List<string>();
            foreach (var item in new SkillNode(tree, block.Index).Items)
            {
                if (item.Kind != MotionKinds.Source) continue;
                var body = new SourceNode(tree, item.Index).Body;
                if (body.Kind == MotionKinds.Nested) targets.Add(new NestedNode(tree, body.Index).SkillName.ToString());
            }
            edges[name] = targets;
        }
        string from = new SkillNode(tree, skill).Name.ToString();
        if (first[from] != skill) return null; // a duplicate skill: binding reports it
        return Cycle(order, edges, from, new NestedNode(tree, nested).SkillName.ToString()) is { } path ? $"nested skill cycle: {path}" : null;
    }
}
