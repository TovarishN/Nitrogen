using System.Runtime.CompilerServices;

namespace Nitrogen.Grammar;

public static partial class GrammarFunctions
{
    /// <summary>
    /// Evaluates calls. Calls are memoized on their argument values, so a function produces one shared expression per
    /// distinct argument list. Produced rule references are qualified (<c>Module.Rule</c>), which makes a result valid
    /// wherever it is inlined. Depth, node and step limits keep evaluation total.
    /// </summary>
    internal sealed class Evaluator(Registry registry)
    {
        readonly Dictionary<string, Expr> _memo = new(StringComparer.Ordinal);
        readonly List<string> _stack = new();
        int _nodes;
        long _steps;

        /// <summary>The call's expansion with the call's span, or null after reporting why it has none.</summary>
        public Expr? ExpandCall(GrammarCallExpr call, ModuleDecl site, bool inToken, List<GrammarDiagnostic> diagnostics)
        {
            if (!new Checker(registry, null, diagnostics, site).CheckSite(call)) return null;
            _nodes = 0;
            _steps = 0;
            try
            {
                var result = Build(call, null, site);
                if (inToken) result = new Factoring().Factor(result);
                return Respan(result, call.Span);
            }
            catch (EvaluationException error)
            {
                if (error.Code is { } code)
                    diagnostics.Add(new GrammarDiagnostic(code, GrammarSeverity.Error,
                        error.Chain.Length > 0 ? $"{error.Message} (in {error.Chain})" : error.Message, call.Span, site.Name));
                return null;
            }
            finally
            {
                _stack.Clear();
            }
        }

        Expr Call(Function function, List<Value> arguments)
        {
            if (!function.Valid) throw new EvaluationException(null, "", ""); // its body error is already reported
            Scope<Value>? scope = null;
            for (int i = 0; i < arguments.Count; i++)
            {
                Value value = function.ParameterTypes[i] switch
                {
                    ValueType.Class => new ClassValue(AsClass(arguments[i])),
                    ValueType.Rule => new RuleValue(AsRule(arguments[i])),
                    _ => arguments[i],
                };
                arguments[i] = value;
                scope = Scope<Value>.With(scope, function.Declaration.Parameters[i].Name.Name, value);
            }
            string key = function.Module.Name + "." + function.Name + "(" + string.Join(",", arguments.Select(a => a.Key)) + ")";
            if (_memo.TryGetValue(key, out var known)) return known;
            if (_stack.Count >= MaxDepth) throw Limit($"grammar function calls nest deeper than {MaxDepth}");
            _stack.Add(function.Name);
            scope = Lets(function.Declaration.Lets, scope, function.Module);
            var result = Build(function.Declaration.Body, scope, function.Module);
            _stack.RemoveAt(_stack.Count - 1);
            _memo[key] = result;
            return result;
        }

        Scope<Value>? Lets(EquatableArray<LetBinding> lets, Scope<Value>? scope, ModuleDecl module)
        {
            foreach (var let in lets) scope = Scope<Value>.With(scope, let.Name.Name, Eval(let.Value, scope, module));
            return scope;
        }

        Function Target(NameDecl name, ModuleDecl module) =>
            registry.Resolve(name.Name, module, out string? error) ?? throw Error(error!);

        // ---- rules ----

        NameDecl QualifiedContext(NameDecl name,ModuleDecl module)
        {
            var resolved=registry.Analysis.ResolveContext(name,module,null) ?? throw Error($"unknown context '{name.Name}'");
            return name with { Name=resolved.Module.Name+"."+resolved.Name.Name };
        }

