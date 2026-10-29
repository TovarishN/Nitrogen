namespace Nitrogen.Grammar;

public static partial class GrammarFunctions
{
    /// <summary>
    /// Static checks of grammar functions and call sites: every body is checked once, used or not, so a broken function
    /// fails where it is written. Names resolve to parameters, <c>let</c>s and loop variables first, then to rules.
    /// </summary>
    internal sealed class Checker(Registry registry, Function? function, List<GrammarDiagnostic> diagnostics, ModuleDecl? site = null)
    {
        readonly ModuleDecl _module = function?.Module ?? site!;
        int _errors;

        public void CheckFunction()
        {
            var declaration = function!.Declaration;
            Scope<ValueType>? scope = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < declaration.Parameters.Count; i++)
            {
                var parameter = declaration.Parameters[i];
                if (!seen.Add(parameter.Name.Name))
                    Report(GrammarCodes.FunctionType, $"parameter '{parameter.Name.Name}' is declared twice", parameter.Name.Span);
                if (function.ParameterTypes[i] is not { } type)
                {
                    Report(GrammarCodes.FunctionType, "a grammar function parameter is Grammar.Int, Grammar.Char, Grammar.Text, Grammar.Class or Grammar.Rule",
                        parameter.Category.Span);
                    continue;
                }
                scope = Scope<ValueType>.With(scope, parameter.Name.Name, type);
            }
            scope = Lets(declaration.Lets, scope);
            CheckRule(declaration.Body, scope);
            if (_errors > 0) function.Valid = false;
        }

        /// <summary>A call in a rule body: its arguments are checked with no names in scope but rules.</summary>
        public bool CheckSite(GrammarCallExpr call)
        {
            CheckCall(call.Name, call.Arguments, null);
            return _errors == 0;
        }

        Scope<ValueType>? Lets(EquatableArray<LetBinding> lets, Scope<ValueType>? scope)
        {
            foreach (var let in lets)
                scope = Scope<ValueType>.With(scope, let.Name.Name, TypeOf(let.Value, scope) ?? ValueType.Rule);
            return scope;
        }

        void CheckRule(Expr expr, Scope<ValueType>? scope)
        {
            switch (expr)
            {
                case ReferenceExpr reference:
                    if (Scope<ValueType>.TryFind(scope, reference.Name, out var type))
                    {
                        if (!Converts(type, ValueType.Rule))
                            Report(GrammarCodes.FunctionType, $"'{reference.Name}' is {Describe(type)}, not a rule", reference.Span);
                    }
                    else if (registry.ResolveRule(reference.Name, _module) is null)
                        Report(GrammarCodes.FunctionType, UnknownName(reference.Name), reference.Span);
                    break;
                case ContextTestExpr context:
                    registry.Analysis.ResolveContext(context.Context,_module,null,(code,message,span,module)=>Report(code,message,span));
                    break;
                case ContextScopeExpr context:
                    registry.Analysis.ResolveContext(context.Context,_module,null,(code,message,span,module)=>Report(code,message,span));
                    CheckRule(context.Inner,scope);
                    break;
                case GrammarCallExpr call:
                    CheckCall(call.Name, call.Arguments, scope);
                    break;
                case IfExpr conditional:
                    Expect(conditional.Condition, ValueType.Bool, scope);
                    CheckRule(conditional.Then, scope);
                    CheckRule(conditional.Else, scope);
                    break;
                case ComprehensionExpr comprehension:
                {
                    ValueType variable;
                    if (comprehension.To is { } to)
                    {
                        Expect(comprehension.From, ValueType.Int, scope);
                        Expect(to, ValueType.Int, scope);
                        variable = ValueType.Int;
                    }
                    else
                    {
                        Expect(comprehension.From, ValueType.Text, scope);
                        variable = ValueType.Char;
                    }
                    CheckBlock(comprehension.Block, Scope<ValueType>.With(scope, comprehension.Variable.Name, variable));
                    break;
                }
                case ValueClassExpr valueClass:
                    CheckItems(valueClass.Items, scope);
                    break;
                default:
                    foreach (var child in GrammarExtraction.Children(expr)) CheckRule(child, scope);
                    break;
            }
        }

