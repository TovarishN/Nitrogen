using System.Globalization;
using System.Numerics;
using System.Text;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl.Syntax;
using Ast = Gravity.MotionDSL.Ast;

namespace Nitrogen.MotionDsl;

/// <summary>
/// Maps a tree parsed by the generated Motion module to the AST MotionParser builds, applying the
/// parser's semantic rules: last attribute wins, axis names, deg conversion, part-name keywords.
/// </summary>
internal sealed class MotionAstMapper(SyntaxTree tree, SourceText source)
{
    public Ast.FileNode File()
    {
        var file = SyntaxView.Cast<FileNode>(tree, tree.Root);
        var blocks = new List<Ast.ITopLevelNode>();
        foreach (var block in file.Blocks)
        {
            switch (SyntaxKinds.LocalOf(block.Kind))
            {
                case MotionModule.LBody: blocks.Add(Body(Cast<BodyNode>(block))); break;
                case MotionModule.LBehavior: blocks.Add(Behavior(Cast<BehaviorNode>(block))); break;
                case MotionModule.LSkill: blocks.Add(Skill(Cast<SkillNode>(block))); break;
                default: blocks.Add(Motion(Cast<MotionBlockNode>(block))); break;
            }
        }
        return new Ast.FileNode(blocks);
    }

    // ---- body ----

    Ast.BodyNode Body(BodyNode node)
    {
        var variables = new List<Ast.LetNode>();
        foreach (var variable in node.Variables) variables.Add(new Ast.LetNode(variable.Name.ToString(), Expr(variable.Value)));

        var bindings = new List<Ast.MotorBindingNode>();
        if (node.Bindings.HasValue)
        {
            foreach (var binding in node.Bindings.Value.Bindings)
            {
                var segments = new List<string>();
                foreach (var segment in binding.Slot) segments.Add(segment.ToString());
                var (line, col) = source.GetLineColumn(binding.Span.Start);
                bindings.Add(new Ast.MotorBindingNode(string.Join('.', segments), PartName(binding.Target), new Ast.SourceSpan(line, col)));
            }
        }
        return new Ast.BodyNode(node.Name.ToString(), variables, bindings, Part(node.Root));
    }

    Ast.PartNode Part(PartNode node)
    {
        string name = PartName(node.Name);
        var shape = new Ast.ShapeNode(node.Shape.Type.ToString(), Exprs(node.Shape.Args));

        Ast.ExprNode? mass = null;
        Ast.TupleNode? color = null, offset = null, rotation = null;
        foreach (var attribute in node.Attributes)
        {
            switch (SyntaxKinds.LocalOf(attribute.Kind))
            {
                case MotionModule.LMass: mass = Expr(Cast<MassNode>(attribute).Value); break;
                case MotionModule.LColor: color = Tuple(Cast<ColorNode>(attribute).Value); break;
                case MotionModule.LAt: offset = Tuple(Cast<AtNode>(attribute).Value); break;
                default: rotation = Tuple(Cast<RotNode>(attribute).Value); break;
            }
        }

        Ast.JointNode? joint = null;
        var children = new List<Ast.IPartChild>();
        if (node.Contents.HasValue)
        {
            foreach (var item in node.Contents.Value.Items)
            {
                if (item.Kind == MotionKinds.Joint) joint = Joint(Cast<JointNode>(item));
                else children.Add(Child(item));
            }
        }
        return new Ast.PartNode(name, shape, mass, color, offset, rotation, joint, children);
    }

    Ast.IPartChild Child(SyntaxNode node) => SyntaxKinds.LocalOf(node.Kind) switch
    {
        MotionModule.LPart => new Ast.PartChildPart(Part(Cast<PartNode>(node))),
        MotionModule.LRepeat => new Ast.PartChildRepeat(Repeat(Cast<RepeatNode>(node))),
        _ => new Ast.PartChildFor(For(Cast<ForNode>(node))),
    };

    Ast.RepeatNode Repeat(RepeatNode node) =>
        new(Expr(node.Count), node.Iterator.ToString(), Children(node.Children));

