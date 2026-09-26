namespace Nitrogen.Grammar;

/// <summary>One syntax kind a module declares: a token rule, a non-alias syntax rule, or an alternative.</summary>
internal sealed class KindInfo
{
    public KindInfo(string name, int local, GrammarSpan span, RuleDecl? rule, Alternative? alternative,
        GrammarSymbol? point, string? implicitModule)
    {
        Name = name;
        Local = local;
        Span = span;
        Rule = rule;
        Alternative = alternative;
        Point = point;
        ImplicitModule = implicitModule;
    }

    public string Name { get; }

    public int Local { get; }

    public GrammarSpan Span { get; }

    /// <summary>The token or syntax rule, or null for an alternative.</summary>
    public RuleDecl? Rule { get; }

    public Alternative? Alternative { get; }

    /// <summary>The extension point an alternative belongs to.</summary>
    public GrammarSymbol? Point { get; }

    public string? ImplicitModule { get; }

    public string ViewName => Alternative is not null ? Alternative.Name + Point!.Rule.Name : Rule!.Name + "Node";
}

internal sealed class ModuleInfo
{
    public ModuleInfo(ModuleDecl module, string ns, IReadOnlyList<KindInfo> kinds)
    {
        Module = module;
        Namespace = ns;
        Kinds = kinds;
    }

    public ModuleDecl Module { get; }

    public string Namespace { get; }

    public IReadOnlyList<KindInfo> Kinds { get; }

    public string ShortName => CSharpText.LastSegment(Module.Name);

    public string ClassName => ShortName + "Module";

    public string KindsName => ShortName + "Kinds";

    public string VisitorName => ShortName + "Visitor";

    /// <summary>A type of this module's namespace as written from code generated for <paramref name="from"/>.</summary>
    public string TypeRef(ModuleInfo from, string typeName) =>
        Namespace == from.Namespace ? typeName : $"global::{Namespace}.{typeName}";

    /// <summary>The prefix for static members of this module's class, empty inside the class itself.</summary>
    public string ClassPrefix(ModuleInfo from) => ReferenceEquals(this, from) ? "" : TypeRef(from, ClassName) + ".";
}

/// <summary>What the emitter needs on top of a validated <see cref="GrammarAnalysis"/>.</summary>
internal sealed class EmitModel
{
    static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "Instance", "Name", "Id", "KindBase", "Kind", "GetKindName", "Register", "GetRule", "GetBinding", "Builtins", "GetSemantics", "Properties", "SymbolProperties", "Fail",
        "ToString", "Equals", "GetHashCode", "GetType", "MemberwiseClone", "ReferenceEquals",
    };

    readonly Dictionary<string, ModuleInfo> _infos = new(StringComparer.Ordinal);
    readonly IReadOnlyDictionary<string, GrammarInput>? _inputs;

    public EmitModel(GrammarAnalysis analysis, IReadOnlyDictionary<string, string> namespaces,
        IReadOnlyDictionary<string, GrammarInput>? inputs = null)
    {
        Analysis = analysis;
        _inputs = inputs;
        var modules = new List<ModuleInfo>();
        foreach (var module in analysis.PrimaryModules)
        {
            var info = new ModuleInfo(module, namespaces[module.Name], BuildKinds(module));
            _infos[module.Name] = info;
            modules.Add(info);
        }
        Modules = modules;
        Commits = new CommitAnalysis(analysis);
    }

    public GrammarAnalysis Analysis { get; }

    /// <summary>Commit points (issue 235): where the recovery pass repairs.</summary>
    public CommitAnalysis Commits { get; }

    public IReadOnlyList<ModuleInfo> Modules { get; }

    public ModuleInfo Info(ModuleDecl module) => _infos[module.Name];

    /// <summary>The file a module came from, for #line mapping of semantics C# (issue 239); null when unknown.</summary>
    public GrammarInput? InputOf(ModuleDecl module) =>
        _inputs is not null && _inputs.TryGetValue(module.Name, out var input) ? input : null;

    /// <summary>Resolves a reference that validation has already accepted.</summary>
    public GrammarSymbol Resolve(string name, ModuleDecl from, string? implicitModule) =>
        Analysis.Resolve(name, default, from, implicitModule)
        ?? throw new InvalidOperationException($"'{name}' does not resolve in module '{from.Name}'; validate before emitting");

    /// <summary>A syntax rule that is one reference or a choice of references produces no node of its own.</summary>
    public static bool IsAlias(SyntaxRule rule) =>
        rule.Body is ReferenceExpr || (rule.Body is ChoiceExpr choice && choice.Alternatives.All(a => a is ReferenceExpr));

    public IReadOnlyList<GrammarDiagnostic> Check()
    {
        var diagnostics = new List<GrammarDiagnostic>();
        var classes = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
        foreach (var info in Modules)
        {
            string className = info.Namespace + "." + info.ClassName;
            if (classes.TryGetValue(className, out var first))
                diagnostics.Add(Error(GrammarCodes.GeneratedNameCollision,
                    $"modules '{first.Module.Name}' and '{info.Module.Name}' both generate '{info.ClassName}' in namespace '{info.Namespace}'",
                    info.Module.Span, info.Module));
            else
                classes[className] = info;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in info.Kinds)
                if (!names.Add(kind.Name))
                    diagnostics.Add(Error(GrammarCodes.KindNameCollision,
                        $"'{kind.Name}' names two syntax kinds in module '{info.Module.Name}'; rename one of them", kind.Span, info.Module));

            foreach (var rule in Analysis.PrimaryRules(info.Module))
                if (rule is not TokenRule && ReservedNames.Contains(rule.Name))
                    diagnostics.Add(Error(GrammarCodes.ReservedName,
                        $"rule name '{rule.Name}' is reserved by the generated class {info.ClassName}", rule.Span, info.Module));

            diagnostics.AddRange(SemanticsWriter.Check(this, info));
        }
        return diagnostics;
    }

    List<KindInfo> BuildKinds(ModuleDecl module)
    {
        var kinds = new List<KindInfo>();
        foreach (var rule in Analysis.PrimaryRules(module))
        {
            switch (rule)
            {
                case TokenRule:
                    kinds.Add(new KindInfo(rule.Name, kinds.Count + 1, rule.Span, rule, null, null, null));
                    break;
                case SyntaxRule syntax when !IsAlias(syntax):
                    kinds.Add(new KindInfo(rule.Name, kinds.Count + 1, rule.Span, rule, null, null, null));
                    break;
                case ExtensibleRule extensible:
                {
                    var point = new GrammarSymbol(module, extensible);
                    foreach (var alternative in extensible.Alternatives)
                        kinds.Add(new KindInfo(alternative.Name, kinds.Count + 1, alternative.Span, null, alternative, point, module.Name));
                    break;
                }
            }
        }
        foreach (var extend in module.Extends)
        {
            var target = Resolve(extend.Target, module, null);
            foreach (var alternative in extend.Alternatives)
                kinds.Add(new KindInfo(alternative.Name, kinds.Count + 1, alternative.Span, null, alternative, target, target.Module.Name));
        }
        return kinds;
    }

    static GrammarDiagnostic Error(string code, string message, GrammarSpan span, ModuleDecl module) =>
        new(code, GrammarSeverity.Error, message, span, module.Name);
}