        void CheckBlock(FunctionBlock block, Scope<ValueType>? scope)
        {
            scope = Lets(block.Lets, scope);
            if (block.Where is { } where) Expect(where, ValueType.Bool, scope);
            CheckRule(block.Body, scope);
        }

        void CheckItems(EquatableArray<ValueClassItem> items, Scope<ValueType>? scope)
        {
            foreach (var item in items)
            {
                var first = TypeOf(item.First, scope);
                if (item.Last is { } last)
                {
                    CheckBound(item.First, first);
                    CheckBound(last, TypeOf(last, scope));
                }
                else if (first is { } single && single != ValueType.Int && !Converts(single, ValueType.Class))
                    Report(GrammarCodes.FunctionType, $"a class item is {CharType}, {IntType} or {ClassType}, found {Describe(single)}", item.First.Span);
            }
        }

        void CheckBound(ValueExpr bound, ValueType? type)
        {
            if (type is { } known and not (ValueType.Int or ValueType.Char))
                Report(GrammarCodes.FunctionType, $"a range bound is {CharType} or {IntType}, found {Describe(known)}", bound.Span);
        }

        void Expect(ValueExpr value, ValueType expected, Scope<ValueType>? scope)
        {
            if (TypeOf(value, scope) is { } actual && !Converts(actual, expected))
                Report(GrammarCodes.FunctionType, $"expected {Describe(expected)}, found {Describe(actual)}", value.Span);
        }

        /// <summary>The value's type, or null after reporting why it has none.</summary>
        ValueType? TypeOf(ValueExpr value, Scope<ValueType>? scope)
        {
            switch (value)
            {
                case IntValueExpr: return ValueType.Int;
                case CharValueExpr: return ValueType.Char;
                case TextValueExpr: return ValueType.Text;
                case NameValueExpr name:
                    if (Scope<ValueType>.TryFind(scope, name.Name, out var type)) return type;
                    if (registry.ResolveRule(name.Name, _module) is not null) return ValueType.RuleName;
                    Report(GrammarCodes.FunctionType, UnknownName(name.Name), name.Span);
                    return null;
                case CallValueExpr call:
                    return CheckCall(call.Name, call.Arguments, scope);
                case RuleValueExpr rule:
                    CheckBlock(rule.Block, scope);
                    return ValueType.Rule;
                case ClassValueExpr classValue:
                    CheckItems(classValue.Items, scope);
                    return ValueType.Class;
                case IfValueExpr conditional:
                {
                    Expect(conditional.Condition, ValueType.Bool, scope);
                    var then = TypeOf(conditional.Then, scope);
                    var otherwise = TypeOf(conditional.Else, scope);
                    if (then is null || otherwise is null) return null;
                    if (Unify(then.Value, otherwise.Value) is { } unified) return unified;
                    Report(GrammarCodes.FunctionType, $"the branches are {Describe(then.Value)} and {Describe(otherwise.Value)}", conditional.Span);
                    return null;
                }
                case UnaryValueExpr unary:
                {
                    var expected = unary.Operator switch { "-" => ValueType.Int, "~" => ValueType.Class, _ => ValueType.Bool };
                    Expect(unary.Operand, expected, scope);
                    return expected;
                }
                case BinaryValueExpr binary:
                    return Binary(binary, scope);
                default:
                    return null;
            }
        }

