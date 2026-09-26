namespace Nitrogen.Grammar;

internal sealed class GrammarSymbol
{
    public GrammarSymbol(ModuleDecl module, RuleDecl rule)
    {
        Module = module;
        Rule = rule;
    }

    public ModuleDecl Module { get; }

    public RuleDecl Rule { get; }

    public string Key => Module.Name + "." + Rule.Name;
}

/// <summary>
/// An alternative of an extension point: the module it is written in, and the module that
/// unqualified references may also resolve into (the point's own module).
/// </summary>
internal sealed class PointAlternative
{
    public PointAlternative(Alternative alternative, ModuleDecl module, string implicitModule)
    {
        Alternative = alternative;
        Module = module;
        ImplicitModule = implicitModule;
    }

    public Alternative Alternative { get; }

    public ModuleDecl Module { get; }

    public string ImplicitModule { get; }
}

internal delegate void GrammarReport(string code, string message, GrammarSpan span, ModuleDecl module);

/// <summary>
/// The semantic model of a set of modules, shared by the validator and the emitter: modules and
/// rules (first declaration wins), every extension point's alternatives across all modules, name
/// resolution, and nullability.
/// </summary>
internal sealed class GrammarAnalysis
{
    readonly List<ModuleDecl> _modules;
    readonly Dictionary<string, ModuleDecl> _byName = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, RuleDecl>> _rules = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<PointAlternative>> _points = new(StringComparer.Ordinal);
    readonly List<string> _pointOrder = new();
    readonly HashSet<string> _nullable = new(StringComparer.Ordinal);

    public GrammarAnalysis(IEnumerable<ModuleDecl> modules, GrammarReport? report = null)
    {
        _modules = modules.ToList();
        foreach (var module in _modules) Register(module, report);
        CollectPoints();
        ComputeNullable();
    }

    public IEnumerable<ModuleDecl> PrimaryModules => _modules.Where(m => ReferenceEquals(_byName[m.Name], m));

    public IEnumerable<RuleDecl> PrimaryRules(ModuleDecl module) =>
        module.Rules.Where(r => ReferenceEquals(_rules[module.Name][r.Name], r));

    public bool HasModule(string name) => _byName.ContainsKey(name);

    public ModuleDecl? FindModule(string name) => _byName.TryGetValue(name, out var module) ? module : null;

    public IReadOnlyList<string> PointKeys => _pointOrder;

    public IReadOnlyList<PointAlternative> Point(string key) =>
        _points.TryGetValue(key, out var list) ? list : (IReadOnlyList<PointAlternative>)Array.Empty<PointAlternative>();

    public GrammarSymbol? Resolve(string name, GrammarSpan span, ModuleDecl from, string? implicitModule, GrammarReport? report = null)
    {
        int dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            string moduleName = name.Substring(0, dot), ruleName = name.Substring(dot + 1);
            if (!_byName.TryGetValue(moduleName, out var module))
            {
                report?.Invoke(GrammarCodes.UnknownModule, $"unknown module '{moduleName}'", span, from);
                return null;
            }
            if (_rules[moduleName].TryGetValue(ruleName, out var qualified)) return new GrammarSymbol(module, qualified);
            report?.Invoke(GrammarCodes.UndefinedRule, $"module '{moduleName}' has no rule '{ruleName}'", span, from);
            return null;
        }

        if (_rules[from.Name].TryGetValue(name, out var local)) return new GrammarSymbol(from, local);

