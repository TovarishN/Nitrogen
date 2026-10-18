using System.Globalization;
using System.Text;
using Nitrogen.Grammar;
using Nitrogen.Semantic;

namespace Nitrogen.Ngr;

internal sealed class NgrMappingException(string message, GrammarSpan span) : Exception(message)
{
    public GrammarSpan Span { get; } = span;
}

/// <summary>
/// Projects the typed HIR that Nitrogen.ngr's lowers clauses produce (<see cref="GrammarSemantics"/>) to the
/// GrammarModel, following the bootstrap parser's span rules exactly. It reads operations, text, optionals,
/// sequences and their origins, never the syntax tree. An operation's origin is its rule node; an inline
/// text argument's is its token; a <c>lowers text</c> wrapper's is the wrapper node.
/// </summary>
internal static class NgrProjector
{
    public static GrammarFile File(HirNode root)
    {
        var modules = new List<ModuleDecl>();
        var file = Expect(root, "File");
        foreach (var module in Items(file.Arguments[0])) modules.Add(Module(module));
        var languages = new List<LanguageDecl>();
        if (Value(file.Arguments[1]) is { } declaration)
        {
            var language = Expect(declaration, "LanguageDeclaration");
            var settings = new List<LanguageSetting>();
            foreach (var item in Items(language.Arguments[1]))
            {
                var setting = (HirOperation)item;
                var args = setting.Arguments;
                string key = Text(args[0]);
                var values = new List<NameDecl>();
                if (Kind(setting) is "LanguageListSetting" or "LanguageUsingSetting")
                    foreach (var value in Items(args[1]))
                        values.Add(new NameDecl(key == "usings" ? Text(value) : Unquote(Text(value)), Span(value)));
                else
                {
                    values.Add(new NameDecl(Text(args[1]), Span(args[1])));
                    if (Kind(setting) == "LanguagePresentationSetting")
                        values.Add(new NameDecl(Unquote(Text(args[2])), Span(args[2])));
                }
                settings.Add(new LanguageSetting(key, values.ToArray(), Span(setting)));
            }
            languages.Add(LanguageDeclarations.Build(Text(language.Arguments[0]), settings.ToArray(), Span(language)));
        }
        return new GrammarFile(modules.ToArray(), languages.ToArray());
    }

    static QuoteParameter QuoteParameter(HirNode node)
    {
        var parts = Expect(node, "QuoteParameter").Arguments;
        return new(Name(parts[0]), Name(parts[1]), Value(parts[2]) is not null, Span(node));
    }

    static QuoteText Quote(HirNode node)
    {
        var span = Span(node);
        return new(Text(node)[2..^2], GrammarSpan.FromBounds(span.Start + 2, span.End - 2));
    }