        ValueType? Binary(BinaryValueExpr binary, Scope<ValueType>? scope)
        {
            var left = TypeOf(binary.Left, scope);
            var right = TypeOf(binary.Right, scope);
            if (left is not { } l || right is not { } r) return null;
            ValueType? result = binary.Operator switch
            {
                "or" or "and" => l == ValueType.Bool && r == ValueType.Bool ? ValueType.Bool : null,
                "==" or "!=" => Comparable(l, r) ? ValueType.Bool : null,
                "<" or "<=" or ">" or ">=" => l == r && l is ValueType.Int or ValueType.Char ? ValueType.Bool : null,
                "|" or "&" => Converts(l, ValueType.Class) && Converts(r, ValueType.Class) ? ValueType.Class : null,
                "+" => (l, r) switch
                {
                    (ValueType.Int, ValueType.Int) => ValueType.Int,
                    (ValueType.Char, ValueType.Int) => ValueType.Char,
                    _ when Converts(l, ValueType.Class) && r == ValueType.Int => ValueType.Class,
                    _ => null,
                },
                "-" => (l, r) switch
                {
                    (ValueType.Int, ValueType.Int) => ValueType.Int,
                    (ValueType.Char, ValueType.Int) => ValueType.Char,
                    _ when Converts(l, ValueType.Class) && r == ValueType.Int => ValueType.Class,
                    _ when Converts(l, ValueType.Class) && Converts(r, ValueType.Class) => ValueType.Class,
                    _ => null,
                },
                _ => l == ValueType.Int && r == ValueType.Int ? ValueType.Int : null,
            };
            if (result is null)
                Report(GrammarCodes.FunctionType, $"'{binary.Operator}' does not apply to {Describe(l)} and {Describe(r)}", binary.Span);
            return result;
        }

        static bool Comparable(ValueType l, ValueType r) =>
            l == r && l != ValueType.Rule && l != ValueType.RuleName || Converts(l, ValueType.Class) && Converts(r, ValueType.Class) && l != ValueType.Char && r != ValueType.Char;

        static ValueType? Unify(ValueType a, ValueType b)
        {
            if (a == b) return a;
            if (Converts(a, ValueType.Class) && Converts(b, ValueType.Class)) return ValueType.Class;
            if (Converts(a, ValueType.Rule) && Converts(b, ValueType.Rule)) return ValueType.Rule;
            return null;
        }

        /// <summary>A built-in or grammar function call; its result type, or null after an error.</summary>
        ValueType? CheckCall(NameDecl name, EquatableArray<ValueExpr> arguments, Scope<ValueType>? scope)
        {
            var types = arguments.Select(a => TypeOf(a, scope)).ToList();
            if (s_builtins.Contains(name.Name))
            {
                var (parameter, result) = name.Name switch
                {
                    "char" => (ValueType.Int, ValueType.Char),
                    "code" => (ValueType.Char, ValueType.Int),
                    "length" => (ValueType.Text, ValueType.Int),
                    _ => (ValueType.Char, ValueType.Char),
                };
                if (arguments.Count != 1)
                {
                    Report(GrammarCodes.FunctionArgumentCount, $"'{name.Name}' takes 1 argument, found {arguments.Count}", name.Span);
                    return null;
                }
                if (types[0] is { } actual && !Converts(actual, parameter))
                    Report(GrammarCodes.FunctionArgumentType, $"'{name.Name}' expects {Describe(parameter)}, found {Describe(actual)}", arguments[0].Span);
                return result;
            }
            var target = registry.Resolve(name.Name, _module, out string? error);
            if (target is null)
            {
                Report(GrammarCodes.UnknownFunction, error!, name.Span);
                return null;
            }
            var parameters = target.Declaration.Parameters;
            if (parameters.Count != arguments.Count)
            {
                Report(GrammarCodes.FunctionArgumentCount,
                    $"'{name.Name}' takes {parameters.Count} argument{(parameters.Count == 1 ? "" : "s")}, found {arguments.Count}", name.Span);
                return ValueType.Rule;
            }
            for (int i = 0; i < arguments.Count; i++)
                if (types[i] is { } actual && target.ParameterTypes[i] is { } expected && !Converts(actual, expected))
                    Report(GrammarCodes.FunctionArgumentType,
                        $"argument '{parameters[i].Name.Name}' of '{name.Name}' expects {Describe(expected)}, found {Describe(actual)}", arguments[i].Span);
            return ValueType.Rule;
        }

        static string UnknownName(string name) => $"'{name}' is not a parameter, a let, a variable or a rule";

        void Report(string code, string message, GrammarSpan span)
        {
            _errors++;
            diagnostics.Add(new GrammarDiagnostic(code, GrammarSeverity.Error, message, span, _module.Name));
        }
    }
}