    Ast.ForNode For(ForNode node)
    {
        var values = new List<string>();
        foreach (var value in node.Values) values.Add(value.ToString());
        return new Ast.ForNode(node.Iterator.ToString(), values, Children(node.Children));
    }

    List<Ast.IPartChild> Children(SyntaxList<SyntaxNode> nodes)
    {
        var children = new List<Ast.IPartChild>();
        foreach (var child in nodes) children.Add(Child(child));
        return children;
    }

    string PartName(SyntaxNode node)
    {
        if (node.Kind == MotionKinds.Interpolation) return Interpolation(Cast<InterpolationNode>(node));
        var plain = Cast<PlainNameNode>(node);
        string head = plain.Head.ToString();
        if (MotionModule.MatchIdentifier(head, 0) != head.Length)
        {
            // A keyword: MotionParser accepts only Body..Rot as a part name.
            var type = new MotionLexer(head).Tokenize()[0].Type;
            if (type is < TokenType.Body or > TokenType.Rot)
                throw Error($"Expected part name, got {type}", plain.Head.Span.Start);
        }
        var name = new StringBuilder(head);
        foreach (var suffix in plain.Suffixes) name.Append(Interpolation(suffix));
        return name.ToString();
    }

    static string Interpolation(InterpolationNode node) =>
        "${" + node.Var.ToString() + "}" + (node.Tail.HasValue ? node.Tail.Value.ToString() : "");

    Ast.JointNode Joint(JointNode node)
    {
        string type = Text(node.Type.Index);
        Ast.TupleNode? axis = null, anchor = null, anchorSelf = null, linearSpeed = null, angularAxis = null;
        Ast.RangeNode? range = null;
        bool motor = false, spring = false;
        Ast.ExprNode? torque = null, force = null, speed = null, length = null, hertz = null, damping = null;
        Ast.ExprNode? kp = null, kd = null, maxSpeed = null, pose = null;

        foreach (var attribute in node.Attributes)
        {
            switch (SyntaxKinds.LocalOf(attribute.Kind))
            {
                case MotionModule.LAxis: axis = AxisValue(Cast<AxisNode>(attribute).Value); break;
                case MotionModule.LRange:
                {
                    var r = Cast<RangeNode>(attribute);
                    range = new Ast.RangeNode(Expr(r.Low), Expr(r.High));
                    break;
                }
                case MotionModule.LMotorFlag: motor = true; break;
                case MotionModule.LSpringFlag: spring = true; break;
                case MotionModule.LTorque: torque = Value(attribute); break;
                case MotionModule.LForce: force = Value(attribute); break;
                case MotionModule.LSpeed: speed = Value(attribute); break;
                case MotionModule.LLength: length = Value(attribute); break;
                case MotionModule.LHertz: hertz = Value(attribute); break;
                case MotionModule.LDamping: damping = Value(attribute); break;
                case MotionModule.LAnchor: anchor = TupleValue(attribute); break;
                case MotionModule.LAnchorSelf: anchorSelf = TupleValue(attribute); break;
                case MotionModule.LLinearSpeed: linearSpeed = TupleValue(attribute); break;
                case MotionModule.LAngularAxis: angularAxis = TupleValue(attribute); break;
                case MotionModule.LKp: kp = Value(attribute); break;
                case MotionModule.LKd: kd = Value(attribute); break;
                case MotionModule.LMaxSpeed: maxSpeed = Value(attribute); break;
                default: pose = Value(attribute); break;
            }
        }
        return new Ast.JointNode(type, axis, range, motor, spring, torque, force, speed, length, hertz, damping,
            anchor, anchorSelf, linearSpeed, angularAxis, kp, kd, maxSpeed, pose);
    }

    /// <summary>Every <c>name = Expr</c> attribute has its value as child 2.</summary>
    Ast.ExprNode Value(SyntaxNode attribute) => Expr(new ExprNode(tree, tree.Child(attribute.Index, 2)));

    /// <summary>Every <c>name = Tuple</c> attribute has its tuple as child 2.</summary>
    Ast.TupleNode TupleValue(SyntaxNode attribute) => Tuple(new TupleNode(tree, tree.Child(attribute.Index, 2)));

