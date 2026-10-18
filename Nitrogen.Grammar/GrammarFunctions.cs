namespace Nitrogen.Grammar;

/// <param name="Modules">The modules with every grammar function call replaced by its expansion, ready for validation and emission.</param>
public sealed record GrammarFunctionsResult(EquatableArray<ModuleDecl> Modules, EquatableArray<GrammarDiagnostic> Diagnostics);

/// <summary>
/// Grammar functions (spec: grammar functions): the compile-time types and the expansion stage. <see cref="Expand"/> runs after
/// parsing and before validation. It type-checks every function, evaluates every call, and replaces each call with the
/// ordinary expression it evaluates to, so later stages never see a call.
/// </summary>
public static partial class GrammarFunctions
{
    public const string RuleType = "Grammar.Rule";
    public const string IntType = "Grammar.Int";
    public const string CharType = "Grammar.Char";
    public const string TextType = "Grammar.Text";
    public const string ClassType = "Grammar.Class";

    const int MaxDepth = 200;
    const int MaxNodes = 1_000_000;
    const long MaxSteps = 10_000_000;

    static readonly HashSet<string> s_builtins = new(StringComparer.Ordinal) { "char", "code", "upper", "lower", "length" };

    /// <summary>Whether a call of <paramref name="name"/> is a built-in; no grammar function may take a built-in's name.</summary>
    public static bool IsBuiltin(string name) => s_builtins.Contains(name);

    /// <param name="onExpanded">Called with each call written in a rule body and what it expanded to (null when evaluation failed); editors use it.</param>
    public static GrammarFunctionsResult Expand(IEnumerable<ModuleDecl> modules, Action<GrammarCallExpr, Expr?>? onExpanded = null)
    {
        var user = modules.ToList();
        var diagnostics = new List<GrammarDiagnostic>();
        if (!user.Any(m => m.Functions.Count > 0 || HasCall(m)))
            return new GrammarFunctionsResult(user.ToArray(), default);

        var registry = new Registry(user, diagnostics);
        foreach (var function in registry.All) new Checker(registry, function, diagnostics).CheckFunction();

        var evaluator = new Evaluator(registry);
        var expanded = user.Select(module =>
        {
            var rules = module.Rules.Select(rule => rule switch
            {
                TokenRule token => token with { Body = Inline(token.Body, module, true) },
                SyntaxRule syntax => syntax with { Body = Inline(syntax.Body, module, false) },
                ExtensibleRule extensible => extensible with
                {
                    Alternatives = extensible.Alternatives.Select(a => a with { Body = Inline(a.Body, module, false) }).ToArray(),
                },
                _ => rule,
            }).ToArray();
            var extends = module.Extends.Select(e => e with
            {
                Alternatives = e.Alternatives.Select(a => a with { Body = Inline(a.Body, module, false) }).ToArray(),
            }).ToArray();
            return module with { Rules = rules, Extends = extends };
        }).ToArray();
        return new GrammarFunctionsResult(expanded, diagnostics.ToArray());

        Expr Inline(Expr body, ModuleDecl module, bool inToken) => Map(body, expr =>
        {
            if (expr is not GrammarCallExpr call) return null;
            var expansion = evaluator.ExpandCall(call, module, inToken, diagnostics);
            onExpanded?.Invoke(call, expansion);
            if (expansion is null) return new AnyCharExpr(call.Span);
            if (!inToken && IsCharacterLevel(expansion))
                diagnostics.Add(new GrammarDiagnostic(GrammarCodes.CharacterLevelCall, GrammarSeverity.Warning,
                    $"'{call.Name.Name}' expands to adjacent single characters; in a syntax rule, trivia may separate them. Call it from a token rule",
                    call.Span, module.Name));
            return expansion;
        });
    }

    static bool HasCall(ModuleDecl module) =>
        module.Rules.Any(rule => rule switch
        {
            TokenRule token => ContainsCall(token.Body),
            SyntaxRule syntax => ContainsCall(syntax.Body),
            ExtensibleRule extensible => extensible.Alternatives.Any(a => ContainsCall(a.Body)),
            _ => false,
        }) || module.Extends.Any(e => e.Alternatives.Any(a => ContainsCall(a.Body)));

    static bool ContainsCall(Expr expr) => expr is GrammarCallExpr || GrammarExtraction.Children(expr).Any(ContainsCall);

    /// <summary>Rebuilds <paramref name="expr"/> bottom-up where <paramref name="replace"/> returns a replacement.</summary>
    static Expr Map(Expr expr, Func<Expr, Expr?> replace)
    {
        if (replace(expr) is { } replaced) return replaced;
        return expr switch
        {
            SequenceExpr s => s with { Items = s.Items.Select(i => Map(i, replace)).ToArray() },
            ChoiceExpr c => c with { Alternatives = c.Alternatives.Select(a => Map(a, replace)).ToArray() },
            LabeledExpr l => l with { Inner = Map(l.Inner, replace) },
            RepeatExpr r => r with { Inner = Map(r.Inner, replace) },
            SeparatedListExpr l => l with { Item = Map(l.Item, replace), Separator = Map(l.Separator, replace) },
            PredicateExpr p => p with { Inner = Map(p.Inner, replace) },
            _ => expr,
        };
    }