        Expr Build(Expr expr, Scope<Value>? scope, ModuleDecl module)
        {
            if (++_nodes > MaxNodes) throw Limit($"the expansion exceeds {MaxNodes} nodes");
            switch (expr)
            {
                case LiteralExpr or CharClassExpr or AnyCharExpr:
                    return expr;
                case ReferenceExpr reference:
                    if (Scope<Value>.TryFind(scope, reference.Name, out var value)) return AsRule(value);
                    return new ReferenceExpr((registry.ResolveRule(reference.Name, module) ?? throw Error($"unknown rule '{reference.Name}'")).Key, default);
                case ContextTestExpr context:
                    return context with { Context = QualifiedContext(context.Context,module) };
                case ContextScopeExpr context:
                    return context with { Context = QualifiedContext(context.Context,module), Inner = Build(context.Inner,scope,module) };
                case LabeledExpr labeled:
                    return new LabeledExpr(labeled.Label, Build(labeled.Inner, scope, module), default);
                case RepeatExpr repeat:
                    return new RepeatExpr(repeat.Kind, Build(repeat.Inner, scope, module), default);
                case SeparatedListExpr list:
                    return new SeparatedListExpr(Build(list.Item, scope, module), Build(list.Separator, scope, module), list.AtLeastOne, default);
                case PredicateExpr predicate:
                    return new PredicateExpr(predicate.Kind, Build(predicate.Inner, scope, module), default);
                case SequenceExpr sequence:
                    return Sequence(sequence.Items.Select(i => Build(i, scope, module)).ToList());
                case ChoiceExpr choice:
                    return Choice(choice.Alternatives.Select(a => Build(a, scope, module)).ToList());
                case GrammarCallExpr call:
                    return Call(Target(call.Name, module), call.Arguments.Select(a => Eval(a, scope, module)).ToList());
                case IfExpr conditional:
                    return Build(AsBool(Eval(conditional.Condition, scope, module)) ? conditional.Then : conditional.Else, scope, module);
                case ComprehensionExpr comprehension:
                {
                    var items = new List<Expr>();
                    foreach (var element in Elements(comprehension, scope, module))
                    {
                        if (++_steps > MaxSteps) throw Limit($"comprehensions take more than {MaxSteps} steps");
                        if (Block(comprehension.Block, Scope<Value>.With(scope, comprehension.Variable.Name, element), module) is { } item)
                            items.Add(item);
                    }
                    return comprehension.IsChoice ? Choice(items) : Sequence(items);
                }
                case ValueClassExpr valueClass:
                {
                    var set = Items(valueClass.Items, valueClass.Negated, scope, module);
                    if (set.IsEmpty) throw Error("an empty class matches nothing");
                    return set.ToClass(default);
                }
                default:
                    throw Error("this expression cannot appear in a grammar function");
            }
        }

        IEnumerable<Value> Elements(ComprehensionExpr comprehension, Scope<Value>? scope, ModuleDecl module)
        {
            var from = Eval(comprehension.From, scope, module);
            if (comprehension.To is null)
            {
                foreach (char c in AsText(from)) yield return new CharValue(c);
                yield break;
            }
            long first = AsInt(from), last = AsInt(Eval(comprehension.To, scope, module));
            if (last - first >= MaxSteps) throw Limit($"comprehensions take more than {MaxSteps} steps");
            for (long i = first; i <= last; i++) yield return new IntValue(i);
        }

        /// <summary>The block's rule, or null when its <c>where</c> condition is false.</summary>
        Expr? Block(FunctionBlock block, Scope<Value>? scope, ModuleDecl module)
        {
            scope = Lets(block.Lets, scope, module);
            if (block.Where is { } where && !AsBool(Eval(where, scope, module))) return null;
            return Build(block.Body, scope, module);
        }

        Expr Sequence(List<Expr> items)
        {
            items.RemoveAll(i => i is SequenceExpr { Items.Count: 0 });
            return items.Count == 1 ? items[0] : new SequenceExpr(items.ToArray(), default);
        }

        Expr Choice(List<Expr> alternatives) => alternatives.Count switch
        {
            0 => throw Error("'choice' produced no alternatives"),
            1 => alternatives[0],
            _ => new ChoiceExpr(alternatives.ToArray(), default),
        };

        // ---- values ----

