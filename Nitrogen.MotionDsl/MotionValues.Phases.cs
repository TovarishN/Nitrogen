using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantics;

namespace Nitrogen.MotionDsl;

/// <summary>
/// Phase sources and the skill length (issue 240, Plan 2): the rules <c>SkillPhaseCompiler</c> and
/// <c>SkillCompiler.CompileLength</c> apply, read from the tree. Each <c>…Problem</c> method returns the
/// compiler's message, or null. A value that another check reports (not constant, negative, a type
/// error) makes a rule skip, so one mistake stays one diagnostic.
/// </summary>
public static partial class MotionValues
{
    const float Tolerance = 1e-4f;

    /// <summary>One phase's items, as <c>MotionAstMapper.Phase</c> collects them (the first of each kind).</summary>
    public readonly record struct PhaseParts(PhasePoseNode? Pose, TransitionNode? Transition, PhaseHoldNode? Hold, List<OverrideNode> Overrides);

    /// <summary>A time slot's value when it is a finite, non-negative float constant; otherwise null.</summary>
    public static float? Time(FileSemantics semantics, int expr) =>
        Checkable(semantics.Get(expr, MotionModule.P_Expr_Type)) && semantics.Get(expr, MotionModule.P_Expr_Constant) is float v && float.IsFinite(v) && v >= 0f
            ? v
            : null;

    public static PhaseParts Parts(SyntaxTree tree, int phase)
    {
        PhasePoseNode? pose = null;
        TransitionNode? transition = null;
        PhaseHoldNode? hold = null;
        var overrides = new List<OverrideNode>();
        foreach (var item in new PhaseNode(tree, phase).Items)
        {
            if (item.Kind == MotionKinds.PhasePose) pose ??= new PhasePoseNode(tree, item.Index);
            else if (item.Kind == MotionKinds.Transition) transition ??= new TransitionNode(tree, item.Index);
            else if (item.Kind == MotionKinds.PhaseHold) hold ??= new PhaseHoldNode(tree, item.Index);
            else if (item.Kind == MotionKinds.Override) overrides.Add(new OverrideNode(tree, item.Index));
        }
        return new PhaseParts(pose, transition, hold, overrides);
    }

