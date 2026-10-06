using System.Globalization;
using System.Text;
using Nitrogen.Grammar;
using Nitrogen.Ngr.Syntax;

namespace Nitrogen.Ngr;

/// <summary>
/// Maps a tree parsed by the generated Nitrogen module to the GrammarModel, following the
/// bootstrap parser's span rules exactly.
/// </summary>
internal sealed class NgrMapper(SyntaxTree tree)
{
    public GrammarFile File()
    {
        var file = SyntaxView.Cast<FileNode>(tree, tree.Root);
        var modules = new List<ModuleDecl>();
        foreach (var module in file.Modules) modules.Add(Module(module));
        return new GrammarFile(modules.ToArray());
    }

    ModuleDecl Module(ModuleNode node)
    {
        var usings = new List<UsingDecl>();
        var rules = new List<RuleDecl>();
        var extends = new List<ExtendDecl>();
        var symbols = new List<SymbolsDecl>();
        var builtins = new List<BuiltinDecl>();
        var symbolProperties = new List<SymbolPropertyDecl>();
        foreach (var member in node.Members)
        {
            int kind = member.Kind;
            if (kind == NitrogenKinds.Using)
            {
                var u = Cast<UsingNode>(member);
                usings.Add(new UsingDecl(u.Module.ToString(), Span(u.Span)));
            }
            else if (kind == NitrogenKinds.TokenRule)
            {
                var r = Cast<TokenRuleNode>(member);
                var except = new List<LiteralExpr>();
                if (r.Except.HasValue)
                {
                    foreach (var word in r.Except.Value.Words)
                    {
                        string value = Unquote(word.Value.ToString());
                        if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(word.Span));
                        except.Add(new LiteralExpr(value, Span(word.Span)));
                    }
                }
                rules.Add(new TokenRule(r.Name.ToString(), Expression(r.Body), Span(r.Span), except.ToArray()));
            }
            else if (kind == NitrogenKinds.SyntaxRule)
            {
                var r = Cast<SyntaxRuleNode>(member);
                SemanticsBlock? semantics = r.End.Kind == NitrogenKinds.RuleSemantics
                    ? Semantics(Cast<RuleSemanticsNode>(r.End).Block) : null;
                rules.Add(new SyntaxRule(r.Name.ToString(), Expression(r.Body), Span(r.Span), Clauses(r.Clauses), semantics));
            }
            else if (kind == NitrogenKinds.ExtensibleRule)
            {
                var r = Cast<ExtensibleRuleNode>(member);
                rules.Add(new ExtensibleRule(r.Name.ToString(), Alternatives(r.Alternatives), Span(r.Span), Properties(r.Properties)));
            }
            else if (kind == NitrogenKinds.Symbols)
            {
                var s = Cast<SymbolsNode>(member);
                var kinds = new List<NameDecl>();
                foreach (var k in s.Kinds) kinds.Add(Name(k.Value));
                symbols.Add(new SymbolsDecl(kinds.ToArray(), Span(s.Span)));
            }
            else if (kind == NitrogenKinds.Builtin)
            {
                var b = Cast<BuiltinNode>(member);
                var names = new List<NameDecl>();
                foreach (var n in b.Names) names.Add(new NameDecl(n.Value.ToString(), Span(n.Value.Span)));
                NameDecl? scope = null;
                if (b.Within.HasValue)
                {
                    var rule = b.Within.Value.Name;
                    scope = new NameDecl(rule.ToString(), Span(rule.Span));
                }
                builtins.Add(new BuiltinDecl(Name(b.SymbolKind), names.ToArray(), Span(b.Span), scope));
            }
            else if (kind == NitrogenKinds.SymbolProperty)
            {
                var p = Cast<SymbolPropertyNode>(member);
                symbolProperties.Add(new SymbolPropertyDecl(Name(p.Name), Kinds(p.Kinds), Code(p.Type), Code(p.Default), Span(p.Span)));
            }
            else
            {
                var e = Cast<ExtendNode>(member);
                extends.Add(new ExtendDecl(e.Target.ToString(), Alternatives(e.Alternatives), Span(e.Span)));
            }
        }
        return new ModuleDecl(node.Name.ToString(), usings.ToArray(), rules.ToArray(), extends.ToArray(), Span(node.Span),
            symbols.ToArray(), builtins.ToArray(), symbolProperties.ToArray());
    }

    EquatableArray<Alternative> Alternatives(SyntaxList<AlternativeNode> nodes)
    {
        var alternatives = new List<Alternative>();
        foreach (var node in nodes)
        {
            var body = Expression(node.Body);
            string? name = node.Named.HasValue ? node.Named.Value.Name.ToString() : null;
            int end = body.Span.End;
            int? precedence = null;
            GrammarAssociativity? associativity = null;
            if (node.Precedence.HasValue)
            {
                var group = node.Precedence.Value;
                end = group.Span.End;
                var number = group.Level;
                string digits = number.ToString();
                if (digits.Length > 9) throw new NgrMappingException("precedence is too large", Span(number.Span));
                precedence = int.Parse(digits, CultureInfo.InvariantCulture);
                if (group.Associativity.HasValue)
                    associativity = Text(group.Associativity.Value.Index) == "left" ? GrammarAssociativity.Left : GrammarAssociativity.Right;
            }
            var clauses = Clauses(node.Clauses);
            if (clauses.Count > 0) end = clauses[clauses.Count - 1].Span.End;
            SemanticsBlock? semantics = null;
            if (node.Semantics.HasValue)
            {
                semantics = Semantics(node.Semantics.Value);
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
                GrammarSpan.FromBounds(node.Span.Start, end), clauses, semantics));
        }
        return alternatives.ToArray();
    }

    static NameDecl Name(Token token) => new(token.ToString(), Span(token.Span));

    /// <summary>Clause spans end at their last token, as the bootstrap parser's do (an absent optional adds nothing).</summary>
    EquatableArray<BindingClause> Clauses(SyntaxList<SyntaxNode> nodes)
    {
        var clauses = new List<BindingClause>();
        foreach (var node in nodes)
        {
            int kind = node.Kind;
            if (kind == NitrogenKinds.Declares)
            {
                var d = Cast<DeclaresNode>(node);
                int end = d.Export.HasValue ? d.Export.Value.Span.End : d.Field.Span.End;
                if (!d.Export.HasValue && d.Sequential.HasValue) end = d.Sequential.Value.Span.End;
                if (!d.Export.HasValue && d.FileScope.HasValue) end = d.FileScope.Value.Span.End;
                NameDecl? type = null;
                if (d.Type.HasValue)
                {
                    var name = d.Type.Value.Name;
                    type = new NameDecl(name.ToString(), Span(name.Span));
                    end = name.Span.End;
                }
                clauses.Add(new BindingClause(BindingClauseKind.Declares, new[] { Name(d.SymbolKind) }, d.Field.ToString(),
                    Span(d.Field.Span), false, d.Export.HasValue, GrammarSpan.FromBounds(d.Span.Start, end), Target: type,
                    FileScope: d.FileScope.HasValue, Sequential: d.Sequential.HasValue));
            }
            else if (kind == NitrogenKinds.Lowers)
            {
                var l = Cast<LowersNode>(node);
                if (l.Form.Kind == NitrogenKinds.LowersRepeat)
                {
                    var repeat = Cast<LowersRepeatNode>(l.Form);
                    var type = new NameDecl(repeat.Type.ToString(), Span(repeat.Type.Span));
                    clauses.Add(new BindingClause(BindingClauseKind.LowersRepeat, default, "", default, false, false,
                        GrammarSpan.FromBounds(l.Span.Start, repeat.Children.Span.End), Target: type, Arguments: new[]
                        {
                            new LoweringArgument(repeat.Count.ToString(), Span(repeat.Count.Span)),
                            new LoweringArgument(repeat.Iterator.ToString(), Span(repeat.Iterator.Span)),
                            new LoweringArgument(repeat.Children.ToString(), Span(repeat.Children.Span), SequenceElementType: type),
                        }));
                }
                else if (l.Form.Kind == NitrogenKinds.LowersLiteral || l.Form.Kind == NitrogenKinds.LowersText ||
                    l.Form.Kind == NitrogenKinds.LowersSequence || l.Form.Kind == NitrogenKinds.LowersValue)
                {
                    var form = l.Form.Kind == NitrogenKinds.LowersText ? BindingClauseKind.LowersText
                        : l.Form.Kind == NitrogenKinds.LowersSequence ? BindingClauseKind.LowersSequence
                        : l.Form.Kind == NitrogenKinds.LowersValue ? BindingClauseKind.LowersValue
                        : BindingClauseKind.LowersLiteral;
                    var type = l.Form.Child(1);
                    var field = l.Form.Child(2);
                    bool hasDynamicType = form == BindingClauseKind.LowersValue &&
                        type.ChildCount == 2 && type.Child(0).ToString() == "type";
                    var dynamicType = hasDynamicType ? type.Child(1) : type;
                    clauses.Add(new BindingClause(form, default, field.ToString(),
                        Span(field.Span), false, false, GrammarSpan.FromBounds(l.Span.Start, field.Span.End),
                        Target: hasDynamicType ? null : new NameDecl(type.ToString(), Span(type.Span)),
                        TypeProperty: hasDynamicType ? new NameDecl(dynamicType.ToString(), Span(dynamicType.Span)) : null));
                }
                else if (l.Form.Kind == NitrogenKinds.LowersTemplate || l.Form.Kind == NitrogenKinds.LowersExpand)
                {
                    var field = l.Form.Child(1);
                    var list = l.Form.Child(3);
                    clauses.Add(new BindingClause(l.Form.Kind == NitrogenKinds.LowersTemplate
                            ? BindingClauseKind.LowersTemplate : BindingClauseKind.LowersExpand, default,
                        field.ToString(), Span(field.Span), false, false, GrammarSpan.FromBounds(l.Span.Start, l.Form.Child(4).Span.End),
                        Arguments: new[] { new LoweringArgument(list.ToString(), Span(list.Span)) }));
                }
                else if (l.Form.Kind == NitrogenKinds.LowersReference)
                {
                    var property = l.Form.Child(2);
                    var source = Cast<LowersReferenceNode>(l.Form).Initializer;
                    Token? initializer = source.HasValue ? source.Value.Name : null;
                    clauses.Add(new BindingClause(BindingClauseKind.LowersReference, default, "", default,
                        false, false, GrammarSpan.FromBounds(l.Span.Start, initializer?.Span.End ?? property.Span.End),
                        TypeProperty: new NameDecl(property.ToString(), Span(property.Span)),
                        InitializerProperty: initializer is { } value ? new NameDecl(value.ToString(), Span(value.Span)) : null));
                }
                else
                {
                    var call = Cast<LowersCallNode>(l.Form);
                    var operationNode = call.Operation;
                    bool computed = operationNode.ChildCount >= 2 && operationNode.Child(0).ToString() == "operation";
                    bool optionalOperation = computed && operationNode.Child(1).ToString() == "?";
                    var operationName = computed ? operationNode.Child(operationNode.ChildCount - 1) : operationNode;
                    var arguments = new List<LoweringArgument>();
                    foreach (var argument in call.Arguments)
                    {
                        if (argument.Kind == NitrogenKinds.LowersSequenceArgument)
                        {
                            var type = argument.Child(1);
                            var field = argument.Child(2);
                            arguments.Add(type.ToString() == "inferred"
                                ? new LoweringArgument(field.ToString(), Span(field.Span), InferSequence: true)
                                : new LoweringArgument(field.ToString(), Span(field.Span),
                                    new NameDecl(type.ToString(), Span(type.Span))));
                        }
                        else if (argument.Kind == NitrogenKinds.LowersOptionalTextArgument)
                        {
                            var field = argument.Child(2);
                            arguments.Add(new LoweringArgument(field.ToString(), Span(field.Span), OptionalText: true));
                        }
                        else if (argument.Kind == NitrogenKinds.LowersOptionalArgument)
                        {
                            var type = argument.Child(1);
                            var field = argument.Child(2);
                            arguments.Add(new LoweringArgument(field.ToString(), Span(field.Span),
                                OptionalElementType: new NameDecl(type.ToString(), Span(type.Span))));
                        }
                        else if (argument.Kind == NitrogenKinds.LowersTextArgument)
                        {
                            var field = argument.Child(1);
                            arguments.Add(new LoweringArgument(field.ToString(), Span(field.Span), AsText: true));
                        }
                        else arguments.Add(new LoweringArgument(argument.ToString(), Span(argument.Span)));
                    }
                    clauses.Add(new BindingClause(BindingClauseKind.Lowers, default, "", default, optionalOperation, false,
                        GrammarSpan.FromBounds(l.Span.Start, call.Close.Span.End),
                        Target: computed ? null : new NameDecl(operationName.ToString(), Span(operationName.Span)),
                        Arguments: arguments.ToArray(),
                        OperationProperty: computed ? new NameDecl(operationName.ToString(), Span(operationName.Span)) : null));
                }
            }
            else if (kind == NitrogenKinds.References)
            {
                var r = Cast<ReferencesNode>(node);
                var kinds = Kinds(r.Kinds);
                NameDecl? qualifier = null;
                int end = r.Field.Span.End;
                if (r.Within.HasValue)
                {
                    var qualifierKind = r.Within.Value.Name;
                    qualifier = new NameDecl(qualifierKind.ToString(), Span(qualifierKind.Span));
                    end = qualifierKind.Span.End;
                }
                clauses.Add(new BindingClause(BindingClauseKind.References, kinds, r.Field.ToString(),
                    Span(r.Field.Span), r.Question.HasValue, false, GrammarSpan.FromBounds(r.Span.Start, end), qualifier));
            }
            else
            {
                var clauseKind = kind == NitrogenKinds.ScopeClause ? BindingClauseKind.Scope : BindingClauseKind.Dynamic;
                clauses.Add(new BindingClause(clauseKind, default, "", default, false, false, Span(node.Span)));
            }
        }
        return clauses.ToArray();
    }

    EquatableArray<NameDecl> Kinds(SyntaxNode node)
    {
        var kinds = new List<NameDecl>();
        if (node.Kind == NitrogenKinds.KindGroup)
            foreach (var k in Cast<KindGroupNode>(node).Kinds) kinds.Add(Name(k.Value));
        else
            kinds.Add(new NameDecl(node.ToString(), Span(node.Span)));
        return kinds.ToArray();
    }

    /// <summary>C# text as the bootstrap parser captures it: trimmed at the end (issue 239).</summary>
    static CodeText Code(Token token)
    {
        string text = token.ToString().TrimEnd();
        return new CodeText(text, new GrammarSpan(token.Span.Start, text.Length));
    }

    EquatableArray<PropertyDecl> Properties(SyntaxList<PropertyDeclNode> nodes)
    {
        var properties = new List<PropertyDecl>();
        foreach (var node in nodes) properties.Add(Property(node));
        return properties.ToArray();
    }

    PropertyDecl Property(PropertyDeclNode node)
    {
        bool hover = false, expected = false;
        foreach (var flag in node.Flags)
        {
            if (Text(flag.Index) == "hover") hover = true;
            else expected = true;
        }
        var direction = node.Direction.ToString() == "out" ? PropertyDirection.Out : PropertyDirection.In;
        return new PropertyDecl(direction, Name(node.Name), Code(node.Type), Code(node.Default), hover, expected, Span(node.Span));
    }

    SemanticsBlock Semantics(SemanticsNode node)
    {
        var properties = new List<PropertyDecl>();
        var statements = new List<SemanticStatement>();
        foreach (var item in node.Items)
        {
            int kind = item.Kind;
            if (kind == NitrogenKinds.PropertyDecl)
            {
                properties.Add(Property(Cast<PropertyDeclNode>(item)));
            }
            else if (kind == NitrogenKinds.Check)
            {
                var c = Cast<CheckNode>(item);
                NameDecl? code = c.Code.HasValue ? Name(c.Code.Value) : null;
                NameDecl? at = c.At.HasValue ? Name(new Token(tree, tree.Child(c.At.Value.Index, 1))) : null;
                statements.Add(new CheckStatement(code, Code(c.Condition), Code(c.Message), Span(c.Span), at));
            }
            else
            {
                statements.Add(Assign(Cast<AssignmentNode>(item)));
            }
        }
        return new SemanticsBlock(properties.ToArray(), statements.ToArray(), Span(node.Span));
    }

    AssignStatement Assign(AssignmentNode node)
    {
        var target = node.Target;
        string text = target.ToString();
        var span = Span(node.Span);
        var value = Code(node.Value);
        string[] parts = text.Split('.');
        if (parts.Length == 1) return new AssignStatement(AssignTarget.Self, null, new NameDecl(text, Span(target.Span)), value, span);
        if (parts.Length > 2)
            throw new NgrMappingException("an assignment target is Property, Child.Property or symbol.Property", Span(target.Span));
        var property = new NameDecl(parts[1], GrammarSpan.FromBounds(target.Span.Start + parts[0].Length + 1, target.Span.End));
        if (parts[0] == "symbol") return new AssignStatement(AssignTarget.Symbol, null, property, value, span);
        return new AssignStatement(AssignTarget.Child, new NameDecl(parts[0], new GrammarSpan(target.Span.Start, parts[0].Length)), property, value, span);
    }

    Expr Expression(ExpressionNode node)
    {
        var alternatives = new List<Expr>();
        foreach (var sequence in node.Choices) alternatives.Add(Sequence(sequence));
        return alternatives.Count == 1
            ? alternatives[0]
            : new ChoiceExpr(alternatives.ToArray(), Bounds(alternatives[0], alternatives[^1]));
    }

    Expr Sequence(SequenceNode node)
    {
        var items = new List<Expr>();
        foreach (var element in node.Elements) items.Add(Element(element));
        return items.Count == 1 ? items[0] : new SequenceExpr(items.ToArray(), Bounds(items[0], items[^1]));
    }

    Expr Element(ElementNode node)
    {
        var inner = Unary(node.Body);
        if (!node.Label.HasValue) return inner;
        var label = node.Label.Value;
        return new LabeledExpr(label.Name.ToString(), inner, GrammarSpan.FromBounds(label.Span.Start, inner.Span.End));
    }

    Expr Unary(UnaryNode node)
    {
        var inner = Postfix(node.Body);
        for (int i = node.Prefixes.Count - 1; i >= 0; i--)
        {
            var op = node.Prefixes[i];
            var kind = Text(op.Index) == "!" ? PredicateKind.Not : PredicateKind.And;
            inner = new PredicateExpr(kind, inner, GrammarSpan.FromBounds(op.Span.Start, inner.Span.End));
        }
        return inner;
    }

    Expr Postfix(PostfixNode node)
    {
        var expr = Primary(node.Body);
        foreach (var op in node.Operators)
        {
            var kind = Text(op.Index) switch
            {
                "?" => RepeatKind.Optional,
                "*" => RepeatKind.ZeroOrMore,
                _ => RepeatKind.OneOrMore,
            };
            expr = new RepeatExpr(kind, expr, GrammarSpan.FromBounds(expr.Span.Start, op.Span.End));
        }
        return expr;
    }

    Expr Primary(SyntaxNode node)
    {
        int kind = node.Kind;
        if (kind == NitrogenKinds.StringLiteral)
        {
            string value = Unquote(node.ToString());
            if (value.Length == 0) throw new NgrMappingException("a literal must not be empty", Span(node.Span));
            return new LiteralExpr(value, Span(node.Span));
        }
        if (kind == NitrogenKinds.Any) return new AnyCharExpr(Span(node.Span));
        if (kind == NitrogenKinds.CharClass) return CharClass(Cast<CharClassNode>(node));
        if (kind == NitrogenKinds.Parenthesized)
        {
            var parenthesized = Cast<ParenthesizedNode>(node);
            var inner = Expression(parenthesized.Inner);
            if (parenthesized.Tail.Kind != NitrogenKinds.SeparatorTail) return inner; // a plain group keeps the inner span
            var tail = Cast<SeparatorTailNode>(parenthesized.Tail);
            return new SeparatedListExpr(inner, Expression(tail.Separator), Text(tail.Op.Index) == "+", Span(parenthesized.Span));
        }
        var reference = Cast<ReferenceNode>(node);
        return new ReferenceExpr(reference.Name.ToString(), Span(reference.Name.Span));
    }

    Expr CharClass(CharClassNode node)
    {
        var ranges = new List<CharRange>();
        foreach (var item in node.Items)
        {
            char first = Unquote(item.First.ToString())[0];
            char last = first;
            if (item.Last.HasValue)
            {
                var lastToken = item.Last.Value.Last;
                last = Unquote(lastToken.ToString())[0];
                if (last < first)
                    throw new NgrMappingException("empty character range", GrammarSpan.FromBounds(item.First.Span.Start, lastToken.Span.End));
            }
            ranges.Add(new CharRange(first, last));
        }
        return new CharClassExpr(ranges.ToArray(), node.Negated.HasValue, Span(node.Span));
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

    T Cast<T>(SyntaxNode node) where T : struct, ISyntaxView<T> => SyntaxView.Cast<T>(tree, node.Index);

    string Text(int index) => tree.GetText(index).ToString();

    static GrammarSpan Span(TextSpan span) => new(span.Start, span.Length);

    static GrammarSpan Bounds(Expr first, Expr last) => GrammarSpan.FromBounds(first.Span.Start, last.Span.End);
}