        Value Eval(ValueExpr value, Scope<Value>? scope, ModuleDecl module)
        {
            switch (value)
            {
                case IntValueExpr i: return new IntValue(i.Value);
                case CharValueExpr c: return new CharValue(c.Value);
                case TextValueExpr t: return new TextValue(t.Value);
                case NameValueExpr name:
                    if (Scope<Value>.TryFind(scope, name.Name, out var bound)) return bound;
                    return new RuleNameValue(registry.ResolveRule(name.Name, module) ?? throw Error($"unknown name '{name.Name}'"));
                case CallValueExpr call:
                {
                    var arguments = call.Arguments.Select(a => Eval(a, scope, module)).ToList();
                    return call.Name.Name switch
                    {
                        "char" => CharOf(AsInt(arguments[0])),
                        "code" => new IntValue(AsChar(arguments[0])),
                        "upper" => new CharValue(char.ToUpperInvariant(AsChar(arguments[0]))),
                        "lower" => new CharValue(char.ToLowerInvariant(AsChar(arguments[0]))),
                        "length" => new IntValue(AsText(arguments[0]).Length),
                        _ => new RuleValue(Call(Target(call.Name, module), arguments)),
                    };
                }
                case RuleValueExpr rule:
                    return new RuleValue(Block(rule.Block, scope, module) ?? new SequenceExpr(default, default));
                case ClassValueExpr classValue:
                    return new ClassValue(Items(classValue.Items, classValue.Negated, scope, module));
                case IfValueExpr conditional:
                    return Eval(AsBool(Eval(conditional.Condition, scope, module)) ? conditional.Then : conditional.Else, scope, module);
                case UnaryValueExpr unary:
                {
                    var operand = Eval(unary.Operand, scope, module);
                    return unary.Operator switch
                    {
                        "-" => new IntValue(Checked(() => -AsInt(operand))),
                        "~" => new ClassValue(AsClass(operand).Complement()),
                        _ => new BoolValue(!AsBool(operand)),
                    };
                }
                case BinaryValueExpr binary:
                    return Binary(binary, scope, module);
                default:
                    throw Error("unsupported value");
            }
        }

        Value Binary(BinaryValueExpr binary, Scope<Value>? scope, ModuleDecl module)
        {
            var left = Eval(binary.Left, scope, module);
            switch (binary.Operator)
            {
                case "or": return AsBool(left) ? left : new BoolValue(AsBool(Eval(binary.Right, scope, module)));
                case "and": return !AsBool(left) ? left : new BoolValue(AsBool(Eval(binary.Right, scope, module)));
            }
            var right = Eval(binary.Right, scope, module);
            switch (binary.Operator)
            {
                case "==": return new BoolValue(Same(left, right));
                case "!=": return new BoolValue(!Same(left, right));
                case "<" or "<=" or ">" or ">=":
                {
                    long l = left is CharValue lc ? lc.Character : AsInt(left), r = right is CharValue rc ? rc.Character : AsInt(right);
                    return new BoolValue(binary.Operator switch { "<" => l < r, "<=" => l <= r, ">" => l > r, _ => l >= r });
                }
                case "|": return new ClassValue(AsClass(left).Union(AsClass(right)));
                case "&": return new ClassValue(AsClass(left).Intersect(AsClass(right)));
                case "+" or "-":
                {
                    long sign = binary.Operator == "+" ? 1 : -1;
                    if (left is IntValue li && right is IntValue ri) return new IntValue(Checked(() => li.Number + sign * ri.Number));
                    if (left is CharValue c && right is IntValue offset)
                    {
                        long moved = c.Character + sign * offset.Number;
                        return moved is >= 0 and <= char.MaxValue ? new CharValue((char)moved) : throw Error("the character is outside U+0000–U+FFFF");
                    }
                    if (right is IntValue shift) return new ClassValue(AsClass(left).Shift(Checked(() => sign * shift.Number)));
                    return new ClassValue(AsClass(left).Except(AsClass(right)));
                }
                default:
                {
                    long l = AsInt(left), r = AsInt(right);
                    return new IntValue(binary.Operator switch
                    {
                        "*" => Checked(() => l * r),
                        "/" => r == 0 ? throw Error("division by zero") : l / r,
                        "%" => r == 0 ? throw Error("division by zero") : l % r,
                        _ => Power(l, r),
                    });
                }
            }
        }