    Ast.TupleNode AxisValue(SyntaxNode value)
    {
        if (value.Kind != MotionKinds.Identifier) return Tuple(Cast<TupleNode>(value));
        string name = value.ToString();
        return name switch
        {
            "x" => new Ast.TupleNode([new Ast.NumberLiteral(1), new Ast.NumberLiteral(0), new Ast.NumberLiteral(0)]),
            "y" => new Ast.TupleNode([new Ast.NumberLiteral(0), new Ast.NumberLiteral(1), new Ast.NumberLiteral(0)]),
            "z" => new Ast.TupleNode([new Ast.NumberLiteral(0), new Ast.NumberLiteral(0), new Ast.NumberLiteral(1)]),
            _ => throw Error($"Expected axis (x/y/z) or tuple, got '{name}'", value.Span.Start),
        };
    }

    Ast.TupleNode Tuple(TupleNode node) => new(Exprs(node.Values));

    // ---- behavior and motion ----

    Ast.BehaviorNode Behavior(BehaviorNode node)
    {
        string? inherit = null, model = null;
        Ast.ExprNode? reward = null, done = null;
        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case MotionModule.LInherit: inherit = Cast<InheritNode>(item).Name.ToString(); break;
                case MotionModule.LModel: model = Unquote(Cast<ModelNode>(item).Path.ToString()); break;
                case MotionModule.LReward: reward = Expr(Cast<RewardNode>(item).Value); break;
                default: done = Expr(Cast<DoneNode>(item).Condition); break;
            }
        }
        return new Ast.BehaviorNode(node.Name.ToString(), inherit, model, reward, done);
    }

    Ast.MotionNode Motion(MotionBlockNode node)
    {
        var steps = new List<Ast.IMotionStep>();
        foreach (var step in node.Steps)
        {
            if (step.Kind == MotionKinds.Hold)
            {
                steps.Add(new Ast.HoldStep(Expr(Cast<HoldNode>(step).Duration)));
                continue;
            }
            var keyframe = Cast<KeyframeNode>(step);
            var targets = new List<Ast.PoseTarget>();
            foreach (var target in keyframe.Targets)
                targets.Add(new Ast.PoseTarget(target.Part.ToString(), target.Property.ToString(), Expr(target.Value)));
            steps.Add(new Ast.KeyframeStep(Expr(keyframe.Time), targets));
        }
        return new Ast.MotionNode(node.Name.ToString(), steps);
    }

    // ---- skills (Plan 6): MotionParser.ParseSkill and its helpers ----

    Ast.SkillNode Skill(SkillNode node)
    {
        var lifecycle = Lifecycle(node.Lifecycle);
        Ast.ExprNode? length = node.Length.HasValue ? Expr(node.Length.Value.Value) : null;
        if (length is null && lifecycle.Kind == Ast.SkillDurationKind.Finite && lifecycle.Duration is null)
            throw Error("Finite lifecycle requires duration, or a skill length", node.Lifecycle.Span.Start);
        if (length is null && lifecycle.Kind == Ast.SkillDurationKind.Looping && lifecycle.Period is null)
            throw Error("Looping lifecycle requires period, or a skill length", node.Lifecycle.Span.Start);

        IReadOnlyList<Ast.SkillValueDeclarationNode> arguments = [];
        IReadOnlyList<Ast.SkillValueDeclarationNode> inputs = [];
        IReadOnlyList<string> requiredMotors = [];
        var sources = new List<Ast.SkillSourceNode>();
        Ast.SkillBlendNode? output = null;
        Ast.SkillCompletionNode? completion = null;
        Ast.SkillTimeoutNode? timeout = null;
        bool sawArguments = false, sawInputs = false, sawRequires = false;
        var declarations = new HashSet<string>(StringComparer.Ordinal);
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in node.Items)
        {
            int at = item.Span.Start;
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case MotionModule.LDeclarations:
                {
                    var block = Cast<DeclarationsNode>(item);
                    bool isArguments = Text(block.Section.Index) == "arguments";
                    if (isArguments ? sawArguments : sawInputs)
                        throw Error(isArguments ? "Duplicate arguments block" : "Duplicate inputs block", at);
                    var values = Declarations(block, declarations);
                    if (isArguments)
                    {
                        sawArguments = true;
                        arguments = values;
                    }
                    else
                    {
                        sawInputs = true;
                        inputs = values;
                    }
                    break;
                }
                case MotionModule.LRequires:
                    if (sawRequires) throw Error("Duplicate requires motors block", at);
                    sawRequires = true;
                    requiredMotors = RequiredMotors(Cast<RequiresNode>(item));
                    break;
                case MotionModule.LSource:
                {
                    var sourceNode = Cast<SourceNode>(item);
                    var mapped = Source(sourceNode);
                    if (!sourceNames.Add(mapped.Name))
                        throw Error($"Duplicate skill source '{mapped.Name}'", sourceNode.Name.Span.Start);
                    sources.Add(mapped);
                    break;
                }
                case MotionModule.LOutput:
                    if (output is not null) throw Error("Duplicate output block", at);
                    output = Blend(Cast<OutputNode>(item).Children, at);
                    break;
                case MotionModule.LCompletion:
                {
                    if (completion is not null) throw Error("Duplicate completion condition", at);
                    var complete = Cast<CompletionNode>(item);
                    var dwell = complete.Dwell.HasValue ? Expr(new ExprNode(tree, tree.Child(complete.Dwell.Value.Index, 1))) : null;
                    completion = new Ast.SkillCompletionNode(Expr(complete.Condition), dwell, At(at));
                    break;
                }
                default:
                {
                    if (timeout is not null) throw Error("Duplicate timeout", at);
                    var limit = Cast<TimeoutNode>(item);
                    var result = Text(limit.Result.Index) == "complete" ? Ast.SkillTimeoutResult.Complete : Ast.SkillTimeoutResult.Fail;
                    timeout = new Ast.SkillTimeoutNode(Expr(limit.Duration), result, At(at));
                    break;
                }
            }
        }

        if (output is null) throw Error("A skill requires one output blend", node.Span.Start);
        if (lifecycle.Kind == Ast.SkillDurationKind.Finite && completion is not null && timeout is null)
            throw Error("A condition-driven finite skill requires timeout", node.Span.Start);

        return new Ast.SkillNode(node.Name.ToString(), lifecycle, length, arguments, inputs, requiredMotors,
            sources, output, completion, timeout, At(node.Span.Start));
    }

    Ast.SkillLifecycleNode Lifecycle(LifecycleNode node)
    {
        int at = node.Span.Start;
        var kind = Text(node.Mode.Index) switch
        {
            "finite" => Ast.SkillDurationKind.Finite,
            "looping" => Ast.SkillDurationKind.Looping,
            _ => Ast.SkillDurationKind.Continuous,
        };
        Ast.ExprNode? duration = null, period = null, fadeIn = null, fadeOut = null;
        foreach (var field in node.Fields)
        {
            string name = Text(field.Field.Index);
            var value = Expr(field.Value);
            int fieldAt = field.Span.Start;
            switch (name)
            {
                case "duration":
                    if (duration is not null) throw Error("Duplicate lifecycle duration", fieldAt);
                    duration = value;
                    break;
                case "period":
                    if (period is not null) throw Error("Duplicate lifecycle period", fieldAt);
                    period = value;
                    break;
                case "fade_in":
                    if (fadeIn is not null) throw Error("Duplicate lifecycle fade_in", fieldAt);
                    fadeIn = value;
                    break;
                default:
                    if (fadeOut is not null) throw Error("Duplicate lifecycle fade_out", fieldAt);
                    fadeOut = value;
                    break;
            }
        }
        if (kind == Ast.SkillDurationKind.Finite && period is not null)
            throw Error("Finite lifecycle cannot declare period", at);
        if (kind == Ast.SkillDurationKind.Looping && duration is not null)
            throw Error("Looping lifecycle cannot declare duration", at);
        if (kind == Ast.SkillDurationKind.Continuous && (duration is not null || period is not null))
            throw Error("Continuous lifecycle cannot declare duration or period", at);
        return new Ast.SkillLifecycleNode(kind, duration, period, fadeIn, fadeOut, At(at));
    }

    List<Ast.SkillValueDeclarationNode> Declarations(DeclarationsNode block, HashSet<string> names)
    {
        var result = new List<Ast.SkillValueDeclarationNode>();
        foreach (var declaration in block.Values)
        {
            string name = declaration.Name.ToString();
            if (!names.Add(name)) throw Error($"Duplicate skill declaration '{name}'", declaration.Span.Start);

            var typeNode = new SyntaxNode(tree, declaration.Type.Index).Child(0);
            Ast.SkillValueType type;
            IReadOnlyList<string> enumValues = [];
            if (typeNode.Kind == MotionKinds.EnumType)
            {
                type = Ast.SkillValueType.Enum;
                var values = new List<string>();
                foreach (var value in Cast<EnumTypeNode>(typeNode).Values) values.Add(value.Name.ToString());
                enumValues = values;
            }
            else
            {
                type = typeNode.ToString() == "float" ? Ast.SkillValueType.Float : Ast.SkillValueType.Bool;
            }

            Ast.ExprNode? defaultValue = declaration.Default.HasValue
                ? Expr(new ExprNode(tree, tree.Child(declaration.Default.Value.Index, 1)))
                : null;
            Ast.RangeNode? range = null;
            if (declaration.Limits.HasValue)
            {
                var bounds = new BoundsNode(tree, tree.Child(declaration.Limits.Value.Index, 1));
                range = new Ast.RangeNode(Expr(bounds.Low), Expr(bounds.High));
            }
            result.Add(new Ast.SkillValueDeclarationNode(name, type, enumValues, defaultValue, range, At(declaration.Span.Start)));
        }
        return result;
    }

    List<string> RequiredMotors(RequiresNode node)
    {
        var motors = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in node.Motors)
        {
            var path = declaration.Path;
            string motor = PathText(path);
            if (!unique.Add(motor)) throw Error($"Duplicate required motor '{motor}'", path.Span.Start);
            motors.Add(motor);
        }
        return motors;
    }

    static string PathText(SemanticPathNode path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Segments) segments.Add(segment.ToString());
        return string.Join('.', segments);
    }

    Ast.SkillSourceNode Source(SourceNode node)
    {
        string name = node.Name.ToString();
        int nameAt = node.Name.Span.Start;
        var body = node.Body;
        switch (SyntaxKinds.LocalOf(body.Kind))
        {
            case MotionModule.LTracks:
            {
                var tracks = Cast<TracksNode>(body);
                var list = new List<Ast.SkillTrackNode>();
                foreach (var track in tracks.Items) list.Add(Track(track));
                var (persistent, paused) = Modifiers(tracks.Modifiers);
                return new Ast.SkillTrackSourceNode(name, list, At(nameAt)) { Persistent = persistent, StartsPaused = paused };
            }
            case MotionModule.LPhases:
            {
                var phases = Cast<PhasesNode>(body);
                var list = new List<Ast.SkillPhaseNode>();
                foreach (var phase in phases.Items) list.Add(Phase(phase));
                var (persistent, paused) = Modifiers(phases.Modifiers);
                return new Ast.SkillPhaseSourceNode(name, list, At(nameAt)) { Persistent = persistent, StartsPaused = paused };
            }
            case MotionModule.LMpc:
            {
                var mpc = Cast<MpcNode>(body);
                var inputs = new List<Ast.SkillValueMappingNode>();
                foreach (var input in mpc.Inputs) inputs.Add(Mapping(input.Name, input.Value, input.Span.Start));
                var (persistent, paused) = Modifiers(mpc.Modifiers);
                return new Ast.SkillMpcSourceNode(name, Unquote(mpc.Controller.ToString()), inputs, At(nameAt))
                {
                    Persistent = persistent,
                    StartsPaused = paused,
                };
            }
            case MotionModule.LNested:
            {
                var nested = Cast<NestedNode>(body);
                var arguments = new List<Ast.SkillValueMappingNode>();
                var inputs = new List<Ast.SkillValueMappingNode>();
                foreach (var item in nested.Items)
                {
                    if (item.Kind == MotionKinds.ArgumentMapping)
                    {
                        var argument = Cast<ArgumentMappingNode>(item);
                        arguments.Add(Mapping(argument.Name, argument.Value, argument.Span.Start));
                    }
                    else
                    {
                        var input = Cast<InputMappingNode>(item);
                        inputs.Add(Mapping(input.Name, input.Value, input.Span.Start));
                    }
                }
                var (persistent, paused) = Modifiers(nested.Modifiers);
                return new Ast.SkillNestedSourceNode(name, nested.SkillName.ToString(), arguments, inputs, At(nameAt))
                {
                    Persistent = persistent,
                    StartsPaused = paused,
                };
            }
            default:
            {
                var blend = Blend(Cast<BlendSourceNode>(body).Children, nameAt);
                return new Ast.SkillBlendSourceNode(name, blend.Children, blend.Span);
            }
        }
    }

    static (bool Persistent, bool Paused) Modifiers(Optional<ModifiersNode> modifiers) =>
        modifiers.HasValue ? (true, modifiers.Value.Paused.HasValue) : (false, false);

    Ast.SkillValueMappingNode Mapping(Token name, ExprNode value, int at) =>
        new(name.ToString(), Expr(value), At(at));

    Ast.SkillTrackNode Track(TrackNode node)
    {
        var path = new StringBuilder(node.Target.Motor.Head.ToString());
        foreach (var part in node.Target.Motor.Parts)
        {
            if (part.Kind == MotionKinds.PathIndex) path.Append('[').Append(Cast<PathIndexNode>(part).Name.ToString()).Append(']');
            else path.Append('.').Append(Cast<PathMemberNode>(part).Name.ToString());
        }
        return new Ast.SkillTrackNode(path.ToString(), Text(node.Target.Channel.Index), TrackValue(node.From),
            Expr(node.To), Expr(node.Start), Expr(node.End), node.Easing.ToString(), node.Release.HasValue, At(node.Span.Start));
    }

    Ast.SkillTrackValueNode TrackValue(SyntaxNode value) =>
        value.Kind == MotionKinds.RestValue ? new(true, null) : new(false, Expr(new ExprNode(tree, value.Index)));

    Ast.SkillPhaseNode Phase(PhaseNode node)
    {
        string name = node.Name.ToString();
        Ast.ExprNode? transitionDuration = null, hold = null;
        string? transitionEasing = null;
        List<Ast.SkillPhaseTargetNode>? targets = null;
        var overrides = new List<Ast.SkillPhaseOverrideNode>();
        Vector3? position = null;
        Quaternion? rotation = null;
        Ast.SourceSpan? rootSpan = null;

        foreach (var item in node.Items)
        {
            int at = item.Span.Start;
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case MotionModule.LTransition:
                {
                    if (transitionDuration is not null) throw Error("Duplicate transition", at);
                    var transition = Cast<TransitionNode>(item);
                    transitionDuration = Expr(transition.Duration);
                    transitionEasing = transition.Easing.ToString();
                    break;
                }
                case MotionModule.LPhasePose:
                    if (targets is not null) throw Error("Duplicate pose block", at);
                    targets = new List<Ast.SkillPhaseTargetNode>();
                    foreach (var target in Cast<PhasePoseNode>(item).Targets)
                        targets.Add(new Ast.SkillPhaseTargetNode(PathText(target.Slot), TrackValue(target.Value), At(target.Span.Start)));
                    break;
                case MotionModule.LOverride:
                    overrides.Add(Override(Cast<OverrideNode>(item)));
                    break;
                case MotionModule.LPhaseHold:
                    if (hold is not null) throw Error("Duplicate hold", at);
                    hold = Expr(Cast<PhaseHoldNode>(item).Value);
                    break;
                case MotionModule.LRootPosition:
                {
                    if (position is not null) throw Error("Duplicate root_position_offset", at);
                    float[] v = RootLiterals(name, "root_position_offset", Cast<RootPositionNode>(item).Values, 3, at);
                    position = new Vector3(v[0], v[1], v[2]);
                    rootSpan ??= At(at);
                    break;
                }
                default:
                {
                    if (rotation is not null) throw Error("Duplicate root_rotation_offset", at);
                    float[] v = RootLiterals(name, "root_rotation_offset", Cast<RootRotationNode>(item).Values, 4, at);
                    var quaternion = new Quaternion(v[0], v[1], v[2], v[3]);
                    if (!float.IsFinite(quaternion.LengthSquared()) || quaternion.LengthSquared() < 1e-12f)
                        throw Error($"Phase '{name}' root_rotation_offset must be finite and normalizable", at);
                    rotation = MathF.Abs(quaternion.LengthSquared() - 1f) <= 1e-6f ? quaternion : Quaternion.Normalize(quaternion);
                    rootSpan ??= At(at);
                    break;
                }
            }
        }

        if (targets is null) throw Error($"Phase '{name}' requires one pose block", node.Span.Start);
        var rootPose = rootSpan is null
            ? null
            : new Ast.SkillPhaseRootPoseNode(position ?? Vector3.Zero, rotation ?? Quaternion.Identity, rootSpan.Value);
        return new Ast.SkillPhaseNode(name, transitionDuration, transitionEasing, rootPose, targets, overrides, hold, At(node.Span.Start));
    }

    /// <summary>MotionParser.ParseFiniteRootLiteral for each value: an optional '-' and a finite number, no unit.</summary>
    float[] RootLiterals(string phase, string field, SeparatedList<RootLiteralNode> values, int count, int at)
    {
        if (values.Count != count) throw Error($"Phase '{phase}' {field} requires {count} finite numeric literals", at);
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            var literal = values[i];
            if (!float.TryParse(literal.Value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                throw Error($"Phase '{phase}' {field} requires finite numeric literals", at);
            result[i] = literal.Negative.HasValue ? -value : value;
        }
        return result;
    }

    Ast.SkillPhaseOverrideNode Override(OverrideNode node)
    {
        Ast.ExprNode? delay = null, duration = null;
        string? easing = null;
        foreach (var item in node.Items)
        {
            int at = item.Span.Start;
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case MotionModule.LOverrideDelay:
                    if (delay is not null) throw Error("Duplicate override delay", at);
                    delay = Expr(Cast<OverrideDelayNode>(item).Value);
                    break;
                case MotionModule.LOverrideTransition:
                    if (duration is not null) throw Error("Duplicate override transition", at);
                    duration = Expr(Cast<OverrideTransitionNode>(item).Value);
                    break;
                default:
                    if (easing is not null) throw Error("Duplicate override easing", at);
                    easing = Cast<OverrideEasingNode>(item).Name.ToString();
                    break;
            }
        }
        if (delay is null && duration is null && easing is null)
            throw Error("Motor override requires delay, transition duration, or easing", node.Span.Start);
        return new Ast.SkillPhaseOverrideNode(PathText(node.Slot), delay, duration, easing, At(node.Span.Start));
    }

    Ast.SkillBlendNode Blend(SyntaxList<BlendChildNode> children, int at)
    {
        var list = new List<Ast.SkillBlendChildNode>();
        foreach (var child in children)
        {
            var mode = child.Mode;
            // Every mode ends in its value: `priority E`, `weighted weight E`, …
            var value = Expr(new ExprNode(tree, mode.Child(mode.ChildCount - 1).Index));
            var kind = SyntaxKinds.LocalOf(mode.Kind) switch
            {
                MotionModule.LPriorityMode => Ast.SkillBlendMode.Priority,
                MotionModule.LWeightedMode => Ast.SkillBlendMode.Weighted,
                MotionModule.LAdditiveMode => Ast.SkillBlendMode.Additive,
                _ => Ast.SkillBlendMode.ParameterWeighted,
            };
            bool isPriority = kind == Ast.SkillBlendMode.Priority;
            list.Add(new Ast.SkillBlendChildNode(child.Source.ToString(), kind,
                isPriority ? value : null, isPriority ? null : value, At(child.Span.Start)));
        }
        return new Ast.SkillBlendNode(list, At(at));
    }

    Ast.SourceSpan At(int position)
    {
        var (line, col) = source.GetLineColumn(position);
        return new Ast.SourceSpan(line, col);
    }

    // ---- expressions ----

    List<Ast.ExprNode> Exprs(SeparatedList<ExprNode> nodes)
    {
        var list = new List<Ast.ExprNode>();
        foreach (var node in nodes) list.Add(Expr(node));
        return list;
    }

    Ast.ExprNode Expr(ExprNode node)
    {
        switch (SyntaxKinds.LocalOf(node.Kind))
        {
            case MotionModule.LNum:
            {
                var number = node.As<NumExpr>();
                float value = float.Parse(number.Value.Text, CultureInfo.InvariantCulture);
                if (number.Unit.HasValue && Text(number.Unit.Value.Index) == "deg") value *= MathF.PI / 180f;
                return new Ast.NumberLiteral(value);
            }
            case MotionModule.LTrue: return new Ast.BoolLiteral(true);
            case MotionModule.LFalse: return new Ast.BoolLiteral(false);
            case MotionModule.LCall:
            {
                var call = node.As<CallExpr>();
                return new Ast.FunctionCall(call.Callee.ToString(), Exprs(call.Args));
            }
            case MotionModule.LRef: return new Ast.IdentifierExpr(node.As<RefExpr>().Identifier.ToString());
            case MotionModule.LParen: return Expr(node.As<ParenExpr>().Expr);
            case MotionModule.LNegate: return new Ast.UnaryExpr(Ast.UnaryOp.Negate, Expr(node.As<NegateExpr>().Expr));
            case MotionModule.LNot: return new Ast.UnaryExpr(Ast.UnaryOp.Not, Expr(node.As<NotExpr>().Expr));
            case MotionModule.LMember:
            {
                var member = node.As<MemberExpr>();
                return new Ast.PropertyAccess(Expr(member.Expr), member.Property.ToString());
            }
            case MotionModule.LTernary:
            {
                var ternary = node.As<TernaryExpr>();
                return new Ast.TernaryExpr(Expr(ternary.Expr), Expr(ternary.Then), Expr(ternary.Else));
            }
            case var local:
            {
                // Every binary alternative is `Expr op Expr`: operands are children 0 and 2.
                var left = Expr(new ExprNode(tree, tree.Child(node.Index, 0)));
                var right = Expr(new ExprNode(tree, tree.Child(node.Index, 2)));
                return new Ast.BinaryExpr(left, BinaryOp(local), right);
            }
        }
    }

    static Ast.BinaryOp BinaryOp(int local) => local switch
    {
        MotionModule.LOr => Ast.BinaryOp.Or,
        MotionModule.LAnd => Ast.BinaryOp.And,
        MotionModule.LEqual => Ast.BinaryOp.Equal,
        MotionModule.LNotEqual => Ast.BinaryOp.NotEqual,
        MotionModule.LLess => Ast.BinaryOp.Less,
        MotionModule.LGreater => Ast.BinaryOp.Greater,
        MotionModule.LLessEqual => Ast.BinaryOp.LessEqual,
        MotionModule.LGreaterEqual => Ast.BinaryOp.GreaterEqual,
        MotionModule.LAdd => Ast.BinaryOp.Add,
        MotionModule.LSub => Ast.BinaryOp.Sub,
        MotionModule.LMul => Ast.BinaryOp.Mul,
        MotionModule.LDiv => Ast.BinaryOp.Div,
        _ => throw new InvalidOperationException($"not a binary kind: {local}"),
    };

    // ---- helpers ----

    T Cast<T>(SyntaxNode node) where T : struct, ISyntaxView<T> => SyntaxView.Cast<T>(tree, node.Index);

    string Text(int index) => tree.GetText(index).ToString();

    /// <summary>MotionLexer keeps a string's content verbatim: no escapes.</summary>
    static string Unquote(string quoted) => quoted.Substring(1, quoted.Length - 2);

    ParseException Error(string message, int position)
    {
        var (line, col) = source.GetLineColumn(position);
        return new ParseException(message, line, col);
    }
}