    /// <summary>
    /// A phase source's authored end, as <c>SkillPhaseCompiler</c> computes it: each phase arrives a
    /// hold and a shared transition after the one before, and the source ends after the last hold.
    /// Overrides stay inside their transition, so they never move the end. Null when there are no
    /// phases, the transitions are misplaced, or a hold or transition is not a valid time (MV0001,
    /// MV0002 or MV0006 reports that).
    /// </summary>
    public static float? Timeline(FileSemantics semantics, int phases)
    {
        var tree = semantics.Tree;
        var items = new PhasesNode(tree, phases).Items;
        if (items.Count == 0) return null;
        float arrival = 0f, hold = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            var parts = Parts(tree, items[i].Index);
            if (i == 0 && parts.Transition is not null) return null;
            if (i > 0)
            {
                if (parts.Transition is not { } transition || Time(semantics, transition.Duration.Index) is not float shared) return null;
                arrival = arrival + hold + shared;
            }
            if (parts.Hold is { } phaseHold)
            {
                if (Time(semantics, phaseHold.Value.Index) is not float value) return null;
                hold = value;
            }
            else hold = 0f;
        }
        return arrival + hold;
    }

    static int Enclosing(SyntaxTree tree, int node, int kind)
    {
        int parent = tree.Parent(node);
        while (tree.Kind(parent) != kind) parent = tree.Parent(parent);
        return parent;
    }

    /// <summary>The phase's source, and the phase's index in it.</summary>
    static (PhasesNode Source, int Index) Position(SyntaxTree tree, int phase)
    {
        var source = new PhasesNode(tree, Enclosing(tree, phase, MotionKinds.Phases));
        for (int i = 0; i < source.Items.Count; i++)
            if (source.Items[i].Index == phase) return (source, i);
        throw new InvalidOperationException("a phase outside its source");
    }

    /// <summary>A slot as the compiler spells it: the segments joined by dots.</summary>
    static string Path(SemanticPathNode path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Segments) segments.Add(segment.ToString());
        return string.Join('.', segments);
    }

    /// <summary>The pose's motors in order, or null when the phase has no pose block.</summary>
    static List<string>? Motors(PhaseParts parts)
    {
        if (parts.Pose is not { } pose) return null;
        var motors = new List<string>();
        foreach (var target in pose.Targets) motors.Add(Path(target.Slot));
        return motors;
    }

    /// <summary>The first pose's motors, when the compiler accepts that pose: present, not empty, no duplicates.</summary>
    static HashSet<string>? Expected(SyntaxTree tree, PhasesNode source)
    {
        if (source.Items.Count == 0 || Motors(Parts(tree, source.Items[0].Index)) is not { Count: > 0 } motors) return null;
        var set = new HashSet<string>(motors, StringComparer.Ordinal);
        return set.Count == motors.Count ? set : null;
    }

    /// <summary>MV0006 on a phase: the first has no incoming transition or override; every later one has a transition.</summary>
    public static string? PhaseProblem(FileSemantics semantics, int phase)
    {
        var (_, index) = Position(semantics.Tree, phase);
        var parts = Parts(semantics.Tree, phase);
        if (index == 0)
            return parts.Transition is not null || parts.Overrides.Count > 0 ? "The first phase cannot have an incoming transition." : null;
        return parts.Transition is null ? "Every phase after the first requires an incoming transition." : null;
    }

    /// <summary>MV0007 on a phase: the first pose is not empty, and every later pose has the first pose's motors.</summary>
    public static string? PoseProblem(FileSemantics semantics, int phase)
    {
        var tree = semantics.Tree;
        var (source, index) = Position(tree, phase);
        if (Motors(Parts(tree, phase)) is not { } motors) return null;
        if (index == 0) return motors.Count == 0 ? "The first phase pose cannot be empty." : null;
        var actual = new HashSet<string>(motors, StringComparer.Ordinal);
        if (actual.Count != motors.Count || Expected(tree, source) is not { } expected) return null; // a duplicate, or a first pose MV0007 already reports
        return actual.SetEquals(expected) ? null : "Every phase pose must contain exactly the same motors.";
    }

    /// <summary>MV0007 on a target: no earlier target in its pose names the same motor.</summary>
    public static string? DuplicateMotor(FileSemantics semantics, int target)
    {
        var tree = semantics.Tree;
        string motor = Path(new PoseTargetNode(tree, target).Slot);
        foreach (var other in new PhasePoseNode(tree, Enclosing(tree, target, MotionKinds.PhasePose)).Targets)
        {
            if (other.Index == target) return null;
            if (Path(other.Slot) == motor) return "A phase pose cannot contain duplicate motors.";
        }
        return null;
    }

    /// <summary>
    /// MV0008 on a target: <c>rest</c> only in the first phase; otherwise a finite number, or a negated
    /// one, with parentheses aside (the compiler's AST has none). A target that is not a float is a
    /// type error, so this rule skips it.
    /// </summary>
    public static string? TargetProblem(FileSemantics semantics, int target)
    {
        var tree = semantics.Tree;
        var value = new PoseTargetNode(tree, target).Value;
        if (value.Kind == MotionKinds.RestValue)
            return Position(tree, Enclosing(tree, target, MotionKinds.Phase)).Index > 0 ? "Only the first phase may use rest targets." : null;
        if (!Checkable(semantics.Get(value.Index, MotionModule.P_Expr_Type))) return null;
        return Literal(tree, value.Index) && Finite(semantics.Get(value.Index, MotionModule.P_Expr_Constant))
            ? null
            : "Phase targets must be finite numeric literals.";
    }

    static bool Literal(SyntaxTree tree, int expr)
    {
        expr = Unwrap(tree, expr);
        if (tree.Kind(expr) == MotionKinds.Negate) expr = Unwrap(tree, tree.Child(expr, 1));
        return tree.Kind(expr) == MotionKinds.Num;
    }

    static int Unwrap(SyntaxTree tree, int expr)
    {
        while (tree.Kind(expr) == MotionKinds.Paren) expr = tree.Child(expr, 1);
        return expr;
    }

    /// <summary>
    /// MV0009 on an override after the first phase: a motor of the first pose, overridden once in the
    /// phase, whose window (delay plus its transition, else the shared one) stays inside the shared
    /// transition. An override on the first phase is MV0006.
    /// </summary>
    public static string? OverrideProblem(FileSemantics semantics, int @override)
    {
        var tree = semantics.Tree;
        int phase = Enclosing(tree, @override, MotionKinds.Phase);
        var (source, index) = Position(tree, phase);
        if (index == 0 || Expected(tree, source) is not { } expected) return null;
        var node = new OverrideNode(tree, @override);
        string motor = Path(node.Slot);
        var parts = Parts(tree, phase);
        bool repeated = false;
        foreach (var other in parts.Overrides)
        {
            if (other.Index == @override) break;
            if (Path(other.Slot) == motor) repeated = true;
        }
        if (repeated || !expected.Contains(motor)) return $"Invalid override for motor '{motor}'.";
        if (parts.Transition is not { } transition || Time(semantics, transition.Duration.Index) is not float shared) return null;
        float duration = shared;
        OverrideDelayNode? delay = null;
        foreach (var item in node.Items)
        {
            if (item.Kind == MotionKinds.OverrideTransition)
            {
                if (Time(semantics, new OverrideTransitionNode(tree, item.Index).Value.Index) is not float value) return null;
                if (value > shared + Tolerance) return "A motor transition cannot exceed the shared transition duration.";
                duration = value;
            }
            else if (item.Kind == MotionKinds.OverrideDelay) delay ??= new OverrideDelayNode(tree, item.Index);
        }
        if (delay is { } wait && Time(semantics, wait.Value.Index) is float start && start + duration > shared + Tolerance)
            return $"Motor window for '{motor}' must remain inside the shared transition.";
        return null;
    }

    /// <summary>
    /// MV0005, as <c>SkillCompiler.CompileLength</c>: a stated length is positive, agrees with a finite
    /// skill's duration or a looping skill's period, and covers every phase timeline. Without a length,
    /// the duration, or else the period, must cover them. Null when a value it needs is invalid (MV0001
    /// or MV0002 reports it).
    /// </summary>
    public static string? LengthProblem(FileSemantics semantics, int skill)
    {
        var tree = semantics.Tree;
        var node = new SkillNode(tree, skill);
        float duration = 0f, period = 0f;
        foreach (var field in node.Lifecycle.Fields)
        {
            string name = tree.GetText(field.Field.Index).ToString();
            if (name is not ("duration" or "period")) continue;
            if (Time(semantics, field.Value.Index) is not float value) return null;
            if (name == "duration") duration = value;
            else period = value;
        }
        string mode = tree.GetText(node.Lifecycle.Mode.Index).ToString();

        float? furthest = 0f;
        foreach (var item in node.Items)
        {
            if (item.Kind != MotionKinds.Source) continue;
            var body = new SourceNode(tree, item.Index).Body;
            if (body.Kind != MotionKinds.Phases) continue;
            furthest = furthest is float current && Timeline(semantics, body.Index) is float end ? MathF.Max(current, end) : null;
        }

        if (!node.Length.HasValue)
            return duration > 0f ? Extent(duration, furthest) : period > 0f ? Extent(period, furthest) : null;
        if (Time(semantics, node.Length.Value.Value.Index) is not float length) return null;
        if (length <= 0f) return "Skill length must be greater than zero.";
        if (mode == "finite" && duration > 0f && MathF.Abs(duration - length) > Tolerance)
            return $"Skill length {Format(length)}s conflicts with lifecycle duration {Format(duration)}s.";
        if (mode == "looping" && period > 0f && MathF.Abs(period - length) > Tolerance)
            return $"Skill length {Format(length)}s conflicts with lifecycle period {Format(period)}s.";
        return Extent(length, furthest);
    }

    /// <summary>MV0005 at a stated length.</summary>
    public static string? StatedLengthProblem(FileSemantics semantics, int length) =>
        LengthProblem(semantics, semantics.ParentOf(length));

    /// <summary>MV0005 at the lifecycle, for a skill with no stated length.</summary>
    public static string? UnstatedLengthProblem(FileSemantics semantics, int lifecycle)
    {
        int skill = semantics.ParentOf(lifecycle);
        return new SkillNode(semantics.Tree, skill).Length.HasValue ? null : LengthProblem(semantics, skill);
    }

    static string? Extent(float length, float? furthest) =>
        furthest is float end && end > length + Tolerance
            ? $"Phase timeline ends at {Format(end)}s, beyond skill length {Format(length)}s."
            : null;
}