        CharValue CharOf(long code) =>
            code is >= 0 and <= char.MaxValue ? new CharValue((char)code) : throw Error($"char({code}) is outside U+0000–U+FFFF");

        bool Same(Value left, Value right) =>
            left.GetType() == right.GetType() ? left.Key == right.Key : AsClass(left).Key == AsClass(right).Key;

        long Power(long value, long exponent)
        {
            if (exponent < 0) throw Error("a negative exponent");
            long result = 1;
            for (long i = 0; i < exponent; i++) result = Checked(() => result * value);
            return result;
        }

        long Checked(Func<long> compute)
        {
            try
            {
                return checked(compute());
            }
            catch (OverflowException)
            {
                throw Error("integer overflow");
            }
        }

        CodeSet Items(EquatableArray<ValueClassItem> items, bool negated, Scope<Value>? scope, ModuleDecl module)
        {
            var set = CodeSet.Empty;
            foreach (var item in items)
            {
                var first = Eval(item.First, scope, module);
                if (item.Last is { } lastValue)
                    set = set.Union(CodeSet.Of(new[] { (Bound(first), Bound(Eval(lastValue, scope, module))) }));
                else
                    set = set.Union(first is IntValue code ? CodeSet.Of(new[] { (code.Number, code.Number) }) : AsClass(first));
            }
            return negated ? set.Complement() : set;
        }

        long Bound(Value value) => value is CharValue c ? c.Character : AsInt(value);

        // ---- conversions ----

        long AsInt(Value value) => value is IntValue i ? i.Number : throw Error("expected " + IntType);

        bool AsBool(Value value) => value is BoolValue b ? b.Truth : throw Error("expected a condition");

        char AsChar(Value value) => value is CharValue c ? c.Character : throw Error("expected " + CharType);

        string AsText(Value value) => value is TextValue t ? t.Text : throw Error("expected " + TextType);

        CodeSet AsClass(Value value) => value switch
        {
            ClassValue c => c.Set,
            CharValue c => CodeSet.Of(c.Character),
            RuleNameValue name => ClassOfRule(name.Symbol, new HashSet<string>(StringComparer.Ordinal)),
            _ => throw Error("expected " + ClassType),
        };

        CodeSet ClassOfRule(GrammarSymbol symbol, HashSet<string> visiting)
        {
            if (symbol.Rule is not TokenRule token) throw Error($"'{symbol.Rule.Name}' is not a character-class token rule");
            if (!visiting.Add(symbol.Key)) throw Error($"token rule '{symbol.Rule.Name}' refers to itself");
            try
            {
                return ClassOfBody(token.Body, symbol.Module, visiting) ?? throw Error($"token rule '{symbol.Rule.Name}' is not a character class");
            }
            finally
            {
                visiting.Remove(symbol.Key);
            }
        }

        CodeSet? ClassOfBody(Expr body, ModuleDecl module, HashSet<string> visiting)
        {
            switch (body)
            {
                case CharClassExpr c:
                    return CodeSet.Of(c);
                case ChoiceExpr choice:
                {
                    var set = CodeSet.Empty;
                    foreach (var alternative in choice.Alternatives)
                    {
                        if (ClassOfBody(alternative, module, visiting) is not { } part) return null;
                        set = set.Union(part);
                    }
                    return set;
                }
                case ReferenceExpr reference:
                    return registry.ResolveRule(reference.Name, module) is { } target ? ClassOfRule(target, visiting) : null;
                default:
                    return null;
            }
        }

        Expr AsRule(Value value) => value switch
        {
            RuleValue r => r.Rule,
            RuleNameValue name => new ReferenceExpr(name.Symbol.Key, default),
            CharValue c => CodeSet.Of(c.Character).ToClass(default),
            ClassValue { Set.IsEmpty: true } => throw Error("an empty class matches nothing"),
            ClassValue c => c.Set.ToClass(default),
            TextValue { Text.Length: 0 } => new SequenceExpr(default, default),
            TextValue t => new LiteralExpr(t.Text, default),
            _ => throw Error("expected " + RuleType),
        };