    static ModuleDecl Module(HirNode node)
    {
        var module = Expect(node, "Module");
        var usings = new List<UsingDecl>();
        var rules = new List<RuleDecl>();
        var extends = new List<ExtendDecl>();
        var symbols = new List<SymbolsDecl>();
        var builtins = new List<BuiltinDecl>();
        var symbolProperties = new List<SymbolPropertyDecl>();
        var helpers = new List<QuoteHelperDecl>();
        var functions = new List<GrammarFunctionDecl>();
        foreach (var item in Items(module.Arguments[1]))
        {
            var member = (HirOperation)item;
            var a = member.Arguments;
            if (Kind(member) is "QuoteHelper" or "QuoteMatchHelper" && Text(a[2]) == GrammarFunctions.RuleType)
                throw new NgrMappingException("a quote helper cannot return Grammar.Rule; give it a grammar body", Span(a[2]));
            switch (Kind(member))
            {
                case "GrammarFunction":
                    if (Text(a[2]) != GrammarFunctions.RuleType)
                        throw new NgrMappingException("a function with a grammar body must return Grammar.Rule", Span(a[2]));
                    functions.Add(new(Name(a[0]), Items(a[1]).Select(QuoteParameter).ToArray(), Name(a[2]),
                        Items(a[3]).Select(Let).ToArray(), Expression(a[4]), Span(member)));
                    break;
                case "QuoteHelper":
                    helpers.Add(new(Name(a[0]), Items(a[1]).Select(QuoteParameter).ToArray(), Name(a[2]), Quote(a[3]), Span(member)));
                    break;
                case "QuoteMatchHelper":
                {
                    var body = Expect(a[3], "QuoteMatchBody");
                    var arms = Items(body.Arguments[1]).Select(arm =>
                    {
                        var parts = Expect(arm, "QuoteMatchArm").Arguments;
                        var captures = Value(parts[0]) is { } captureList
                            ? Items(Expect(captureList, "QuoteCaptures").Arguments[0]).Select(QuoteParameter).ToArray()
                            : Array.Empty<QuoteParameter>();
                        return new QuoteMatchArm(captures, Quote(parts[1]), Quote(parts[2]), Span(arm));
                    }).ToArray();
                    helpers.Add(new(Name(a[0]), Items(a[1]).Select(QuoteParameter).ToArray(), Name(a[2]), Quote(body.Arguments[2]), Span(member))
                    { Match = new(Name(body.Arguments[0]), arms, Span(body)) });
                    break;
                }
                case "Using":
                    usings.Add(new UsingDecl(Text(a[0]), Span(member)));
                    break;
                case "TokenRule":
                {
                    var except = new List<LiteralExpr>();
                    if (Value(a[2]) is { } list)
                        foreach (var word in Items(Expect(list, "Except").Arguments[0]))
                        {
                            string value = Unquote(Text(word));
                            if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(word));
                            except.Add(new LiteralExpr(value, Span(word)));
                        }
                    rules.Add(new TokenRule(Text(a[0]), Expression(a[1]), Span(member), except.ToArray()));
                    break;
                }
                case "SyntaxRule":
                {
                    var end = (HirOperation)a[3];
                    SemanticsBlock? semantics = Kind(end) == "RuleSemantics" ? Semantics(end.Arguments[0]) : null;
                    rules.Add(new SyntaxRule(Text(a[0]), Expression(a[1]), Span(member), Clauses(a[2]), semantics));
                    break;
                }
                case "ExtensibleRule":
                    rules.Add(new ExtensibleRule(Text(a[0]), Alternatives(a[2]), Span(member), Items(a[1]).Select(Property).ToArray()));
                    break;
                case "Symbols":
                    symbols.Add(new SymbolsDecl(Items(a[0]).Select(Name).ToArray(), Span(member)));
                    break;
                case "Builtin":
                    builtins.Add(new BuiltinDecl(Name(a[0]), Items(a[2]).Select(Name).ToArray(), Span(member),
                        Value(a[1]) is { } within ? AtEnd(within) : null));
                    break;
                case "SymbolProperty":
                    symbolProperties.Add(new SymbolPropertyDecl(Name(a[0]), Kinds(a[1]), Code(a[2]), Code(a[3]), Span(member)));
                    break;
                case "Extend":
                    extends.Add(new ExtendDecl(Text(a[0]), Alternatives(a[1]), Span(member)));
                    break;
                default:
                    throw Unexpected(member);
            }
        }
        return new ModuleDecl(Text(module.Arguments[0]), usings.ToArray(), rules.ToArray(), extends.ToArray(), Span(module),
            symbols.ToArray(), builtins.ToArray(), symbolProperties.ToArray(), helpers.ToArray(), functions.ToArray());
    }

    static EquatableArray<Alternative> Alternatives(HirNode list)
    {
        var alternatives = new List<Alternative>();
        foreach (var item in Items(list))
        {
            var node = Expect(item, "Alternative");
            var a = node.Arguments;
            var body = Expression(a[1]);
            string? name = Value(a[0]) is { } named ? Text(named) : null;
            int end = body.Span.End;
            int? precedence = null;
            GrammarAssociativity? associativity = null;
            if (Value(a[2]) is { } spec)
            {
                var group = Expect(spec, "Precedence");
                end = Span(group).End;
                var number = group.Arguments[0];
                string digits = Text(number);
                if (digits.Length > 9) throw new NgrMappingException("precedence is too large", Span(number));
                precedence = int.Parse(digits, CultureInfo.InvariantCulture);
                if (Value(group.Arguments[1]) is { } word)
                    associativity = Text(word) == "left" ? GrammarAssociativity.Left : GrammarAssociativity.Right;
            }
            var clauses = Clauses(a[3]);
            if (clauses.Count > 0) end = clauses[clauses.Count - 1].Span.End;
            SemanticsBlock? semantics = null;
            if (Value(a[4]) is { } block)
            {
                semantics = Semantics(block);
                end = semantics.Span.End;
            }
            bool isImplicit = false;
            if (name is null)
            {
                if (body is ReferenceExpr reference)
                {
                    int dot = reference.Name.LastIndexOf('.');
                    name = dot < 0 ? reference.Name : reference.Name.Substring(dot + 1);
                    isImplicit = true;
                }
                else
                {
                    name = "";
                }
            }
            alternatives.Add(new Alternative(name, isImplicit, body, precedence, associativity,
                GrammarSpan.FromBounds(Span(node).Start, end), clauses, semantics, IsTransparent: Value(a[5]) is not null));
        }
        return alternatives.ToArray();
    }

    /// <summary>Clause spans end at their last token, as the bootstrap parser's do (an absent optional adds nothing).</summary>
    static EquatableArray<BindingClause> Clauses(HirNode list)
    {
        var clauses = new List<BindingClause>();
        foreach (var item in Items(list))
        {
            var node = (HirOperation)item;
            var a = node.Arguments;
            switch (Kind(node))
            {
                case "Declares":
                {
                    var field = a[1];
                    var sequential = Value(a[2]);
                    var fileScope = Value(a[3]);
                    var export = Value(a[4]);
                    int end = export is not null ? Span(export).End : Span(field).End;
                    if (export is null && sequential is not null) end = Span(sequential).End;
                    if (export is null && fileScope is not null) end = Span(fileScope).End;
                    NameDecl? type = null;
                    if (Value(a[5]) is { } declared)
                    {
                        type = AtEnd(declared);
                        end = type.Span.End;
                    }
                    clauses.Add(new BindingClause(BindingClauseKind.Declares, new[] { Name(a[0]) }, Text(field), Span(field), false,
                        export is not null, GrammarSpan.FromBounds(Span(node).Start, end), Target: type,
                        FileScope: fileScope is not null, Sequential: sequential is not null));
                    break;
                }
                case "References":
                {
                    var field = a[2];
                    NameDecl? qualifier = Value(a[3]) is { } within ? AtEnd(within) : null;
                    int end = qualifier?.Span.End ?? Span(field).End;
                    clauses.Add(new BindingClause(BindingClauseKind.References, Kinds(a[1]), Text(field), Span(field),
                        Value(a[0]) is not null, false, GrammarSpan.FromBounds(Span(node).Start, end), qualifier));
                    break;
                }
                case "Scope":
                    clauses.Add(new BindingClause(BindingClauseKind.Scope, default, "", default, false, false, Span(node)));
                    break;
                case "Dynamic":
                    clauses.Add(new BindingClause(BindingClauseKind.Dynamic, default, "", default, false, false, Span(node)));
                    break;
                case "HelperRewrite":
                    clauses.Add(new(BindingClauseKind.Rewrites, default, "", default, false, false, Span(node),
                        HelperCall: new(Name(a[0]), Items(a[1]).Select(Name).ToArray(),
                            GrammarSpan.FromBounds(Span(a[0]).Start, Span(node).End))));
                    break;
                case "Rewrites":
                {
                    // The token includes its delimiters; the bootstrap parser keeps the text between them.
                    var token = Span(a[0]);
                    string text = Text(a[0]);
                    clauses.Add(new BindingClause(BindingClauseKind.Rewrites, default, "", default, false, false,
                        GrammarSpan.FromBounds(Span(node).Start, token.End),
                        Target: new NameDecl(text[2..^2], GrammarSpan.FromBounds(token.Start + 2, token.End - 2))));
                    break;
                }
                case "Lowers":
                    clauses.Add(Lowers(node));
                    break;
                default:
                    throw Unexpected(node);
            }
        }
        return clauses.ToArray();
    }

    static BindingClause Lowers(HirOperation clause)
    {
        int start = Span(clause).Start;
        var form = (HirOperation)clause.Arguments[0];
        var a = form.Arguments;
        switch (Kind(form))
        {
            case "LowersRepeat":
            {
                var type = Name(a[0]);
                return new BindingClause(BindingClauseKind.LowersRepeat, default, "", default, false, false,
                    GrammarSpan.FromBounds(start, Span(a[3]).End), Target: type, Arguments: new[]
                    {
                        new LoweringArgument(Text(a[1]), Span(a[1])),
                        new LoweringArgument(Text(a[2]), Span(a[2])),
                        new LoweringArgument(Text(a[3]), Span(a[3]), SequenceElementType: type),
                    });
            }
            case "LowersLiteral":
            case "LowersText":
            case "LowersSequence":
            {
                var kind = Kind(form) switch
                {
                    "LowersText" => BindingClauseKind.LowersText,
                    "LowersSequence" => BindingClauseKind.LowersSequence,
                    _ => BindingClauseKind.LowersLiteral,
                };
                return new BindingClause(kind, default, Text(a[1]), Span(a[1]), false, false,
                    GrammarSpan.FromBounds(start, Span(a[1]).End), Target: Name(a[0]));
            }
            case "LowersValue":
            {
                var spec = (HirOperation)a[0];
                bool computed = Kind(spec) == "ComputedType";
                var type = Name(spec.Arguments[0]);
                return new BindingClause(BindingClauseKind.LowersValue, default, Text(a[1]), Span(a[1]), false, false,
                    GrammarSpan.FromBounds(start, Span(a[1]).End), Target: computed ? null : type, TypeProperty: computed ? type : null);
            }
            case "LowersTemplate":
            case "LowersExpand":
                return new BindingClause(Kind(form) == "LowersTemplate" ? BindingClauseKind.LowersTemplate : BindingClauseKind.LowersExpand,
                    default, Text(a[0]), Span(a[0]), false, false, GrammarSpan.FromBounds(start, Span(form).End),
                    Arguments: new[] { new LoweringArgument(Text(a[1]), Span(a[1])) });
            case "LowersReference":
            {
                var property = Name(a[0]);
                NameDecl? initializer = Value(a[1]) is { } spec ? AtEnd(spec) : null;
                return new BindingClause(BindingClauseKind.LowersReference, default, "", default, false, false,
                    GrammarSpan.FromBounds(start, initializer?.Span.End ?? property.Span.End),
                    TypeProperty: property, InitializerProperty: initializer);
            }
            case "LowersCall":
            {
                var operation = (HirOperation)a[0];
                bool computed = Kind(operation) == "ComputedOperation";
                bool optional = computed && Value(operation.Arguments[0]) is not null;
                var name = Name(operation.Arguments[computed ? 1 : 0]);
                return new BindingClause(BindingClauseKind.Lowers, default, "", default, optional, false,
                    GrammarSpan.FromBounds(start, Span(form).End), Target: computed ? null : name,
                    Arguments: Items(a[1]).Select(Argument).ToArray(), OperationProperty: computed ? name : null);
            }
            default:
                throw Unexpected(form);
        }
    }

    static LoweringArgument Argument(HirNode node)
    {
        var argument = (HirOperation)node;
        var a = argument.Arguments;
        return Kind(argument) switch
        {
            "SequenceArgument" => Text(a[0]) == "inferred"
                ? new LoweringArgument(Text(a[1]), Span(a[1]), InferSequence: true)
                : new LoweringArgument(Text(a[1]), Span(a[1]), Name(a[0])),
            "OptionalArgument" => new LoweringArgument(Text(a[1]), Span(a[1]), OptionalElementType: Name(a[0])),
            "OptionalTextArgument" => new LoweringArgument(Text(a[0]), Span(a[0]), OptionalText: true),
            "TextArgument" => new LoweringArgument(Text(a[0]), Span(a[0]), AsText: true),
            "FieldArgument" => new LoweringArgument(Text(a[0]), Span(a[0])),
            _ => throw Unexpected(argument),
        };
    }

    static EquatableArray<NameDecl> Kinds(HirNode node)
    {
        var kinds = (HirOperation)node;
        return Kind(kinds) == "KindGroup" ? Items(kinds.Arguments[0]).Select(Name).ToArray() : new[] { Name(kinds.Arguments[0]) };
    }

    /// <summary>C# text as the bootstrap parser captures it: trimmed at the end (issue 239).</summary>
    static CodeText Code(HirNode node)
    {
        string text = Text(node).TrimEnd();
        return new CodeText(text, new GrammarSpan(node.Origins[0].Span.Start, text.Length));
    }

    static PropertyDecl Property(HirNode node)
    {
        var property = Expect(node, "PropertyDecl");
        var a = property.Arguments;
        bool hover = false, expected = false;
        foreach (var flag in Items(a[1]))
        {
            if (Text(flag) == "hover") hover = true;
            else expected = true;
        }
        var direction = Text(a[0]) == "out" ? PropertyDirection.Out : PropertyDirection.In;
        return new PropertyDecl(direction, Name(a[2]), Code(a[3]), Code(a[4]), hover, expected, Span(property));
    }

    static SemanticsBlock Semantics(HirNode node)
    {
        var block = Expect(node, "Semantics");
        var properties = new List<PropertyDecl>();
        var statements = new List<SemanticStatement>();
        foreach (var item in Items(block.Arguments[0]))
        {
            var statement = (HirOperation)item;
            var a = statement.Arguments;
            switch (Kind(statement))
            {
                case "PropertyDecl":
                    properties.Add(Property(statement));
                    break;
                case "Check":
                    statements.Add(new CheckStatement(Value(a[0]) is { } code ? Name(code) : null, Code(a[1]), Code(a[2]),
                        Span(statement), Value(a[3]) is { } at ? AtEnd(at) : null));
                    break;
                case "Assignment":
                    statements.Add(Assign(statement));
                    break;
                default:
                    throw Unexpected(statement);
            }
        }
        return new SemanticsBlock(properties.ToArray(), statements.ToArray(), Span(block));
    }

    static AssignStatement Assign(HirOperation node)
    {
        var target = node.Arguments[0];
        string text = Text(target);
        var targetSpan = target.Origins[0].Span;
        var span = Span(node);
        var value = Code(node.Arguments[1]);
        string[] parts = text.Split('.');
        if (parts.Length == 1) return new AssignStatement(AssignTarget.Self, null, new NameDecl(text, Span(target)), value, span);
        if (parts.Length > 2)
            throw new NgrMappingException("an assignment target is Property, Child.Property or symbol.Property", Span(target));
        var property = new NameDecl(parts[1], GrammarSpan.FromBounds(targetSpan.Start + parts[0].Length + 1, targetSpan.End));
        if (parts[0] == "symbol") return new AssignStatement(AssignTarget.Symbol, null, property, value, span);
        return new AssignStatement(AssignTarget.Child, new NameDecl(parts[0], new GrammarSpan(targetSpan.Start, parts[0].Length)), property, value, span);
    }

    /// <summary>A choice of one sequence is that sequence.</summary>
    static Expr Expression(HirNode node)
    {
        var alternatives = Items(Expect(node, "Choice").Arguments[0]).Select(Sequence).ToList();
        return alternatives.Count == 1
            ? alternatives[0]
            : new ChoiceExpr(alternatives.ToArray(), Bounds(alternatives[0], alternatives[^1]));
    }

    /// <summary>A sequence of one element is that element.</summary>
    static Expr Sequence(HirNode node)
    {
        var items = Items(Expect(node, "Sequence").Arguments[0]).Select(Element).ToList();
        return items.Count == 1 ? items[0] : new SequenceExpr(items.ToArray(), Bounds(items[0], items[^1]));
    }

    /// <summary>A label spans from its name (the LabelPrefix node's start) to the end of what it labels.</summary>
    static Expr Element(HirNode node)
    {
        var element = Expect(node, "Element");
        var inner = Unary(element.Arguments[1]);
        if (Value(element.Arguments[0]) is not { } label) return inner;
        return new LabeledExpr(Text(label), inner, GrammarSpan.FromBounds(label.Origins[0].Span.Start, inner.Span.End));
    }

    static Expr Unary(HirNode node)
    {
        var unary = Expect(node, "Unary");
        var inner = Postfix(unary.Arguments[1]);
        var prefixes = Items(unary.Arguments[0]);
        for (int i = prefixes.Count - 1; i >= 0; i--)
        {
            var op = prefixes[i];
            var kind = Text(op) == "!" ? PredicateKind.Not : PredicateKind.And;
            inner = new PredicateExpr(kind, inner, GrammarSpan.FromBounds(op.Origins[0].Span.Start, inner.Span.End));
        }
        return inner;
    }

    static Expr Postfix(HirNode node)
    {
        var postfix = Expect(node, "Postfix");
        var expr = Primary(postfix.Arguments[0]);
        foreach (var op in Items(postfix.Arguments[1]))
        {
            var kind = Text(op) switch
            {
                "?" => RepeatKind.Optional,
                "*" => RepeatKind.ZeroOrMore,
                _ => RepeatKind.OneOrMore,
            };
            expr = new RepeatExpr(kind, expr, GrammarSpan.FromBounds(expr.Span.Start, op.Origins[0].Span.End));
        }
        return expr;
    }

    static Expr Primary(HirNode node)
    {
        var primary = (HirOperation)node;
        switch (Kind(primary))
        {
            case "Literal":
            {
                var quoted = primary.Arguments[0];
                string value = Unquote(Text(quoted));
                if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(quoted));
                return new LiteralExpr(value, Span(quoted));
            }
            case "Any":
                return new AnyCharExpr(Span(primary));
            case "CharClass":
            {
                var items = ClassItems(primary.Arguments[1]);
                bool negated = Value(primary.Arguments[0]) is not null;
                if (items.All(i => i.First is CharValueExpr && i.Last is null or CharValueExpr))
                    return new CharClassExpr(items.Select(i => new CharRange(((CharValueExpr)i.First).Value,
                        ((CharValueExpr?)i.Last ?? (CharValueExpr)i.First).Value)).ToArray(), negated, Span(primary));
                return new ValueClassExpr(items, negated, Span(primary));
            }
            case "If":
            {
                var otherwise = Expression(primary.Arguments[2]);
                return new IfExpr(ValueOf(primary.Arguments[0]), Expression(primary.Arguments[1]), otherwise,
                    GrammarSpan.FromBounds(Span(primary).Start, otherwise.Span.End));
            }
            case "Comprehension":
            {
                var a = primary.Arguments;
                var block = Block(a[4]);
                return new ComprehensionExpr(Text(a[0]) == "choice", Name(a[1]), ValueOf(a[2]),
                    Value(a[3]) is { } to ? ValueOf(Expect(to, "RangeTo").Arguments[0]) : null, block,
                    GrammarSpan.FromBounds(Span(primary).Start, block.Span.End));
            }
            case "Call":
                return new GrammarCallExpr(CallName(primary.Arguments[0]), Items(primary.Arguments[1]).Select(ValueOf).ToArray(), Span(primary));
            case "Empty":
                return new SequenceExpr(Array.Empty<Expr>(), Span(primary));
            case "Parenthesized":
            {
                var inner = Expression(primary.Arguments[0]);
                var tail = (HirOperation)primary.Arguments[1];
                if (Kind(tail) != "SeparatorTail") return inner; // a plain group keeps the inner span
                return new SeparatedListExpr(inner, Expression(tail.Arguments[0]), Text(tail.Arguments[1]) == "+", Span(primary));
            }
            case "Reference":
                return new ReferenceExpr(Text(primary.Arguments[0]), Span(primary.Arguments[0]));
            default:
                throw Unexpected(primary);
        }
    }

    // ---- grammar functions (spec: grammar functions); spans follow the bootstrap parser ----

    static LetBinding Let(HirNode node)
    {
        var let = Expect(node, "LetBinding");
        return new LetBinding(Name(let.Arguments[0]), ValueOf(let.Arguments[1]), Span(let));
    }

    static FunctionBlock Block(HirNode node)
    {
        var block = Expect(node, "Block");
        var where = Value(block.Arguments[1]) is { } clause ? ValueOf(Expect(clause, "Where").Arguments[0]) : null;
        return new FunctionBlock(Items(block.Arguments[0]).Select(Let).ToArray(), where, Expression(block.Arguments[2]), Span(block));
    }

    /// <summary>A <c>CallHead</c> token is the name and its <c>(</c>.</summary>
    static NameDecl CallName(HirNode head)
    {
        string name = Text(head)[..^1];
        return new NameDecl(name, new GrammarSpan(Span(head).Start, name.Length));
    }

    static ValueClassItem[] ClassItems(HirNode items) => Items(items).Select(entry =>
    {
        var item = Expect(entry, "ClassItem");
        var first = ValueOf(item.Arguments[0]);
        var last = Value(item.Arguments[1]) is { } end ? ValueOf(Expect(end, "RangeTo").Arguments[0]) : null;
        if (first is CharValueExpr low && last is CharValueExpr high && high.Value < low.Value)
            throw new NgrMappingException("empty character range", GrammarSpan.FromBounds(first.Span.Start, last.Span.End));
        return new ValueClassItem(first, last, GrammarSpan.FromBounds(first.Span.Start, (last ?? first).Span.End));
    }).ToArray();

    static ValueExpr ValueOf(HirNode node)
    {
        var value = (HirOperation)node;
        var a = value.Arguments;
        switch (Kind(value))
        {
            case "IfValue":
            {
                var otherwise = ValueOf(a[2]);
                return new IfValueExpr(ValueOf(a[0]), ValueOf(a[1]), otherwise, GrammarSpan.FromBounds(Span(value).Start, otherwise.Span.End));
            }
            case "Chain":
                return Items(a[1]).Aggregate(ValueOf(a[0]), Binary);
            case "Single":
                return Value(a[1]) is { } tail ? Binary(ValueOf(a[0]), tail) : ValueOf(a[0]);
            case "UnaryValue":
            {
                var operand = ValueOf(a[1]);
                return new UnaryValueExpr(Text(a[0]), operand, GrammarSpan.FromBounds(Span(value).Start, operand.Span.End));
            }
            case "IntValue":
                return long.TryParse(Text(a[0]), NumberStyles.None, CultureInfo.InvariantCulture, out long number)
                    ? new IntValueExpr(number, Span(value))
                    : throw new NgrMappingException("the integer is too large", Span(value));
            case "CharValue":
                return new CharValueExpr(Unquote(Text(a[0]))[0], Span(value));
            case "TextValue":
                return new TextValueExpr(Unquote(Text(a[0])), Span(value));
            case "ClassValue":
                return new ClassValueExpr(ClassItems(a[1]), Value(a[0]) is not null, Span(value));
            case "BlockValue":
            {
                var block = Block(a[0]);
                return new RuleValueExpr(block, block.Span);
            }
            case "ValueCall":
                return new CallValueExpr(CallName(a[0]), Items(a[1]).Select(ValueOf).ToArray(), Span(value));
            case "Name":
                return new NameValueExpr(Text(a[0]), Span(a[0]));
            case "ValueGroup":
                return ValueOf(a[0]);
            default:
                throw Unexpected(value);
        }
    }

    static ValueExpr Binary(ValueExpr left, HirNode tail)
    {
        var parts = Expect(tail, "ChainTail").Arguments;
        var right = ValueOf(parts[1]);
        return new BinaryValueExpr(Text(parts[0]), left, right, GrammarSpan.FromBounds(left.Span.Start, right.Span.End));
    }

    /// <summary>Decodes a quoted literal the String or Char token has already validated.</summary>
    static string Unquote(string quoted)
    {
        var builder = new StringBuilder();
        for (int i = 1; i < quoted.Length - 1; i++)
        {
            char c = quoted[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }
            char escape = quoted[++i];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case '0': builder.Append('\0'); break;
                case 'u':
                    builder.Append((char)int.Parse(quoted.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: builder.Append(escape); break; // \\ \" \'
            }
        }
        return builder.ToString();
    }

    static HirOperation Expect(HirNode node, string name) =>
        node is HirOperation operation && operation.Signature.Id == "Grammar." + name ? operation : throw Unexpected(node);

    static string Kind(HirOperation operation) =>
        operation.Signature.Id.StartsWith("Grammar.", StringComparison.Ordinal)
            ? operation.Signature.Id["Grammar.".Length..] : throw Unexpected(operation);

    static InvalidOperationException Unexpected(HirNode node) =>
        new($"unexpected {(node as HirOperation)?.Signature.Id ?? node.GetType().Name} in grammar HIR");

    static IReadOnlyList<HirNode> Items(HirNode node) => ((HirSequence)node).Items;

    static HirNode? Value(HirNode node) => ((HirOptional)node).Value;

    static string Text(HirNode node) => ((HirText)node).Value;

    static GrammarSpan Span(HirNode node)
    {
        var span = node.Origins[0].Span;
        return new GrammarSpan(span.Start, span.Length);
    }

    /// <summary>A name whose text spans its origin: an inline text argument, or a wrapper that is only its token.</summary>
    static NameDecl Name(HirNode text) => new(Text(text), Span(text));

    /// <summary>The name a wrapper ends with (<c>in Name</c>, <c>type Name</c>, <c>.. Char</c>, <c>initializer Name</c>, <c>at Name</c>).</summary>
    static NameDecl AtEnd(HirNode text)
    {
        var span = text.Origins[0].Span;
        string value = Text(text);
        return new NameDecl(value, GrammarSpan.FromBounds(span.End - value.Length, span.End));
    }

    static GrammarSpan Bounds(Expr first, Expr last) => GrammarSpan.FromBounds(first.Span.Start, last.Span.End);
}