        var found = new List<GrammarSymbol>();
        foreach (string imported in ImportedModules(from, implicitModule))
            if (_rules[imported].TryGetValue(name, out var rule))
                found.Add(new GrammarSymbol(_byName[imported], rule));
        if (found.Count == 1) return found[0];
        if (found.Count > 1)
            report?.Invoke(GrammarCodes.AmbiguousReference,
                $"'{name}' is defined in both '{found[0].Module.Name}' and '{found[1].Module.Name}'", span, from);
        else
            report?.Invoke(GrammarCodes.UndefinedRule, $"undefined rule '{name}'", span, from);
        return null;
    }

    public bool IsNullable(Expr expr, ModuleDecl module, string? implicitModule) => expr switch
    {
        SequenceExpr s => s.Items.All(i => IsNullable(i, module, implicitModule)),
        ChoiceExpr c => c.Alternatives.Any(a => IsNullable(a, module, implicitModule)),
        LabeledExpr l => IsNullable(l.Inner, module, implicitModule),
        RepeatExpr r => r.Kind != RepeatKind.OneOrMore || IsNullable(r.Inner, module, implicitModule),
        SeparatedListExpr l => !l.AtLeastOne || IsNullable(l.Item, module, implicitModule),
        PredicateExpr => true,
        ReferenceExpr r => Resolve(r.Name, r.Span, module, implicitModule) is { } symbol && _nullable.Contains(symbol.Key),
        _ => false,
    };

    /// <summary>An alternative is postfix when its first element is the extension point itself.</summary>
    public bool IsPostfix(PointAlternative p, string pointKey)
    {
        var first = p.Alternative.Body;
        if (first is SequenceExpr sequence) first = sequence.Items[0];
        if (first is LabeledExpr labeled) first = labeled.Inner;
        return first is ReferenceExpr reference
            && Resolve(reference.Name, reference.Span, p.Module, p.ImplicitModule)?.Key == pointKey;
    }

    public bool IsPostfix(Alternative alternative, ModuleDecl module, string implicitModule, string pointKey) =>
        IsPostfix(new PointAlternative(alternative, module, implicitModule), pointKey);

    void Register(ModuleDecl module, GrammarReport? report)
    {
        if (_byName.ContainsKey(module.Name))
        {
            report?.Invoke(GrammarCodes.DuplicateModule, $"module '{module.Name}' is declared twice", module.Span, module);
            return;
        }
        _byName[module.Name] = module;
        var rules = new Dictionary<string, RuleDecl>(StringComparer.Ordinal);
        foreach (var rule in module.Rules)
        {
            if (rules.ContainsKey(rule.Name))
                report?.Invoke(GrammarCodes.DuplicateRule, $"rule '{rule.Name}' is declared twice", rule.Span, module);
            else
                rules[rule.Name] = rule;
        }
        _rules[module.Name] = rules;
    }

    void CollectPoints()
    {
        foreach (var module in PrimaryModules)
        {
            foreach (var rule in PrimaryRules(module))
                if (rule is ExtensibleRule extensible)
                    MutablePoint(module.Name + "." + extensible.Name)
                        .AddRange(extensible.Alternatives.Select(a => new PointAlternative(a, module, module.Name)));
            foreach (var extend in module.Extends)
                if (Resolve(extend.Target, extend.Span, module, null) is { Rule: ExtensibleRule } target)
                    MutablePoint(target.Key)
                        .AddRange(extend.Alternatives.Select(a => new PointAlternative(a, module, target.Module.Name)));
        }
    }

    List<PointAlternative> MutablePoint(string key)
    {
        if (!_points.TryGetValue(key, out var list))
        {
            _points[key] = list = new List<PointAlternative>();
            _pointOrder.Add(key);
        }
        return list;
    }

    void ComputeNullable()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var module in PrimaryModules)
                foreach (var rule in PrimaryRules(module))
                {
                    string key = module.Name + "." + rule.Name;
                    if (_nullable.Contains(key) || !RuleNullable(key, rule, module)) continue;
                    _nullable.Add(key);
                    changed = true;
                }
        }
    }

    bool RuleNullable(string key, RuleDecl rule, ModuleDecl module) => rule switch
    {
        TokenRule t => IsNullable(t.Body, module, null),
        SyntaxRule s => IsNullable(s.Body, module, null),
        ExtensibleRule => Point(key).Any(p => !IsPostfix(p, key) && IsNullable(p.Alternative.Body, p.Module, p.ImplicitModule)),
        _ => false,
    };

    IEnumerable<string> ImportedModules(ModuleDecl from, string? implicitModule)
    {
        if (implicitModule is not null && implicitModule != from.Name) yield return implicitModule;
        foreach (var u in from.Usings)
            if (_byName.ContainsKey(u.Module) && u.Module != implicitModule && u.Module != from.Name)
                yield return u.Module;
    }
}