        EvaluationException Error(string message) => new(GrammarCodes.EvaluationError, message, string.Join(" → ", _stack));

        EvaluationException Limit(string message) => new(GrammarCodes.EvaluationLimit, message, string.Join(" → ", _stack));
    }

    /// <summary>A failed evaluation; a null <see cref="Code"/> means the cause is already reported.</summary>
    internal sealed class EvaluationException(string? code, string message, string chain) : Exception(message)
    {
        public string? Code => code;
        public string Chain => chain;
    }

    /// <summary>
    /// Choice factoring for token-rule expansions: <c>[a] S / [b] S</c> becomes <c>[ab] S</c> when <c>S</c> is structurally
    /// equal and every alternative between them starts with characters disjoint from <c>[b]</c>, so ordered choice cannot
    /// pick differently. Nested sequences and choices are flattened first.
    /// </summary>
    internal sealed class Factoring
    {
        readonly Dictionary<Expr, Expr> _done = new(ReferenceComparer.Instance);
        readonly Dictionary<Expr, int> _ids = new(ReferenceComparer.Instance);
        readonly Dictionary<string, int> _interned = new(StringComparer.Ordinal);

        public Expr Factor(Expr expr)
        {
            if (_done.TryGetValue(expr, out var known)) return known;
            Expr result = expr switch
            {
                SequenceExpr s => Flat(s.Items.Select(Factor), isChoice: false) is var items && items.Count == 1
                    ? items[0] : new SequenceExpr(items.ToArray(), default),
                ChoiceExpr c => Group(Flat(c.Alternatives.Select(Factor), isChoice: true)),
                ContextScopeExpr c => c with { Inner = Factor(c.Inner) },
                LabeledExpr l => new LabeledExpr(l.Label, Factor(l.Inner), default),
                RepeatExpr r => new RepeatExpr(r.Kind, Factor(r.Inner), default),
                SeparatedListExpr l => new SeparatedListExpr(Factor(l.Item), Factor(l.Separator), l.AtLeastOne, default),
                PredicateExpr p => new PredicateExpr(p.Kind, Factor(p.Inner), default),
                _ => expr,
            };
            _done[expr] = result;
            return result;
        }

        static List<Expr> Flat(IEnumerable<Expr> items, bool isChoice) => items.SelectMany(i => isChoice
            ? i is ChoiceExpr c ? c.Alternatives.AsEnumerable() : new[] { i }
            : i is SequenceExpr s ? s.Items.AsEnumerable() : new[] { i }).ToList();

        Expr Group(List<Expr> alternatives)
        {
            var entries = new List<(CodeSet? Head, int TailId, Expr? Tail, Expr Original)>();
            foreach (var alternative in alternatives)
            {
                var split = Split(alternative);
                if (split is { } parts)
                {
                    int rest = parts.Tail is null ? -1 : Id(parts.Tail);
                    int merged = -1;
                    for (int k = entries.Count - 1; k >= 0; k--)
                    {
                        if (entries[k].Head is { } head && entries[k].TailId == rest)
                        {
                            merged = k;
                            break;
                        }
                        if (entries[k].Head is not { } other || !other.Intersect(parts.Head).IsEmpty) break;
                    }
                    if (merged >= 0)
                    {
                        var entry = entries[merged];
                        entries[merged] = (entry.Head!.Union(parts.Head), entry.TailId, entry.Tail, entry.Original);
                        continue;
                    }
                    entries.Add((parts.Head, rest, parts.Tail, alternative));
                }
                else entries.Add((null, 0, null, alternative));
            }
            var result = entries.Select(e => e.Head is not { } head ? e.Original : Join(head.ToClass(default), e.Tail)).ToList();
            return result.Count == 1 ? result[0] : new ChoiceExpr(result.ToArray(), default);
        }