    /// <summary>A sequence somewhere in <paramref name="expr"/> with two adjacent single-character elements.</summary>
    static bool IsCharacterLevel(Expr expr)
    {
        if (expr is SequenceExpr sequence)
            for (int i = 1; i < sequence.Items.Count; i++)
                if (IsSingleCharacter(sequence.Items[i - 1]) && IsSingleCharacter(sequence.Items[i])) return true;
        return GrammarExtraction.Children(expr).Any(IsCharacterLevel);
    }

    static bool IsSingleCharacter(Expr expr) => expr is CharClassExpr or LiteralExpr { Value.Length: 1 };

    /// <summary>The functions of the modules, with resolution like rule references.</summary>
    internal sealed class Registry
    {
        readonly Dictionary<string, Dictionary<string, Function>> _byModule = new(StringComparer.Ordinal);

        public Registry(List<ModuleDecl> modules, List<GrammarDiagnostic> diagnostics)
        {
            Analysis = new GrammarAnalysis(modules);
            foreach (var module in modules)
            {
                if (_byModule.ContainsKey(module.Name)) continue;
                var functions = new Dictionary<string, Function>(StringComparer.Ordinal);
                _byModule[module.Name] = functions;
                foreach (var function in module.Functions)
                {
                    string name = function.Name.Name;
                    if (functions.ContainsKey(name) || module.Helpers.Any(h => h.Name.Name == name))
                    {
                        diagnostics.Add(new GrammarDiagnostic(GrammarCodes.DuplicateFunction, GrammarSeverity.Error,
                            $"'{name}' is already defined in module '{module.Name}'", function.Name.Span, module.Name));
                        continue;
                    }
                    if (s_builtins.Contains(name))
                    {
                        diagnostics.Add(new GrammarDiagnostic(GrammarCodes.DuplicateFunction, GrammarSeverity.Error,
                            $"'{name}' is a built-in function", function.Name.Span, module.Name));
                        continue;
                    }
                    functions[name] = new Function(module, function);
                }
            }
        }

        public GrammarAnalysis Analysis { get; }

        public IEnumerable<Function> All => _byModule.Values.SelectMany(f => f.Values);

        /// <summary>A function by name from <paramref name="from"/>: qualified, its own, or exactly one of its <c>using</c>s.</summary>
        public Function? Resolve(string name, ModuleDecl from, out string? error)
        {
            error = null;
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                if (_byModule.TryGetValue(name.Substring(0, dot), out var qualified) && qualified.TryGetValue(name.Substring(dot + 1), out var found))
                    return found;
                error = $"unknown function '{name}'";
                return null;
            }
            if (_byModule.TryGetValue(from.Name, out var own) && own.TryGetValue(name, out var local)) return local;
            var candidates = from.Usings.Select(u => u.Module).Distinct(StringComparer.Ordinal)
                .Where(m => m != from.Name && _byModule.TryGetValue(m, out var imported) && imported.ContainsKey(name))
                .Select(m => _byModule[m][name]).ToList();
            if (candidates.Count == 1) return candidates[0];
            error = candidates.Count > 1
                ? $"'{name}' is defined in both '{candidates[0].Module.Name}' and '{candidates[1].Module.Name}'"
                : $"unknown function '{name}'";
            return null;
        }

        public GrammarSymbol? ResolveRule(string name, ModuleDecl from) =>
            Analysis.FindModule(from.Name) is { } module ? Analysis.Resolve(name, default, module, null) : null;
    }

    internal sealed class Function(ModuleDecl module, GrammarFunctionDecl declaration)
    {
        public ModuleDecl Module => module;
        public GrammarFunctionDecl Declaration => declaration;
        public string Name => declaration.Name.Name;

        /// <summary>Parameter types; null when a parameter's type is invalid (reported by the checker).</summary>
        public ValueType?[] ParameterTypes { get; } =
            declaration.Parameters.Select(p => p.IsList ? null : ParameterType(p.Category.Name)).ToArray();

        /// <summary>False once the checker reported an error in the body; its calls are then not evaluated.</summary>
        public bool Valid { get; set; } = true;
    }

    /// <summary>A lexical scope of value names and their types or values.</summary>
    internal sealed class Scope<T>(Scope<T>? parent, string name, T value)
    {
        public static Scope<T>? With(Scope<T>? scope, string name, T value) => new(scope, name, value);

        public static bool TryFind(Scope<T>? scope, string name, out T value)
        {
            for (var s = scope; s is not null; s = s.Parent)
                if (s.Name == name)
                {
                    value = s.Value;
                    return true;
                }
            value = default!;
            return false;
        }

        Scope<T>? Parent => parent;
        string Name => name;
        T Value => value;
    }
}
