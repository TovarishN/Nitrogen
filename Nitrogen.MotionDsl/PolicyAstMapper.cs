using System.Globalization;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl.PolicySyntax;
using Ast = Gravity.MotionDSL.Ast;

namespace Nitrogen.MotionDsl;

/// <summary>
/// Policy.ngr's tree → PolicyParser's AST. The grammar fixes the shape; this class holds
/// PolicyParser's semantics: last-wins and accumulating blocks, required blocks, integer and
/// count literals, and spans. Error texts follow PolicyParser's; positions may differ.
/// </summary>
internal sealed class PolicyAstMapper(SyntaxTree tree, SourceText source)
{
    // ---- policy ----

    public Ast.PolicyNode Policy()
    {
        var node = SyntaxView.Cast<PolicyDocumentNode>(tree, tree.Root).Policy;
        int at = node.Span.Start;
        string? rig = null, scene = null;
        var references = new List<Ast.ReferenceDeclNode>();
        Ast.ActuateNode? actuate = null;
        Ast.ObserveNode? observe = null;
        var goals = new List<Ast.GoalNode>();
        Ast.TrainNode? train = null;
        Ast.EvaluateNode? evaluate = null;

        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LRequires:
                {
                    var requires = Cast<RequiresNode>(item);
                    if (Text(requires.Target.Index) == "rig") rig = Unquote(requires.Asset);
                    else scene = Unquote(requires.Asset);
                    break;
                }
                case PolicyModule.LReference:
                {
                    var reference = Cast<ReferenceNode>(item);
                    string? onScene = reference.Recording.HasValue
                        ? Unquote(Text(tree.Child(reference.Recording.Value.Index, 2)))
                        : null;
                    references.Add(new Ast.ReferenceDeclNode(reference.Name.ToString(), Unquote(reference.Skill), At(item.Span.Start))
                    {
                        RecordingScene = onScene,
                    });
                    break;
                }
                case PolicyModule.LActuate: actuate = Actuate(Cast<ActuateNode>(item)); break;
                case PolicyModule.LObserve: observe = Observe(Cast<ObserveNode>(item)); break;
                case PolicyModule.LGoal: goals.Add(Goal(Cast<GoalNode>(item))); break;
                case PolicyModule.LTrain: train = Train(Cast<TrainNode>(item)); break;
                default: evaluate = Evaluate(Cast<EvaluateNode>(item)); break;
            }
        }

        if (rig == null) throw Error("policy is missing 'requires rig'", at);
        if (scene == null) throw Error("policy is missing 'requires scene'", at);
        if (observe == null) throw Error("policy is missing an 'observe' block", at);
        if (goals.Count == 0) throw Error("policy needs at least one 'goal'", at);
        if (train == null) throw Error("policy is missing a 'train' block", at);
        if (evaluate == null) throw Error("policy is missing an 'evaluate' block", at);

        var span = At(at);
        return new Ast.PolicyNode(node.Name.ToString(), new Ast.RequiresNode(rig, scene, span), references, actuate,
            observe, goals, train, evaluate, span);
    }

    Ast.ActuateNode Actuate(ActuateNode node)
    {
        float? rateLimit = null;
        float scale = 1f, smoothing = 0f;
        bool any = false;
        var clamps = new List<(string, float)>();
        foreach (var item in node.Items)
        {
            any = true;
            if (item.Kind == PolicyKinds.Clamp)
            {
                var clamp = Cast<ClampNode>(item);
                clamps.Add((clamp.Fragment.ToString(), Float(clamp.Radians)));
                continue;
            }
            var setting = Cast<ActuateSettingNode>(item);
            float value = Float(setting.Value);
            switch (Text(setting.Setting.Index))
            {
                case "rate_limit": rateLimit = value; break;
                case "scale": scale = value; break;
                default: smoothing = value; break;
            }
        }
        if (!any) throw Error("actuate block needs rate_limit, scale, smoothing or clamp", node.Span.Start);
        return new Ast.ActuateNode(rateLimit, scale, smoothing, At(node.Span.Start)) { Clamps = clamps };
    }

    Ast.ObserveNode Observe(ObserveNode node)
    {
        var blocks = new List<Ast.ObsBlockNode>();
        var noise = new List<Ast.NoiseChannelNode>();
        int latencyMin = 0, latencyMax = 0;
        foreach (var item in node.Items)
        {
            var at = At(item.Span.Start);
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LNoise:
                    foreach (var channel in Cast<NoiseNode>(item).Channels)
                        noise.Add(new Ast.NoiseChannelNode(channel.Channel.ToString(), Float(channel.Sigma), At(channel.Span.Start)));
                    break;
                case PolicyModule.LLatency:
                {
                    var latency = Cast<LatencyNode>(item);
                    latencyMin = Int(latency.Low);
                    latencyMax = Int(latency.High);
                    break;
                }
                case PolicyModule.LObsReference:
                {
                    var reference = Cast<ObsReferenceNode>(item);
                    blocks.Add(new Ast.ObsBlockNode(Text(reference.Block.Index), new List<string>(), reference.Reference.ToString(), at));
                    break;
                }
                case PolicyModule.LObserveBare:
                    blocks.Add(new Ast.ObsBlockNode(Text(Cast<ObserveBareNode>(item).Block.Index), new List<string>(), null, at));
                    break;
                default:
                {
                    // pelvis / joints / contacts / com: the leading word, then the list of its parts.
                    var parts = new List<string>();
                    foreach (var part in item.Child(1).Children) parts.Add(part.ToString());
                    blocks.Add(new Ast.ObsBlockNode(item.Child(0).ToString(), parts, null, at));
                    break;
                }
            }
        }
        return new Ast.ObserveNode(blocks, noise, latencyMin, latencyMax, At(node.Span.Start));
    }

    // ---- goal ----

    Ast.GoalNode Goal(GoalNode node)
    {
        string name = node.Name.ToString();
        int at = node.Span.Start;
        Ast.ClockNode? clock = null;
        List<Ast.RewardTermNode>? reward = null;
        List<Ast.StartCaseNode>? start = null;
        List<Ast.TerminationNode>? terminate = null;
        Ast.PerturbNode? perturb = null;
        Ast.CommandNode? command = null;
        Ast.RandomizeNode? randomize = null;
        Ast.SuccessNode? success = null;

        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LStepsClock:
                    clock = new Ast.ClockNode("", "steps", 0, Int(Cast<StepsClockNode>(item).Steps), At(item.Span.Start));
                    break;
                case PolicyModule.LRefClock:
                {
                    var refClock = Cast<RefClockNode>(item);
                    int hold = refClock.Hold.HasValue ? Int(NumAt(refClock.Hold.Value, 1)) : 0;
                    clock = new Ast.ClockNode(refClock.Reference.ToString(), refClock.Mode.ToString(), hold, 0, At(item.Span.Start));
                    break;
                }
                case PolicyModule.LReward: reward = Reward(Cast<RewardNode>(item)); break;
                case PolicyModule.LStart: start = Start(Cast<StartNode>(item)); break;
                case PolicyModule.LTerminate: terminate = Terminate(Cast<TerminateNode>(item)); break;
                case PolicyModule.LPerturb: perturb = Perturb(Cast<PerturbNode>(item)); break;
                case PolicyModule.LCommand: command = Command(Cast<CommandNode>(item)); break;
                case PolicyModule.LRandomize: randomize = Randomize(Cast<RandomizeNode>(item)); break;
                default: success = Success(Cast<SuccessNode>(item)); break;
            }
        }

        if (clock == null) throw Error($"goal '{name}' is missing 'clock'", at);
        if (reward == null) throw Error($"goal '{name}' is missing 'reward'", at);
        if (start == null) throw Error($"goal '{name}' is missing 'start'", at);
        if (terminate == null) throw Error($"goal '{name}' is missing 'terminate'", at);
        if (success == null) throw Error($"goal '{name}' is missing 'success'", at);
        return new Ast.GoalNode(name, clock, reward, start, terminate, perturb, command, randomize, success, At(at));
    }

    List<Ast.RewardTermNode> Reward(RewardNode node)
    {
        var terms = new List<Ast.RewardTermNode>();
        foreach (var term in node.Terms)
        {
            var at = At(term.Span.Start);
            switch (SyntaxKinds.LocalOf(term.Kind))
            {
                case PolicyModule.LTrackTerm:
                {
                    var track = Cast<TrackTermNode>(term);
                    bool rms = false;
                    var jointWeights = new List<Ast.JointWeightNode>();
                    string? anchored = null;
                    foreach (var option in track.Options)
                    {
                        switch (SyntaxKinds.LocalOf(option.Kind))
                        {
                            case PolicyModule.LRms: rms = true; break;
                            case PolicyModule.LWeights:
                                foreach (var weight in Cast<WeightsNode>(option).Items)
                                    jointWeights.Add(new Ast.JointWeightNode(weight.Motor.ToString(), Float(weight.Weight), At(weight.Span.Start)));
                                break;
                            default: anchored = Text(Cast<AnchoredNode>(option).Frame.Index); break;
                        }
                    }
                    terms.Add(new Ast.RewardTermNode("track", track.Reference.ToString(), Text(track.Channel.Index),
                        Float(track.Weight), Float(track.Sharpness), rms, jointWeights, anchored, 0f, at));
                    break;
                }
                case PolicyModule.LJointLimitTerm:
                {
                    var limit = Cast<JointLimitTermNode>(term);
                    terms.Add(new Ast.RewardTermNode("joint_limit", null, null, Float(limit.Weight), 0f, false, [], null,
                        Float(limit.Margin), at) { Gate = Gate(limit.Gate) });
                    break;
                }
                case PolicyModule.LGoalTrackTerm:
                {
                    var goal = Cast<GoalTrackTermNode>(term);
                    terms.Add(new Ast.RewardTermNode("goal_track", null, null, Float(goal.Weight), 0f, false, [], null, 0f, at)
                    {
                        Family = goal.Family.ToString(),
                        Halving = Float(goal.Halving),
                        Gate = Gate(goal.Gate),
                    });
                    break;
                }
                case PolicyModule.LSimpleTerm:
                {
                    var simple = Cast<SimpleTermNode>(term);
                    terms.Add(new Ast.RewardTermNode(Text(simple.Term.Index), null, null, Float(simple.Weight), 0f, false, [], null, 0f, at)
                    {
                        Gate = Gate(simple.Gate),
                    });
                    break;
                }
                default:
                {
                    var gait = Cast<GaitTermNode>(term);
                    var parameters = new Dictionary<string, float>();
                    foreach (var parameter in gait.Params) parameters[Text(parameter.Name.Index)] = Float(parameter.Value);
                    terms.Add(new Ast.RewardTermNode(Text(gait.Term.Index), null, null, Float(gait.Weight), 0f, false, [], null, 0f, at)
                    {
                        Params = parameters,
                        Gate = Gate(gait.Gate),
                    });
                    break;
                }
            }
        }
        return terms;
    }

    string? Gate(Optional<GateNode> gate) => gate.HasValue ? Text(gate.Value.Name.Index) : null;

    List<Ast.StartCaseNode> Start(StartNode node)
    {
        var cases = new List<Ast.StartCaseNode>();
        foreach (var item in node.Cases)
        {
            var at = At(item.Span.Start);
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LSpawnCase:
                {
                    var spawn = Cast<SpawnCaseNode>(item);
                    int settle = 0;
                    float jointNoise = 0f;
                    if (spawn.Options.HasValue)
                    {
                        foreach (var option in new SyntaxList<SpawnOptionNode>(tree, tree.Child(spawn.Options.Value.Index, 1)))
                        {
                            if (Text(option.Option.Index) == "settle") settle = Int(option.Value);
                            else jointNoise = Float(option.Value);
                        }
                    }
                    cases.Add(new Ast.StartCaseNode(Text(spawn.Case.Index), Float(spawn.Probability), null, settle, jointNoise, 0f, 1f, at));
                    break;
                }
                case PolicyModule.LCrumpleCase:
                {
                    var crumple = Cast<CrumpleCaseNode>(item);
                    float cut = 0.5f, shoveLo = 0f, shoveHi = 0f, jointNoise = 0f;
                    int settle = 0;
                    foreach (var option in crumple.Options)
                    {
                        if (option.Kind == PolicyKinds.CrumpleShove)
                        {
                            var shove = Cast<CrumpleShoveNode>(option);
                            shoveLo = Float(shove.Low);
                            shoveHi = Float(shove.High);
                            continue;
                        }
                        var setting = Cast<CrumpleSettingNode>(option);
                        switch (Text(setting.Option.Index))
                        {
                            case "cut": cut = Float(setting.Value); break;
                            case "settle": settle = Int(setting.Value); break;
                            default: jointNoise = Float(setting.Value); break;
                        }
                    }
                    cases.Add(new Ast.StartCaseNode("crumple", Float(crumple.Probability), null, settle, jointNoise, 0f, 1f, at)
                    {
                        Cut = cut,
                        ShoveMin = shoveLo,
                        ShoveMax = shoveHi,
                    });
                    break;
                }
                case PolicyModule.LStandingCase:
                    cases.Add(new Ast.StartCaseNode("standing", Float(Cast<StandingCaseNode>(item).Probability), null, 0, 0f, 0f, 1f, at));
                    break;
                default:
                {
                    var reference = Cast<ReferenceCaseNode>(item);
                    float lo = 0f, hi = 1f;
                    if (reference.Window.Kind == PolicyKinds.WindowRange)
                    {
                        var range = Cast<WindowRangeNode>(reference.Window);
                        lo = Float(range.Low);
                        hi = Float(range.High);
                    }
                    cases.Add(new Ast.StartCaseNode("reference", Float(reference.Probability), reference.Reference.ToString(), 0, 0f, lo, hi, at));
                    break;
                }
            }
        }
        return cases;
    }

    List<Ast.TerminationNode> Terminate(TerminateNode node)
    {
        var list = new List<Ast.TerminationNode>();
        foreach (var item in node.Items)
        {
            var at = At(item.Span.Start);
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LNonfinite:
                    list.Add(new Ast.TerminationNode("nonfinite", 0f, at));
                    break;
                case PolicyModule.LBelowTermination:
                    list.Add(new Ast.TerminationNode("height", Float(Cast<BelowTerminationNode>(item).Threshold), at));
                    break;
                case PolicyModule.LAboveTermination:
                {
                    var above = Cast<AboveTerminationNode>(item);
                    list.Add(new Ast.TerminationNode(Text(above.Measure.Index), Float(above.Threshold), at));
                    break;
                }
                default:
                    list.Add(new Ast.TerminationNode("forbidden_contact_steps", Int(Cast<ContactTerminationNode>(item).Threshold), at));
                    break;
            }
        }
        return list;
    }

    Ast.PerturbNode Perturb(PerturbNode node)
    {
        Ast.PerturbNode? result = null;
        foreach (var push in node.Pushes)
        {
            if (result != null) throw Error("perturb takes exactly one push line in v1", push.Span.Start);
            result = new Ast.PerturbNode(Float(push.Max), Float(push.Low), Float(push.High), At(node.Span.Start));
        }
        return result ?? throw Error("perturb block needs a push line", node.Span.Start);
    }

    Ast.CommandNode Command(CommandNode node)
    {
        int at = node.Span.Start;
        List<string>? channels = null;
        var families = new Dictionary<string, Ast.FamilyNode>();
        var box = new Dictionary<string, Ast.BoxNode>();
        float resampleMin = 0f, resampleMax = 0f, standingHeight = 0f;
        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LStandingHeight:
                    standingHeight = Float(Cast<StandingHeightNode>(item).Value);
                    break;
                case PolicyModule.LChannels:
                    channels = new List<string>();
                    foreach (var name in Cast<ChannelsNode>(item).Names) channels.Add(name.Name.ToString());
                    break;
                case PolicyModule.LFamilies:
                    foreach (var family in Cast<FamiliesNode>(item).Items)
                    {
                        float? from = family.FadeFrom.HasValue ? Float(NumAt(family.FadeFrom.Value, 1)) : null;
                        families[family.Name.ToString()] = new Ast.FamilyNode(Float(family.Probability), from);
                    }
                    break;
                case PolicyModule.LBox:
                    foreach (var entry in Cast<BoxNode>(item).Items)
                    {
                        float lo = Float(entry.StartLow), hi = Float(entry.StartHigh);
                        float capLo = lo, capHi = hi;
                        if (entry.Cap.HasValue)
                        {
                            capLo = Float(NumAt(entry.Cap.Value, 1));
                            capHi = Float(NumAt(entry.Cap.Value, 3));
                        }
                        box[entry.Channel.ToString()] = new Ast.BoxNode(lo, hi, capLo, capHi);
                    }
                    break;
                default:
                {
                    var resample = Cast<ResampleNode>(item);
                    resampleMin = Float(resample.Low);
                    resampleMax = Float(resample.High);
                    break;
                }
            }
        }
        if (channels == null) throw Error("command block needs 'channels'", at);
        if (families.Count == 0) throw Error("command block needs 'families'", at);
        if (resampleMax <= 0f) throw Error("command block needs 'resample every a..b'", at);
        if (standingHeight <= 0f) throw Error("command block needs 'standing_height'", at);
        return new Ast.CommandNode(channels, families, box, resampleMin, resampleMax, standingHeight, At(at));
    }

    Ast.RandomizeNode Randomize(RandomizeNode node)
    {
        (float, float) friction = (1f, 1f), gain = (1f, 1f), torqueScale = (1f, 1f);
        (int, int) latency = (0, 0);
        float poseJitter = 0f, velocityJitter = 0f;
        foreach (var item in node.Items)
        {
            if (item.Kind == PolicyKinds.RandomizeJitter)
            {
                var jitter = Cast<RandomizeJitterNode>(item);
                if (Text(jitter.Setting.Index) == "pose_jitter") poseJitter = Float(jitter.Value);
                else velocityJitter = Float(jitter.Value);
                continue;
            }
            var range = Cast<RandomizeRangeNode>(item);
            switch (Text(range.Setting.Index))
            {
                case "friction": friction = (Float(range.Low), Float(range.High)); break;
                case "gain": gain = (Float(range.Low), Float(range.High)); break;
                case "torque_scale": torqueScale = (Float(range.Low), Float(range.High)); break;
                default: latency = (Int(range.Low), Int(range.High)); break;
            }
        }
        return new Ast.RandomizeNode(friction, gain, torqueScale, latency, poseJitter, velocityJitter, At(node.Span.Start));
    }

    Ast.SuccessNode Success(SuccessNode node)
    {
        var at = At(node.Span.Start);
        var body = node.Body;
        switch (SyntaxKinds.LocalOf(body.Kind))
        {
            case PolicyModule.LTrackingSuccess:
                return new Ast.SuccessNode("tracking", 0f, 0f, 0f, Float(Cast<TrackingSuccessNode>(body).Error), at);
            case PolicyModule.LSupineSuccess:
                return new Ast.SuccessNode("supine", 0f, 0f, 0f, 0f, at) { FacingBelow = Float(Cast<SupineSuccessNode>(body).Facing) };
            default:
            {
                var standing = Cast<StandingSuccessNode>(body);
                return new Ast.SuccessNode("standing", Float(standing.Height), Float(standing.Tolerance), Float(standing.Upright), 0f, at);
            }
        }
    }

    // ---- train / evaluate ----

    Ast.TrainNode Train(TrainNode node)
    {
        int at = node.Span.Start;
        string? algorithm = null;
        int? worlds = null, seed = null;
        long? timesteps = null;
        List<int>? network = null;
        bool bounded = false;
        Ast.ScheduleNode? lr = null, entropy = null, stdCap = null;
        Ast.ScheduleNode? commandBox = null, familyMix = null, stepLength = null, postureDepth = null, pushScale = null;
        float? obsClip = null, rewardClip = null;

        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LAlgorithm:
                    algorithm = Cast<AlgorithmNode>(item).Name.ToString();
                    break;
                case PolicyModule.LTrainCount:
                {
                    var count = Cast<TrainCountNode>(item);
                    if (Text(count.Setting.Index) == "worlds") worlds = Int(count.Value);
                    else seed = Int(count.Value);
                    break;
                }
                case PolicyModule.LTimesteps:
                    timesteps = Long(Cast<TimestepsNode>(item).Value);
                    break;
                case PolicyModule.LNetwork:
                {
                    var net = Cast<NetworkNode>(item);
                    network = new List<int>();
                    foreach (var size in net.Sizes) network.Add(Int(size));
                    if (net.Bounded.HasValue) bounded = true;
                    break;
                }
                case PolicyModule.LLearningRate:
                {
                    var rate = Cast<LearningRateNode>(item);
                    float initial = Float(rate.Initial), hold = Float(rate.Hold), decay = Float(rate.Decay);
                    lr = new Ast.ScheduleNode(initial, initial * decay, hold);
                    break;
                }
                case PolicyModule.LScheduleItem:
                {
                    var ramp = Cast<ScheduleItemNode>(item);
                    long over = ramp.Over.HasValue ? Long(new Token(tree, tree.Child(ramp.Over.Value.Index, 1))) : 0L;
                    var schedule = new Ast.ScheduleNode(Float(ramp.From), Float(ramp.To), Float(ramp.Start), over);
                    switch (Text(ramp.Setting.Index))
                    {
                        case "entropy": entropy = schedule; break;
                        case "std_cap": stdCap = schedule; break;
                        case "command_box": commandBox = schedule; break;
                        case "family_mix": familyMix = schedule; break;
                        case "step_length": stepLength = schedule; break;
                        case "posture_depth": postureDepth = schedule; break;
                        default: pushScale = schedule; break;
                    }
                    break;
                }
                default:
                {
                    var normalize = Cast<NormalizeNode>(item);
                    obsClip = Float(normalize.Observations);
                    rewardClip = Float(normalize.Rewards);
                    break;
                }
            }
        }

        ParseException Missing(string what) => Error($"train is missing '{what}'", at);
        return new Ast.TrainNode(
            algorithm ?? throw Missing("algorithm"),
            worlds ?? throw Missing("worlds"),
            seed ?? throw Missing("seed"),
            timesteps ?? throw Missing("timesteps"),
            network ?? throw Missing("network"),
            bounded,
            lr ?? throw Missing("learning_rate"),
            entropy ?? throw Missing("entropy"),
            stdCap ?? throw Missing("std_cap"),
            obsClip ?? throw Missing("normalize observations clip"),
            rewardClip ?? throw Missing("normalize rewards clip"),
            At(at))
        {
            CommandBox = commandBox,
            FamilyMix = familyMix,
            StepLength = stepLength,
            PostureDepth = postureDepth,
            PushScale = pushScale,
        };
    }

    Ast.EvaluateNode Evaluate(EvaluateNode node)
    {
        int at = node.Span.Start;
        int? episodes = null;
        int seeds = 0;
        string? start = null;
        bool noise = false, perturb = false, deterministic = false;
        List<string>? report = null;
        var scenarios = new List<string>();
        Ast.RandomizeNode? randomize = null;

        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LEvaluateCount:
                {
                    var count = Cast<EvaluateCountNode>(item);
                    if (Text(count.Setting.Index) == "episodes") episodes = Int(count.Value);
                    else seeds = Int(count.Value);
                    break;
                }
                case PolicyModule.LRandomize: randomize = Randomize(Cast<RandomizeNode>(item)); break;
                case PolicyModule.LScenarios:
                    // PolicyParser checks the running total, so an empty repeat after a full one passes.
                    foreach (var name in Cast<ScenariosNode>(item).Names) scenarios.Add(name.Name.ToString());
                    if (scenarios.Count == 0) throw Error("scenarios needs at least one scenario name", item.Span.Start);
                    break;
                case PolicyModule.LEvaluateStart: start = Cast<EvaluateStartNode>(item).Case.ToString(); break;
                case PolicyModule.LToggle:
                {
                    var toggle = Cast<ToggleNode>(item);
                    bool on = Text(toggle.State.Index) == "on";
                    if (Text(toggle.Setting.Index) == "noise") noise = on;
                    else perturb = on;
                    break;
                }
                case PolicyModule.LDeterministic: deterministic = true; break;
                default:
                    report = new List<string>();
                    foreach (var word in Cast<ReportNode>(item).Items) report.Add(word.ToString());
                    break;
            }
        }

        ParseException Missing(string what) => Error($"evaluate is missing '{what}'", at);
        if (episodes == null && seeds == 0) throw Missing("episodes (or seeds)");
        return new Ast.EvaluateNode(episodes ?? 0, start ?? throw Missing("start"), noise, perturb, deterministic,
            report ?? throw Missing("report"), At(at))
        {
            Seeds = seeds,
            Scenarios = scenarios,
            Randomize = randomize,
        };
    }

    // ---- compose ----

    public Ast.ComposeNode Compose()
    {
        var node = SyntaxView.Cast<ComposeDocumentNode>(tree, tree.Root).Compose;
        int at = node.Span.Start;
        string? scene = null, walk = null;
        var getups = new List<Ast.ComposeSlotNode>();
        var commands = new List<Ast.ComposeCommandNode>();
        var rules = new Ast.ComposeRulesNode(1.0f, 0.5f, 1.5f, 3);

        foreach (var item in node.Items)
        {
            switch (SyntaxKinds.LocalOf(item.Kind))
            {
                case PolicyModule.LComposeScene: scene = Unquote(Cast<ComposeSceneNode>(item).Asset); break;
                case PolicyModule.LWalk: walk = Unquote(Cast<WalkNode>(item).Document); break;
                case PolicyModule.LGetup:
                {
                    var getup = Cast<GetupNode>(item);
                    string document = Unquote(getup.Document);
                    var poses = new List<string>();
                    foreach (var pose in getup.Poses) poses.Add(pose.Name.ToString());
                    getups.Add(new Ast.ComposeSlotNode(Path.GetFileNameWithoutExtension(document), document, poses, At(item.Span.Start)));
                    break;
                }
                case PolicyModule.LRules:
                {
                    // Each block starts from the values the previous one left.
                    float stand = rules.StandToWalkSeconds, hand = rules.HandOverSeconds, settle = rules.SettleCapSeconds;
                    int retries = rules.MaxRetries;
                    foreach (var rule in Cast<RulesNode>(item).Items)
                    {
                        switch (Text(rule.Setting.Index))
                        {
                            case "stand_to_walk": stand = Float(rule.Value); break;
                            case "hand_over": hand = Float(rule.Value); break;
                            case "settle_cap": settle = Float(rule.Value); break;
                            default: retries = Int(rule.Value); break;
                        }
                    }
                    rules = new Ast.ComposeRulesNode(stand, hand, settle, retries);
                    break;
                }
                default:
                    foreach (var command in Cast<CommandsNode>(item).Items)
                    {
                        var channels = new List<(string, float)>();
                        foreach (var channel in command.Channels) channels.Add((channel.Channel.ToString(), Float(channel.Value)));
                        commands.Add(new Ast.ComposeCommandNode(command.Name.ToString(), channels, At(command.Span.Start)));
                    }
                    break;
            }
        }

        if (scene is null) throw Error("compose needs `requires scene`", at);
        if (walk is null) throw Error("compose needs a `walk` policy", at);
        if (getups.Count == 0) throw Error("compose needs at least one `getup`", at);
        return new Ast.ComposeNode(node.Name.ToString(), scene, walk, getups, rules, commands, At(at));
    }

    // ---- literals ----

    /// <summary>PolicyParser.ExpectNumber.</summary>
    static float Float(NumNode node)
    {
        float value = float.Parse(node.Value.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        return node.Negative.HasValue ? -value : value;
    }

    /// <summary>PolicyParser.ExpectInt: any number that is whole, `2.0` included.</summary>
    int Int(NumNode node)
    {
        float value = Float(node);
        if (value != MathF.Floor(value)) throw Error($"Expected an integer, got {value}", node.Span.Start);
        return (int)value;
    }

    /// <summary>PolicyParser.ExpectLongWithSuffix: MotionLexer's number, then an optional glued k or M.</summary>
    long Long(Token count)
    {
        var text = count.Text;
        int length = NumberLength(text);
        if (!long.TryParse(text[..length], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            throw Error($"Expected an integer count, got '{text[..length]}'", count.Span.Start);
        var suffix = text[length..];
        if (suffix.Length == 0) return value;
        return suffix switch
        {
            "k" => value * 1_000L,
            "M" => value * 1_000_000L,
            _ => throw Error($"Unknown number suffix '{suffix}' (use k or M)", count.Span.Start + length),
        };
    }

    /// <summary>How much of <paramref name="text"/> MotionLexer.ReadNumber takes.</summary>
    static int NumberLength(ReadOnlySpan<char> text)
    {
        int i = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        }
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            int look = i + 1;
            if (look < text.Length && (text[look] == '+' || text[look] == '-')) look++;
            if (look < text.Length && char.IsAsciiDigit(text[look]))
            {
                i = look;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            }
        }
        return i;
    }

    // ---- helpers ----

    T Cast<T>(SyntaxNode node) where T : struct, ISyntaxView<T> => SyntaxView.Cast<T>(tree, node.Index);

    /// <summary>The <see cref="NumNode"/> at <paramref name="child"/> of a labeled optional group.</summary>
    NumNode NumAt(SyntaxNode group, int child) => new(tree, tree.Child(group.Index, child));

    string Text(int index) => tree.GetText(index).ToString();

    static string Unquote(Token token) => Unquote(token.ToString());

    static string Unquote(string quoted) => quoted.Substring(1, quoted.Length - 2);

    Ast.SourceSpan At(int position)
    {
        var (line, col) = source.GetLineColumn(position);
        return new Ast.SourceSpan(line, col);
    }

    ParseException Error(string message, int position)
    {
        var (line, col) = source.GetLineColumn(position);
        return new ParseException(message, line, col);
    }
}