        static Expr Join(Expr head, Expr? rest) => rest switch
        {
            null => head,
            SequenceExpr s => new SequenceExpr(new[] { head }.Concat(s.Items).ToArray(), default),
            _ => new SequenceExpr(new[] { head, rest }, default),
        };

        static (CodeSet Head, Expr? Tail)? Split(Expr alternative)
        {
            switch (alternative)
            {
                case CharClassExpr { Negated: false } c:
                    return (CodeSet.Of(c), null);
                case LiteralExpr { Value.Length: 1 } l:
                    return (CodeSet.Of(l.Value[0]), null);
                case SequenceExpr s when s.Items.Count > 0 && Split(s.Items[0]) is { Tail: null } first:
                    return (first.Head, s.Items.Count == 2 ? s.Items[1] : new SequenceExpr(s.Items.Skip(1).ToArray(), default));
                default:
                    return null;
            }
        }

        /// <summary>Equal structure, equal id (hash-consing without spans).</summary>
        int Id(Expr expr)
        {
            if (_ids.TryGetValue(expr, out int id)) return id;
            string key = expr switch
            {
                LiteralExpr l => "L" + l.Value.Length + ":" + l.Value,
                CharClassExpr c => "C" + (c.Negated ? "^" : "") + CodeSet.Of(c).Key,
                AnyCharExpr => "A",
                ReferenceExpr r => "R" + r.Name,
                SequenceExpr s => "S(" + string.Join(",", s.Items.Select(Id)) + ")",
                ChoiceExpr c => "O(" + string.Join(",", c.Alternatives.Select(Id)) + ")",
                ContextTestExpr c => "CTX"+c.Context.Name,
                ContextScopeExpr c => "WITH"+c.Context.Name+c.Value+Id(c.Inner),
                LabeledExpr l => "B" + l.Label + ":" + Id(l.Inner),
                RepeatExpr r => "P" + (int)r.Kind + ":" + Id(r.Inner),
                SeparatedListExpr l => "T" + (l.AtLeastOne ? "+" : "*") + Id(l.Item) + "," + Id(l.Separator),
                PredicateExpr p => "D" + (int)p.Kind + ":" + Id(p.Inner),
                _ => "X" + RuntimeHelpers.GetHashCode(expr),
            };
            if (!_interned.TryGetValue(key, out id)) _interned[key] = id = _interned.Count;
            return _ids[expr] = id;
        }
    }

    /// <summary>Gives every node of an expansion the call's span, keeping shared nodes shared.</summary>
    static Expr Respan(Expr expr, GrammarSpan span)
    {
        var done = new Dictionary<Expr, Expr>(ReferenceComparer.Instance);
        return Visit(expr);

        Expr Visit(Expr e)
        {
            if (done.TryGetValue(e, out var known)) return known;
            Expr result = e switch
            {
                SequenceExpr s => new SequenceExpr(s.Items.Select(Visit).ToArray(), span),
                ChoiceExpr c => new ChoiceExpr(c.Alternatives.Select(Visit).ToArray(), span),
                ContextTestExpr c => c with { Span = span },
                ContextScopeExpr c => c with { Inner = Visit(c.Inner), Span = span },
                LabeledExpr l => new LabeledExpr(l.Label, Visit(l.Inner), span),
                RepeatExpr r => new RepeatExpr(r.Kind, Visit(r.Inner), span),
                SeparatedListExpr l => new SeparatedListExpr(Visit(l.Item), Visit(l.Separator), l.AtLeastOne, span),
                PredicateExpr p => new PredicateExpr(p.Kind, Visit(p.Inner), span),
                LiteralExpr l => new LiteralExpr(l.Value, span),
                CharClassExpr c => new CharClassExpr(c.Ranges, c.Negated, span),
                AnyCharExpr => new AnyCharExpr(span),
                ReferenceExpr r => new ReferenceExpr(r.Name, span),
                _ => e,
            };
            return done[e] = result;
        }
    }

    sealed class ReferenceComparer : IEqualityComparer<Expr>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(Expr? x, Expr? y) => ReferenceEquals(x, y);

        public int GetHashCode(Expr obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
