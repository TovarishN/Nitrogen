using System.Globalization;

namespace Nitrogen.Grammar;

public sealed record GrammarParseResult(GrammarFile? File, EquatableArray<GrammarDiagnostic> Diagnostics)
{
    public bool Success => File is not null;
}

/// <summary>The bootstrap parser for <c>.ngr</c> files. It stops at the first syntax error.</summary>
public static class GrammarParser
{
    public static GrammarParseResult Parse(string text) => ParseCore(text, false);

    /// <summary>Reads leading language metadata even while a following syntax module is being edited.</summary>
    public static GrammarParseResult ParseDeclaration(string text) => ParseCore(text, true);

    static GrammarParseResult ParseCore(string text, bool declarationOnly)
    {
        try
        {
            var parser = new Parser(text);
            return new GrammarParseResult(GrammarFunctionSyntax.CheckOrThrow(parser.ParseFile(declarationOnly)), default);
        }
        catch (LanguageDeclarationException error)
        {
            return new GrammarParseResult(null, new[] {
                new GrammarDiagnostic(GrammarCodes.Syntax, GrammarSeverity.Error, error.Message, error.Span) });
        }
        catch (GrammarSyntaxException error)
        {
            var diagnostic = new GrammarDiagnostic(GrammarCodes.Syntax, GrammarSeverity.Error, error.Message, error.Span);
            return new GrammarParseResult(null, new[] { diagnostic });
        }
    }

    internal static bool CanUseUnqualifiedReference(string name) =>
        name is not ("precedence" or "except" or "then" or "else") && !IsClauseWord(name);

    /// <summary>Words a value expression never reads as a name (spec: grammar functions).</summary>
    internal static bool IsValueKeyword(string name) => name is "or" or "and" or "not" or "if" or "then" or "else";

    static bool IsClauseWord(string word) => word is "declares" or "references" or "scope" or "dynamic" or "lowers" or "rewrites";

    sealed class Parser
    {
        readonly string _text;
        readonly List<GrammarToken> _tokens = new();
        int _position, _lexed;

        public Parser(string text)
        {
            _text = text;
        }

        GrammarToken Current => TokenAt(_position);

        GrammarToken Next => TokenAt(_position + 1);

        /// <summary>Lexes on demand (issue 239): C# in a semantics block is captured as text, never tokenized.</summary>
        GrammarToken TokenAt(int index)
        {
            while (_tokens.Count <= index)
            {
                if (_tokens.Count > 0 && _tokens[_tokens.Count - 1].Kind == TokenKind.End) return _tokens[_tokens.Count - 1];
                _tokens.Add(GrammarLexer.Next(_text, ref _lexed));
            }
            return _tokens[index];
        }

        int AfterLast => _tokens[_position - 1].End;

        /// <summary>
        /// C# text from <paramref name="from"/> (trivia skipped) to the first top-level stop character
        /// (issue 239), trimmed at the end. Lexing resumes at the stop character.
        /// </summary>
        CodeText Code(int from, string stops, string what)
        {
            from = GrammarLexer.SkipTrivia(_text, from);
            int end = CSharpCapture.Scan(_text, from, stops);
            int trimmed = end;
            while (trimmed > from && char.IsWhiteSpace(_text[trimmed - 1])) trimmed--;
            if (trimmed == from) throw new GrammarSyntaxException("expected " + what, new GrammarSpan(from, 0));
            _tokens.RemoveRange(_position, _tokens.Count - _position);
            _lexed = end;
            return new CodeText(_text.Substring(from, trimmed - from), GrammarSpan.FromBounds(from, trimmed));
        }

        public GrammarFile ParseFile(bool declarationOnly)
        {
            var languages = new List<LanguageDecl>();
            if (AtKeyword("language")) languages.Add(ParseLanguage());
            var modules = new List<ModuleDecl>();
            while (!declarationOnly && !At(TokenKind.End)) modules.Add(ParseModule());
            return new GrammarFile(modules.ToArray(), languages.ToArray());
        }

        LanguageDecl ParseLanguage()
        {
            int start = ExpectKeyword("language").Start;
            string name = ExpectIdentifier("a language name");
            Expect(TokenKind.LBrace, "'{'");
            var settings = new List<LanguageSetting>();
            while (!At(TokenKind.RBrace))
            {
                var key = Expect(TokenKind.Identifier, "a language setting");
                var values = new List<NameDecl>();
                if (key.Value is "start" or "namespace")
                    ReadName();
                else if (key.Value is "tokens" or "types")
                {
                    ReadName();
                    ReadString();
                }
                else if (key.Value is "grammars" or "sources" or "extensions" or "usings")
                {
                    ReadValue();
                    while (At(TokenKind.Comma))
                    {
                        Advance();
                        ReadValue();
                    }
                }
                else throw new GrammarSyntaxException("unknown language setting '" + key.Value + "'", key.Span);
                var end = Expect(TokenKind.Semicolon, "';'");
                settings.Add(new LanguageSetting(key.Value, values.ToArray(), GrammarSpan.FromBounds(key.Start, end.End)));

                void ReadName()
                {
                    var token = Current;
                    values.Add(new NameDecl(ExpectName("a qualified name"), token.Span));
                }
                void ReadString()
                {
                    var token = Expect(TokenKind.String, "a quoted string");
                    values.Add(new NameDecl(token.Value, token.Span));
                }
                void ReadValue()
                {
                    if (key.Value == "usings") ReadName();
                    else ReadString();
                }
            }
            int finish = Advance().End;
            return LanguageDeclarations.Build(name, settings.ToArray(), GrammarSpan.FromBounds(start, finish));
        }

        ModuleDecl ParseModule()
        {
            var start = ExpectKeyword("syntax");
            ExpectKeyword("module");
            string name = ExpectName("a module name");
            Expect(TokenKind.LBrace, "'{'");
            var usings = new List<UsingDecl>();
            var rules = new List<RuleDecl>();
            var extends = new List<ExtendDecl>();
            var symbols = new List<SymbolsDecl>();
            var builtins = new List<BuiltinDecl>();
            var symbolProperties = new List<SymbolPropertyDecl>();
            var helpers = new List<QuoteHelperDecl>();
            var functions = new List<GrammarFunctionDecl>();
            while (!At(TokenKind.RBrace))
            {
                if (AtKeyword("function") && Next.Kind == TokenKind.Identifier && TokenAt(_position + 2).Kind == TokenKind.LParen)
                {
                    ParseFunction(Current, helpers, functions);
                    continue;
                }
                if (AtKeyword("using"))
                {
                    var keyword = Advance();
                    string module = ExpectName("a module name");
                    var semicolon = Expect(TokenKind.Semicolon, "';'");
                    usings.Add(new UsingDecl(module, GrammarSpan.FromBounds(keyword.Start, semicolon.End)));
                }
                else if (AtKeyword("symbols"))
                {
                    var keyword = Advance();
                    Expect(TokenKind.LBrace, "'{'");
                    var kinds = new List<NameDecl>();
                    while (At(TokenKind.Identifier)) kinds.Add(Name(Advance()));
                    if (kinds.Count == 0) throw Error("expected a symbol kind");
                    var brace = Expect(TokenKind.RBrace, "a symbol kind or '}'");
                    symbols.Add(new SymbolsDecl(kinds.ToArray(), GrammarSpan.FromBounds(keyword.Start, brace.End)));
                }
                else if (AtKeyword("symbol"))
                {
                    symbolProperties.Add(ParseSymbolProperty());
                }
                else if (AtKeyword("builtin"))
                {
                    var keyword = Advance();
                    var kind = Name(Expect(TokenKind.Identifier, "a symbol kind"));
                    NameDecl? scope = null;
                    if (AtKeyword("in"))
                    {
                        Advance();
                        scope = Name(Expect(TokenKind.Identifier, "a rule name"));
                    }
                    Expect(TokenKind.LBrace, "'{'");
                    var names = new List<NameDecl>();
                    while (At(TokenKind.Identifier) || At(TokenKind.QualifiedName)) names.Add(Name(Advance()));
                    if (names.Count == 0) throw Error("expected a built-in name");
                    var brace = Expect(TokenKind.RBrace, "a built-in name or '}'");
                    builtins.Add(new BuiltinDecl(kind, names.ToArray(), GrammarSpan.FromBounds(keyword.Start, brace.End), scope));
                }
                else if (AtKeyword("token"))
                {
                    var keyword = Advance();
                    string ruleName = ExpectIdentifier("a rule name");
                    Expect(TokenKind.Equals, "'='");
                    var body = ParseExpr();
                    var except = new List<LiteralExpr>();
                    if (AtKeyword("except"))
                    {
                        Advance();
                        if (!At(TokenKind.String)) throw Error("expected a string literal");
                        while (At(TokenKind.String))
                        {
                            var word = Advance();
                            if (word.Value.Length == 0) throw new GrammarSyntaxException("a literal must not be empty", word.Span);
                            except.Add(new LiteralExpr(word.Value, word.Span));
                        }
                    }
                    var semicolon = Expect(TokenKind.Semicolon, "';'");
                    rules.Add(new TokenRule(ruleName, body, GrammarSpan.FromBounds(keyword.Start, semicolon.End), except.ToArray()));
                }
                else if (AtKeyword("syntax"))
                {
                    var keyword = Advance();
                    if (AtKeyword("function") && Next.Kind == TokenKind.Identifier && TokenAt(_position + 2).Kind == TokenKind.LParen)
                    {
                        ParseFunction(keyword, helpers, functions);
                        continue;
                    }
                    var (ruleName, body, clauses, semantics, end) = ParseRuleBody();
                    rules.Add(new SyntaxRule(ruleName, body, GrammarSpan.FromBounds(keyword.Start, end), clauses, semantics));
                }
                else if (AtKeyword("extensible"))
                {
                    var keyword = Advance();
                    ExpectKeyword("syntax");
                    string ruleName = ExpectIdentifier("a rule name");
                    var (properties, alternatives, end) = ParseAlternatives(allowProperties: true);
                    rules.Add(new ExtensibleRule(ruleName, alternatives, GrammarSpan.FromBounds(keyword.Start, end), properties));
                }
                else if (AtKeyword("extend"))
                {
                    var keyword = Advance();
                    ExpectKeyword("syntax");
                    string target = ExpectName("an extensible rule name");
                    var (_, alternatives, end) = ParseAlternatives(allowProperties: false);
                    extends.Add(new ExtendDecl(target, alternatives, GrammarSpan.FromBounds(keyword.Start, end)));
                }
                else
                {
                    throw Error("expected 'using', 'symbols', 'symbol', 'builtin', 'token', 'syntax', 'extensible', 'extend' or '}'");
                }
            }
            var close = Advance();
            return new ModuleDecl(name, usings.ToArray(), rules.ToArray(), extends.ToArray(),
                GrammarSpan.FromBounds(start.Start, close.End), symbols.ToArray(), builtins.ToArray(), symbolProperties.ToArray(), helpers.ToArray(), functions.ToArray());
        }

        (string Name, Expr Body, EquatableArray<BindingClause> Clauses, SemanticsBlock? Semantics, int End) ParseRuleBody()
        {
            string name = ExpectIdentifier("a rule name");
            Expect(TokenKind.Equals, "'='");
            var body = ParseExpr();
            var clauses = ParseClauses();
            if (At(TokenKind.LBrace))
            {
                var block = ParseSemantics();
                return (name, body, clauses.ToArray(), block, block.Span.End);
            }
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            return (name, body, clauses.ToArray(), null, semicolon.End);
        }

        (EquatableArray<PropertyDecl> Properties, EquatableArray<Alternative> Alternatives, int End) ParseAlternatives(bool allowProperties)
        {
            Expect(TokenKind.LBrace, "'{'");
            var properties = new List<PropertyDecl>();
            while (allowProperties && (AtKeyword("out") || AtKeyword("in"))) properties.Add(ParseProperty());
            var alternatives = new List<Alternative>();
            while (At(TokenKind.Bar)) alternatives.Add(ParseAlternative());
            var close = Expect(TokenKind.RBrace, "'|' or '}'");
            return (properties.ToArray(), alternatives.ToArray(), close.End);
        }

        Alternative ParseAlternative()
        {
            var bar = Advance();
            // `transparent` starts the alternative when a name or an element follows; otherwise it is a reference.
            bool transparent = AtKeyword("transparent")
                && ((Next.Kind == TokenKind.Identifier && TokenAt(_position + 2).Kind == TokenKind.Equals) || IsElementStart(Next));
            if (transparent) Advance();
            string? name = null;
            if (At(TokenKind.Identifier) && Next.Kind == TokenKind.Equals)
            {
                name = Advance().Value;
                Advance();
            }
            var body = ParseExpr();
            int end = body.Span.End;
            int? precedence = null;
            GrammarAssociativity? associativity = null;
            if (AtKeyword("precedence"))
            {
                Advance();
                var number = Expect(TokenKind.Integer, "a precedence number");
                if (number.Value.Length > 9) throw new GrammarSyntaxException("precedence is too large", number.Span);
                precedence = int.Parse(number.Value, CultureInfo.InvariantCulture);
                end = number.End;
                if (AtKeyword("left") || AtKeyword("right"))
                {
                    var word = Advance();
                    associativity = word.Value == "left" ? GrammarAssociativity.Left : GrammarAssociativity.Right;
                    end = word.End;
                }
            }
            var clauses = ParseClauses();
            if (clauses.Count > 0) end = clauses[clauses.Count - 1].Span.End;
            SemanticsBlock? semantics = null;
            if (At(TokenKind.LBrace))
            {
                semantics = ParseSemantics();
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
            return new Alternative(name, isImplicit, body, precedence, associativity, GrammarSpan.FromBounds(bar.Start, end), clauses.ToArray(), semantics,
                transparent);
        }

        Expr ParseExpr()
        {
            var first = ParseSequence();
            if (!At(TokenKind.Slash)) return first;
            var alternatives = new List<Expr> { first };
            while (At(TokenKind.Slash))
            {
                Advance();
                alternatives.Add(ParseSequence());
            }
            var last = alternatives[alternatives.Count - 1];
            return new ChoiceExpr(alternatives.ToArray(), GrammarSpan.FromBounds(first.Span.Start, last.Span.End));
        }

        Expr ParseSequence()
        {
            var items = new List<Expr> { ParseElement() };
            while (IsElementStart()) items.Add(ParseElement());
            if (items.Count == 1) return items[0];
            var last = items[items.Count - 1];
            return new SequenceExpr(items.ToArray(), GrammarSpan.FromBounds(items[0].Span.Start, last.Span.End));
        }

        bool IsElementStart() => IsElementStart(Current);

        static bool IsElementStart(GrammarToken token) => token.Kind switch
        {
            TokenKind.String or TokenKind.LBracket or TokenKind.LParen or TokenKind.Dot
                or TokenKind.Bang or TokenKind.Amp or TokenKind.QualifiedName => true,
            TokenKind.Identifier => CanUseUnqualifiedReference(token.Value),
            _ => false,
        };

        Expr ParseElement()
        {
            if (At(TokenKind.Identifier) && Next.Kind == TokenKind.Colon)
            {
                var label = Advance();
                Advance();
                var inner = ParseUnary();
                return new LabeledExpr(label.Value, inner, GrammarSpan.FromBounds(label.Start, inner.Span.End));
            }
            return ParseUnary();
        }

        Expr ParseUnary()
        {
            if (At(TokenKind.Bang) || At(TokenKind.Amp))
            {
                var op = Advance();
                var inner = ParseUnary();
                var kind = op.Kind == TokenKind.Bang ? PredicateKind.Not : PredicateKind.And;
                return new PredicateExpr(kind, inner, GrammarSpan.FromBounds(op.Start, inner.Span.End));
            }
            return ParsePostfix();
        }

        Expr ParsePostfix()
        {
            var expr = ParsePrimary();
            while (true)
            {
                RepeatKind kind;
                if (At(TokenKind.Question)) kind = RepeatKind.Optional;
                else if (At(TokenKind.Star)) kind = RepeatKind.ZeroOrMore;
                else if (At(TokenKind.Plus)) kind = RepeatKind.OneOrMore;
                else return expr;
                var op = Advance();
                expr = new RepeatExpr(kind, expr, GrammarSpan.FromBounds(expr.Span.Start, op.End));
            }
        }

        Expr ParsePrimary()
        {
            switch (Current.Kind)
            {
                case TokenKind.String:
                {
                    var token = Advance();
                    if (token.Value.Length == 0) throw new GrammarSyntaxException("a literal must not be empty", token.Span);
                    return new LiteralExpr(token.Value, token.Span);
                }
                case TokenKind.Identifier or TokenKind.QualifiedName when AtCall() && Attempt(ParseCall) is { } call:
                    return call;
                case TokenKind.Identifier when Current.Value == "if" && Attempt(ParseIf) is { } conditional:
                    return conditional;
                case TokenKind.Identifier when Current.Value is "choice" or "seq" && Attempt(ParseComprehension) is { } comprehension:
                    return comprehension;
                case TokenKind.Identifier:
                case TokenKind.QualifiedName:
                {
                    var token = Advance();
                    return new ReferenceExpr(token.Value, token.Span);
                }
                case TokenKind.Dot:
                    return new AnyCharExpr(Advance().Span);
                case TokenKind.LBracket:
                    return ParseCharClass();
                case TokenKind.LParen:
                    return ParseGroup();
                default:
                    throw Error("expected an element");
            }
        }

        Expr ParseCharClass()
        {
            var (items, negated, span) = ParseClassItems();
            if (items.Count == 0) throw new GrammarSyntaxException("a character class needs at least one character", span);
            if (items.All(i => i.First is CharValueExpr && i.Last is null or CharValueExpr))
                return new CharClassExpr(items.Select(i => new CharRange(((CharValueExpr)i.First).Value,
                    ((CharValueExpr?)i.Last ?? (CharValueExpr)i.First).Value)).ToArray(), negated, span);
            return new ValueClassExpr(items.ToArray(), negated, span);
        }

        /// <summary><c>[^? items]</c>; a bound is a character, a name, a call or <c>(value)</c> (spec: grammar functions).</summary>
        (List<ValueClassItem> Items, bool Negated, GrammarSpan Span) ParseClassItems()
        {
            var open = Advance();
            bool negated = false;
            if (At(TokenKind.Caret))
            {
                Advance();
                negated = true;
            }
            var items = new List<ValueClassItem>();
            while (Current.Kind is TokenKind.Char or TokenKind.Identifier or TokenKind.QualifiedName or TokenKind.LParen)
            {
                var first = ParseBound();
                ValueExpr? last = null;
                if (At(TokenKind.DotDot))
                {
                    Advance();
                    if (Current.Kind is not (TokenKind.Char or TokenKind.Identifier or TokenKind.QualifiedName or TokenKind.LParen))
                        throw Error("expected a character literal");
                    last = ParseBound();
                    if (first is CharValueExpr low && last is CharValueExpr high && high.Value < low.Value)
                        throw new GrammarSyntaxException("empty character range", GrammarSpan.FromBounds(first.Span.Start, last.Span.End));
                }
                items.Add(new ValueClassItem(first, last, GrammarSpan.FromBounds(first.Span.Start, (last ?? first).Span.End)));
            }
            var close = Expect(TokenKind.RBracket, "a character literal or ']'");
            return (items, negated, GrammarSpan.FromBounds(open.Start, close.End));
        }

        ValueExpr ParseBound()
        {
            if (At(TokenKind.Char))
            {
                var token = Advance();
                return new CharValueExpr(token.Value[0], token.Span);
            }
            if (At(TokenKind.LParen)) return ParseValueGroup();
            return ParseNameOrCall();
        }

        bool AtCall() => Current.Kind is TokenKind.Identifier or TokenKind.QualifiedName &&
            Next.Kind == TokenKind.LParen && Next.Start == Current.End;

        (NameDecl Name, ValueExpr[] Arguments, int End) ParseCallParts()
        {
            var name = Name(Advance());
            Advance();
            var arguments = new List<ValueExpr>();
            if (!At(TokenKind.RParen))
                do
                {
                    arguments.Add(ParseValue());
                    if (!At(TokenKind.Comma)) break;
                    Advance();
                } while (true);
            var close = Expect(TokenKind.RParen, "',' or ')'");
            return (name, arguments.ToArray(), close.End);
        }

        Expr ParseCall()
        {
            var (name, arguments, end) = ParseCallParts();
            return new GrammarCallExpr(name, arguments, GrammarSpan.FromBounds(name.Span.Start, end));
        }

        /// <summary>Tries a keyword-led form or a call; on failure rewinds so the word reads as a rule reference, as the self-hosted PEG does.</summary>
        Expr? Attempt(Func<Expr> parse)
        {
            int saved = _position;
            try
            {
                return parse();
            }
            catch (GrammarSyntaxException)
            {
                _position = saved;
                return null;
            }
        }

        Expr ParseIf()
        {
            int start = Advance().Start;
            var condition = ParseValue();
            ExpectKeyword("then");
            var then = ParseExpr();
            ExpectKeyword("else");
            var otherwise = ParseExpr();
            return new IfExpr(condition, then, otherwise, GrammarSpan.FromBounds(start, otherwise.Span.End));
        }

        Expr ParseComprehension()
        {
            var kind = Advance();
            var variable = Name(Expect(TokenKind.Identifier, "a variable name"));
            ExpectKeyword("in");
            var from = ParseValue();
            ValueExpr? to = null;
            if (At(TokenKind.DotDot))
            {
                Advance();
                to = ParseValue();
            }
            var block = ParseBlock();
            return new ComprehensionExpr(kind.Value == "choice", variable, from, to, block, GrammarSpan.FromBounds(kind.Start, block.Span.End));
        }

        FunctionBlock ParseBlock()
        {
            var open = Expect(TokenKind.LBrace, "'{'");
            var lets = ParseLets();
            ValueExpr? where = null;
            if (AtKeyword("where"))
            {
                Advance();
                where = ParseValue();
                Expect(TokenKind.Colon, "':'");
            }
            var body = ParseExpr();
            var close = Expect(TokenKind.RBrace, "'}'");
            return new FunctionBlock(lets, where, body, GrammarSpan.FromBounds(open.Start, close.End));
        }

        LetBinding[] ParseLets()
        {
            var lets = new List<LetBinding>();
            while (AtKeyword("let") && Next.Kind == TokenKind.Identifier && TokenAt(_position + 2).Kind == TokenKind.Equals)
            {
                int start = Advance().Start;
                var name = Name(Advance());
                Advance();
                var value = ParseValue();
                var semicolon = Expect(TokenKind.Semicolon, "';'");
                lets.Add(new LetBinding(name, value, GrammarSpan.FromBounds(start, semicolon.End)));
            }
            return lets.ToArray();
        }

        // ---- value expressions (spec: grammar functions) ----

        ValueExpr ParseValue()
        {
            if (!AtKeyword("if")) return ParseBinary(0);
            int start = Advance().Start;
            var condition = ParseValue();
            ExpectKeyword("then");
            var then = ParseValue();
            ExpectKeyword("else");
            var otherwise = ParseValue();
            return new IfValueExpr(condition, then, otherwise, GrammarSpan.FromBounds(start, otherwise.Span.End));
        }

        /// <summary>Binary levels from loosest: or, and, (not), comparison, |, &amp;, + -, * / %, ^ (right).</summary>
        ValueExpr ParseBinary(int level)
        {
            switch (level)
            {
                case 2 when AtKeyword("not"):
                {
                    var op = Advance();
                    var operand = ParseBinary(2);
                    return new UnaryValueExpr("not", operand, GrammarSpan.FromBounds(op.Start, operand.Span.End));
                }
                case 2:
                    return ParseBinary(3);
                case 3:
                {
                    var left = ParseBinary(4);
                    if (Current.Kind is not (TokenKind.EqualEqual or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessEqual
                        or TokenKind.Greater or TokenKind.GreaterEqual)) return left;
                    string op = Advance().Value;
                    var right = ParseBinary(4);
                    return new BinaryValueExpr(op, left, right, GrammarSpan.FromBounds(left.Span.Start, right.Span.End));
                }
                case 8:
                {
                    var left = ParseUnaryValue();
                    if (!At(TokenKind.Caret)) return left;
                    Advance();
                    var right = ParseBinary(8);
                    return new BinaryValueExpr("^", left, right, GrammarSpan.FromBounds(left.Span.Start, right.Span.End));
                }
            }
            var result = ParseBinary(level + 1);
            while (OperatorAt(level) is { } op)
            {
                Advance();
                var right = ParseBinary(level + 1);
                result = new BinaryValueExpr(op, result, right, GrammarSpan.FromBounds(result.Span.Start, right.Span.End));
            }
            return result;
        }

        string? OperatorAt(int level) => level switch
        {
            0 when AtKeyword("or") => "or",
            1 when AtKeyword("and") => "and",
            4 when At(TokenKind.Bar) => "|",
            5 when At(TokenKind.Amp) => "&",
            6 when Current.Kind is TokenKind.Plus or TokenKind.Minus => Current.Value,
            7 when Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent => Current.Value,
            _ => null,
        };

        ValueExpr ParseUnaryValue()
        {
            if (Current.Kind is TokenKind.Minus or TokenKind.Tilde)
            {
                var op = Advance();
                var operand = ParseUnaryValue();
                return new UnaryValueExpr(op.Value, operand, GrammarSpan.FromBounds(op.Start, operand.Span.End));
            }
            switch (Current.Kind)
            {
                case TokenKind.Integer:
                {
                    var token = Advance();
                    if (!long.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                        throw new GrammarSyntaxException("the integer is too large", token.Span);
                    return new IntValueExpr(value, token.Span);
                }
                case TokenKind.Char:
                {
                    var token = Advance();
                    return new CharValueExpr(token.Value[0], token.Span);
                }
                case TokenKind.String:
                {
                    var token = Advance();
                    return new TextValueExpr(token.Value, token.Span);
                }
                case TokenKind.LBracket:
                {
                    var (items, negated, span) = ParseClassItems();
                    return new ClassValueExpr(items.ToArray(), negated, span);
                }
                case TokenKind.LBrace:
                {
                    var block = ParseBlock();
                    return new RuleValueExpr(block, block.Span);
                }
                case TokenKind.LParen:
                    return ParseValueGroup();
                case TokenKind.Identifier or TokenKind.QualifiedName:
                    return ParseNameOrCall();
                default:
                    throw Error("expected a value");
            }
        }

        ValueExpr ParseValueGroup()
        {
            Advance();
            var inner = ParseValue();
            Expect(TokenKind.RParen, "')'");
            return inner;
        }

        ValueExpr ParseNameOrCall()
        {
            if (AtCall())
            {
                var (name, arguments, end) = ParseCallParts();
                return new CallValueExpr(name, arguments, GrammarSpan.FromBounds(name.Span.Start, end));
            }
            if (Current.Kind is not (TokenKind.Identifier or TokenKind.QualifiedName) || IsValueKeyword(Current.Value))
                throw Error("expected a value");
            var token = Advance();
            return new NameValueExpr(token.Value, token.Span);
        }

        Expr ParseGroup()
        {
            var open = Advance();
            if (At(TokenKind.RParen)) return new SequenceExpr(default, GrammarSpan.FromBounds(open.Start, Advance().End));
            var inner = ParseExpr();
            if (At(TokenKind.Semicolon))
            {
                Advance();
                var separator = ParseExpr();
                Expect(TokenKind.RParen, "')'");
                if (!At(TokenKind.Star) && !At(TokenKind.Plus)) throw Error("expected '*' or '+' after a separated list");
                var op = Advance();
                return new SeparatedListExpr(inner, separator, op.Kind == TokenKind.Plus, GrammarSpan.FromBounds(open.Start, op.End));
            }
            Expect(TokenKind.RParen, "')'");
            return inner;
        }

        static NameDecl Name(GrammarToken token) => new(token.Value, token.Span);

        /// <summary>
        /// <c>rewrites &lt;[ text ]&gt;</c> (spec: quasiquote rewrites): the text runs verbatim to the first <c>]&gt;</c>,
        /// never tokenized. Lexing resumes after it.
        /// </summary>
        /// <summary>
        /// <c>[syntax] function Name(...) : Type = Body;</c>. A <c>&lt;[</c> or <c>match</c> body is a quote helper; any other
        /// body is a grammar function, which returns <c>Grammar.Rule</c> (spec: grammar functions).
        /// </summary>
        void ParseFunction(GrammarToken keyword, List<QuoteHelperDecl> helpers, List<GrammarFunctionDecl> functions)
        {
            ExpectKeyword("function");
            var name = Name(Expect(TokenKind.Identifier, "a helper name"));
            var parameters = ReadQuoteParameters();
            Expect(TokenKind.Colon, "':'");
            var result = ReadCategory();
            var equals = Expect(TokenKind.Equals, "'='");
            int next = GrammarLexer.SkipTrivia(_text, equals.End);
            bool quote = string.CompareOrdinal(_text, next, "<[", 0, 2) == 0 ||
                AtKeyword("match") && Next.Kind == TokenKind.Identifier && TokenAt(_position + 2).Kind == TokenKind.LBrace;
            if (quote)
            {
                if (result.Name == GrammarFunctions.RuleType)
                    throw new GrammarSyntaxException("a quote helper cannot return Grammar.Rule; give it a grammar body", result.Span);
                helpers.Add(ParseQuoteHelperBody(keyword, name, parameters, result, equals, next));
                return;
            }
            if (result.Name != GrammarFunctions.RuleType)
                throw new GrammarSyntaxException("a function with a grammar body must return Grammar.Rule", result.Span);
            var lets = ParseLets();
            var body = ParseExpr();
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            functions.Add(new GrammarFunctionDecl(name, parameters, result, lets, body, GrammarSpan.FromBounds(keyword.Start, semicolon.End)));
        }

        QuoteHelperDecl ParseQuoteHelperBody(GrammarToken keyword, NameDecl name, QuoteParameter[] parameters, NameDecl result,
            GrammarToken equals, int next)
        {
            QuoteHelperMatch? match = null;
            QuoteText body;
            if (string.CompareOrdinal(_text, next, "match", 0, 5) == 0)
            {
                var matchKeyword = ExpectKeyword("match");
                var selector = Name(Expect(TokenKind.Identifier, "a scalar helper parameter"));
                Expect(TokenKind.LBrace, "'{'");
                var arms = new List<QuoteMatchArm>();
                QuoteText? fallback = null;
                while (!At(TokenKind.RBrace))
                {
                    var bar = Expect(TokenKind.Bar, "'|'");
                    if (fallback is not null)
                        throw new GrammarSyntaxException("the fallback must be the final arm", bar.Span);
                    int armStart = GrammarLexer.SkipTrivia(_text, bar.End);
                    if (armStart < _text.Length && _text[armStart] == '_')
                    {
                        ExpectKeyword("_");
                        var arrow = Expect(TokenKind.Arrow, "'=>'");
                        fallback = ReadQuote(arrow.End);
                        Expect(TokenKind.Semicolon, "';'");
                        continue;
                    }
                    bool hasCaptures = armStart < _text.Length && _text[armStart] == '(';
                    var captures = hasCaptures ? ReadQuoteParameters() : Array.Empty<QuoteParameter>();
                    int afterCaptures = hasCaptures ? _tokens[_position - 1].End : bar.End;
                    var pattern = ReadQuote(afterCaptures);
                    var resultArrow = Expect(TokenKind.Arrow, "'=>'");
                    var returned = ReadQuote(resultArrow.End);
                    var armEnd = Expect(TokenKind.Semicolon, "';'");
                    arms.Add(new(captures, pattern, returned, GrammarSpan.FromBounds(bar.Start, armEnd.End)));
                }
                var close = Expect(TokenKind.RBrace, "'}'");
                body = fallback ?? throw new GrammarSyntaxException("a match helper requires a final fallback", close.Span);
                match = new(selector, arms.ToArray(), GrammarSpan.FromBounds(matchKeyword.Start, close.End));
            }
            else body = ReadQuote(equals.End);
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            return new(name, parameters, result, body, GrammarSpan.FromBounds(keyword.Start, semicolon.End)) { Match = match };
        }

        QuoteParameter[] ReadQuoteParameters()
        {
            Expect(TokenKind.LParen, "'('");
            var parameters = new List<QuoteParameter>();
            if (!At(TokenKind.RParen))
                do
                {
                    var parameter = Name(Expect(TokenKind.Identifier, "a parameter name"));
                    Expect(TokenKind.Colon, "':'");
                    var category = ReadCategory();
                    bool list = At(TokenKind.Star);
                    int end = list ? Advance().End : category.Span.End;
                    parameters.Add(new(parameter, category, list, GrammarSpan.FromBounds(parameter.Span.Start, end)));
                    if (!At(TokenKind.Comma)) break;
                    Advance();
                } while (true);
            Expect(TokenKind.RParen, "')'");
            return parameters.ToArray();
        }

        NameDecl ReadCategory()
        {
            var token = Current;
            return new(ExpectName("a syntax category"), token.Span);
        }

        BindingClause Rewrite(GrammarToken keyword)
        {
            int open = GrammarLexer.SkipTrivia(_text, keyword.End);
            if (string.CompareOrdinal(_text, open, "<[", 0, 2) == 0)
            {
                var quote = ReadQuote(keyword.End);
                return new(BindingClauseKind.Rewrites, default, "", default, false, false,
                    GrammarSpan.FromBounds(keyword.Start, quote.Span.End + 2), Target: new(quote.Text, quote.Span));
            }
            var helper = ReadCategory();
            Expect(TokenKind.LParen, "'('");
            var arguments = new List<NameDecl>();
            if (!At(TokenKind.RParen))
                do
                {
                    arguments.Add(Name(Expect(TokenKind.Identifier, "a labeled field argument")));
                    if (!At(TokenKind.Comma)) break;
                    Advance();
                } while (true);
            var close = Expect(TokenKind.RParen, "')'");
            return new(BindingClauseKind.Rewrites, default, "", default, false, false,
                GrammarSpan.FromBounds(keyword.Start, close.End),
                HelperCall: new(helper, arguments.ToArray(), GrammarSpan.FromBounds(helper.Span.Start, close.End)));
        }

        QuoteText ReadQuote(int after)
        {
            int open = GrammarLexer.SkipTrivia(_text, after);
            if (string.CompareOrdinal(_text, open, "<[", 0, 2) != 0)
                throw new GrammarSyntaxException("expected '<['", new GrammarSpan(open, 0));
            int start = open + 2, close = _text.IndexOf("]>", start, StringComparison.Ordinal);
            if (close < 0) throw new GrammarSyntaxException("expected ']>' to close the template", new GrammarSpan(open, 2));
            _tokens.RemoveRange(_position, _tokens.Count - _position);
            _lexed = close + 2;
            return new(_text.Substring(start, close - start), GrammarSpan.FromBounds(start, close));
        }

        /// <summary>Binding clauses (issue 237) after a rule body or an alternative; none is fine.</summary>
        LoweringArgument ParseLoweringArgument()
        {
            if (AtKeyword("optional") && Next.Kind == TokenKind.Identifier && Next.Value == "text" &&
                TokenAt(_position + 2).Kind == TokenKind.Identifier)
            {
                Advance();
                Advance();
                var field = Name(Advance());
                return new LoweringArgument(field.Name, field.Span, OptionalText: true);
            }
            if (AtKeyword("optional") && Next.Kind is TokenKind.Identifier or TokenKind.QualifiedName &&
                TokenAt(_position + 2).Kind == TokenKind.Identifier)
            {
                Advance();
                var type = Name(Advance());
                var field = Name(Advance());
                return new LoweringArgument(field.Name, field.Span, OptionalElementType: type);
            }
            if (AtKeyword("text") && Next.Kind == TokenKind.Identifier)
            {
                Advance();
                var field = Name(Advance());
                return new LoweringArgument(field.Name, field.Span, AsText: true);
            }
            if (AtKeyword("sequence") && Next.Kind is TokenKind.Identifier or TokenKind.QualifiedName &&
                TokenAt(_position + 2).Kind == TokenKind.Identifier)
            {
                Advance();
                var type = Name(Advance());
                var field = Name(Advance());
                return type.Name == "inferred"
                    ? new LoweringArgument(field.Name, field.Span, InferSequence: true)
                    : new LoweringArgument(field.Name, field.Span, type);
            }
            var direct = Name(Expect(TokenKind.Identifier, "a field label"));
            return new LoweringArgument(direct.Name, direct.Span);
        }

        List<BindingClause> ParseClauses()
        {
            var clauses = new List<BindingClause>();
            while (true)
            {
                if (AtKeyword("declares"))
                {
                    var keyword = Advance();
                    var kind = Name(Expect(TokenKind.Identifier, "a symbol kind"));
                    var field = Expect(TokenKind.Identifier, "a field label or 'this'");
                    int end = field.End;
                    bool sequential = false;
                    if (AtKeyword("sequential"))
                    {
                        end = Advance().End;
                        sequential = true;
                    }
                    bool fileScope = false;
                    if (AtKeyword("in"))
                    {
                        Advance();
                        end = ExpectKeyword("file").End;
                        fileScope = true;
                    }
                    bool export = false;
                    if (AtKeyword("export"))
                    {
                        end = Advance().End;
                        export = true;
                    }
                    NameDecl? type = null;
                    if (AtKeyword("type"))
                    {
                        Advance();
                        if (!At(TokenKind.Identifier) && !At(TokenKind.QualifiedName)) throw Error("expected a field label or a qualified type name");
                        var name = Advance();
                        type = Name(name);
                        end = name.End;
                    }
                    clauses.Add(new BindingClause(BindingClauseKind.Declares, new[] { kind }, field.Value, field.Span,
                        false, export, GrammarSpan.FromBounds(keyword.Start, end), Target: type, FileScope: fileScope,
                        Sequential: sequential));
                }
                else if (AtKeyword("references"))
                {
                    var keyword = Advance();
                    bool optional = false;
                    if (At(TokenKind.Question))
                    {
                        Advance();
                        optional = true;
                    }
                    var kinds = ParseKinds();
                    var field = Expect(TokenKind.Identifier, "a field label or 'this'");
                    int end = field.End;
                    NameDecl? qualifier = null;
                    if (AtKeyword("in"))
                    {
                        Advance();
                        var kind = Expect(TokenKind.Identifier, "a symbol kind");
                        qualifier = Name(kind);
                        end = kind.End;
                    }
                    clauses.Add(new BindingClause(BindingClauseKind.References, kinds.ToArray(), field.Value, field.Span,
                        optional, false, GrammarSpan.FromBounds(keyword.Start, end), qualifier));
                }
                else if (AtKeyword("lowers"))
                {
                    var keyword = Advance();
                    if (AtKeyword("repeat"))
                    {
                        Advance();
                        var element = Advance();
                        if (element.Kind is not (TokenKind.Identifier or TokenKind.QualifiedName)) throw Error("expected a repeat element type");
                        var count = Expect(TokenKind.Identifier, "a count field");
                        var iterator = Expect(TokenKind.Identifier, "an iterator declaration field");
                        var children = Expect(TokenKind.Identifier, "a template list field");
                        clauses.Add(new BindingClause(BindingClauseKind.LowersRepeat, default, "", default, false, false,
                            GrammarSpan.FromBounds(keyword.Start, children.End), Target: Name(element), Arguments: new[]
                            {
                                new LoweringArgument(count.Value, count.Span),
                                new LoweringArgument(iterator.Value, iterator.Span),
                                new LoweringArgument(children.Value, children.Span, SequenceElementType: Name(element)),
                            }));
                    }
                    else if ((AtKeyword("template") || AtKeyword("expand")) && Next.Kind == TokenKind.Identifier)
                    {
                        // lowers template Body(Params) / lowers expand Name(Args)
                        var kind = Advance().Value == "template" ? BindingClauseKind.LowersTemplate : BindingClauseKind.LowersExpand;
                        var field = Expect(TokenKind.Identifier, "a field label");
                        Expect(TokenKind.LParen, "'('");
                        var list = Expect(TokenKind.Identifier, "a list field label");
                        var close = Expect(TokenKind.RParen, "')'");
                        clauses.Add(new BindingClause(kind, default, field.Value, field.Span, false, false,
                            GrammarSpan.FromBounds(keyword.Start, close.End), Arguments: new[] { new LoweringArgument(list.Value, list.Span) }));
                    }
                    else if (AtKeyword("value") && Next.Value == "type")
                    {
                        Advance();
                        Advance();
                        var typeProperty = Expect(TokenKind.Identifier, "a semantic type property");
                        var valueProperty = Expect(TokenKind.Identifier, "a float value property");
                        clauses.Add(new BindingClause(BindingClauseKind.LowersValue, default,
                            valueProperty.Value, valueProperty.Span, false, false,
                            GrammarSpan.FromBounds(keyword.Start, valueProperty.End),
                            TypeProperty: Name(typeProperty)));
                    }
                    else if (AtKeyword("reference") && Next.Value == "type")
                    {
                        Advance();
                        Advance();
                        var typeProperty = Expect(TokenKind.Identifier, "a semantic type property");
                        NameDecl? initializer = null;
                        if (AtKeyword("initializer"))
                        {
                            Advance();
                            initializer = Name(Expect(TokenKind.Identifier, "an initializer node property"));
                        }
                        clauses.Add(new BindingClause(BindingClauseKind.LowersReference, default,
                            "", default, false, false, GrammarSpan.FromBounds(keyword.Start, initializer?.Span.End ?? typeProperty.End),
                            TypeProperty: Name(typeProperty), InitializerProperty: initializer));
                    }
                    else
                    if ((AtKeyword("literal") || AtKeyword("text") || AtKeyword("sequence") || AtKeyword("value")) &&
                        Next.Kind is TokenKind.Identifier or TokenKind.QualifiedName)
                    {
                        var form = Advance().Value;
                        var type = Name(Advance());
                        var field = Expect(TokenKind.Identifier, "a field label or 'this'");
                        var kind = form switch
                        {
                            "text" => BindingClauseKind.LowersText,
                            "sequence" => BindingClauseKind.LowersSequence,
                            "value" => BindingClauseKind.LowersValue,
                            _ => BindingClauseKind.LowersLiteral,
                        };
                        clauses.Add(new BindingClause(kind, default, field.Value, field.Span,
                            false, false, GrammarSpan.FromBounds(keyword.Start, field.End), Target: type));
                    }
                    else
                    {
                        if (!At(TokenKind.Identifier) && !At(TokenKind.QualifiedName)) throw Error("expected an operation name or 'literal'");
                        bool computed = AtKeyword("operation");
                        if (computed) Advance();
                        bool optionalOperation = computed && At(TokenKind.Question);
                        if (optionalOperation) Advance();
                        var operation = computed ? Name(Expect(TokenKind.Identifier, "an operation property")) : Name(Advance());
                        Expect(TokenKind.LParen, "'('");
                        var arguments = new List<LoweringArgument>();
                        if (!At(TokenKind.RParen))
                        {
                            arguments.Add(ParseLoweringArgument());
                            while (At(TokenKind.Comma))
                            {
                                Advance();
                                arguments.Add(ParseLoweringArgument());
                            }
                        }
                        var close = Expect(TokenKind.RParen, "',' or ')'");
                        clauses.Add(new BindingClause(BindingClauseKind.Lowers, default, "", default, optionalOperation, false,
                            GrammarSpan.FromBounds(keyword.Start, close.End),
                            Target: computed ? null : operation, Arguments: arguments.ToArray(),
                            OperationProperty: computed ? operation : null));
                    }
                }
                else if (AtKeyword("rewrites"))
                {
                    var keyword = Advance();
                    clauses.Add(Rewrite(keyword));
                }
                else if (AtKeyword("scope") || AtKeyword("dynamic"))
                {
                    var keyword = Advance();
                    var kind = keyword.Value == "scope" ? BindingClauseKind.Scope : BindingClauseKind.Dynamic;
                    clauses.Add(new BindingClause(kind, default, "", default, false, false, keyword.Span));
                }
                else
                {
                    return clauses;
                }
            }
        }

        /// <summary><c>kind</c> or <c>(a | b …)</c>.</summary>
        List<NameDecl> ParseKinds()
        {
            var kinds = new List<NameDecl>();
            if (At(TokenKind.LParen))
            {
                Advance();
                kinds.Add(Name(Expect(TokenKind.Identifier, "a symbol kind")));
                while (At(TokenKind.Bar))
                {
                    Advance();
                    kinds.Add(Name(Expect(TokenKind.Identifier, "a symbol kind")));
                }
                Expect(TokenKind.RParen, "'|' or ')'");
            }
            else
            {
                kinds.Add(Name(Expect(TokenKind.Identifier, "a symbol kind")));
            }
            return kinds;
        }

        /// <summary><c>{ property… statement… }</c> after a syntax rule or an alternative (issue 239).</summary>
        SemanticsBlock ParseSemantics()
        {
            var open = Expect(TokenKind.LBrace, "'{'");
            var properties = new List<PropertyDecl>();
            var statements = new List<SemanticStatement>();
            while (!At(TokenKind.RBrace))
            {
                if (AtKeyword("out") || AtKeyword("in")) properties.Add(ParseProperty());
                else if (AtKeyword("check")) statements.Add(ParseCheck());
                else if (AtKeyword("match") && Next.Kind != TokenKind.Equals) statements.Add(ParseMatchDiagnostic());
                else if ((At(TokenKind.Identifier) || At(TokenKind.QualifiedName)) && Next.Kind == TokenKind.Equals) statements.Add(ParseAssign());
                else throw Error("expected 'out', 'in', 'check', 'match', an assignment or '}'");
            }
            var close = Advance();
            return new SemanticsBlock(properties.ToArray(), statements.ToArray(), GrammarSpan.FromBounds(open.Start, close.End));
        }

        PropertyDecl ParseProperty()
        {
            var keyword = Advance();
            var direction = keyword.Value == "out" ? PropertyDirection.Out : PropertyDirection.In;
            bool hover = false, expected = false;
            while (AtKeyword("hover") || AtKeyword("expected"))
            {
                if (Advance().Value == "hover") hover = true;
                else expected = true;
            }
            var name = Name(Expect(TokenKind.Identifier, "a property name"));
            Expect(TokenKind.Colon, "':'");
            var type = Code(AfterLast, CSharpCapture.Type, "a C# type");
            Expect(TokenKind.Equals, "'='");
            var value = Code(AfterLast, CSharpCapture.Statement, "a C# default value");
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            return new PropertyDecl(direction, name, type, value, hover, expected, GrammarSpan.FromBounds(keyword.Start, semicolon.End));
        }

        MatchDiagnosticStatement ParseMatchDiagnostic()
        {
            var keyword = Advance();
            bool hasCaptures = At(TokenKind.LParen);
            var captures = hasCaptures ? ReadQuoteParameters() : Array.Empty<QuoteParameter>();
            var pattern = ReadQuote(hasCaptures ? _tokens[_position - 1].End : keyword.End);
            Expect(TokenKind.Arrow, "'=>'");
            if (!AtKeyword("warning") && !AtKeyword("error")) throw Error("expected 'warning' or 'error'");
            bool warning = Advance().Value == "warning";
            var code = Name(Expect(TokenKind.Identifier, "a diagnostic code"));
            if (!IsCheckCode(code.Name)) throw new GrammarSyntaxException("expected a diagnostic code such as XML1001", code.Span);
            var colon = Expect(TokenKind.Colon, "':'");
            var message = Code(colon.End, CSharpCapture.Statement, "a diagnostic message");
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            return new(captures, pattern, code, warning, message, GrammarSpan.FromBounds(keyword.Start, semicolon.End));
        }

        CheckStatement ParseCheck()
        {
            var keyword = Advance();
            // The code is read from the text: the condition after it is C#, which the lexer must not see.
            int from = GrammarLexer.SkipTrivia(_text, keyword.End);
            int word = from;
            while (word < _text.Length && GrammarLexer.IsIdentifierPart(_text[word])) word++;
            NameDecl? code = null;
            if (IsCheckCode(_text.Substring(from, word - from)))
            {
                code = new NameDecl(_text.Substring(from, word - from), GrammarSpan.FromBounds(from, word));
                from = word;
            }
            var condition = Code(from, CSharpCapture.Condition, "a C# condition");
            Expect(TokenKind.Colon, "':'");
            var (message, at) = SplitAt(Code(AfterLast, CSharpCapture.Statement, "a C# message"));
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            return new CheckStatement(code, condition, message, GrammarSpan.FromBounds(keyword.Start, semicolon.End), at);
        }

        static readonly System.Text.RegularExpressions.Regex AtTail =
            new(@"\s+at\s+([A-Za-z_][A-Za-z0-9_]*)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>A trailing <c>at Name</c> names the child a check reports at (issue 241). A string never ends in it, so it is never split.</summary>
        static (CodeText Message, NameDecl? At) SplitAt(CodeText message)
        {
            var match = AtTail.Match(message.Text);
            if (!match.Success) return (message, null);
            string text = message.Text.Substring(0, match.Index).TrimEnd();
            var child = match.Groups[1];
            return (new CodeText(text, new GrammarSpan(message.Span.Start, text.Length)),
                new NameDecl(child.Value, new GrammarSpan(message.Span.Start + child.Index, child.Length)));
        }

        /// <summary>A diagnostic code: two or more capital letters, then four digits (MT0001).</summary>
        static bool IsCheckCode(string word)
        {
            int letters = 0;
            while (letters < word.Length && word[letters] >= 'A' && word[letters] <= 'Z') letters++;
            if (letters < 2 || word.Length - letters != 4) return false;
            for (int i = letters; i < word.Length; i++)
                if (word[i] < '0' || word[i] > '9') return false;
            return true;
        }

        AssignStatement ParseAssign()
        {
            var target = Advance();
            Advance(); // '='
            var value = Code(AfterLast, CSharpCapture.Statement, "a C# value");
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            var span = GrammarSpan.FromBounds(target.Start, semicolon.End);
            string[] parts = target.Value.Split('.');
            if (parts.Length == 1) return new AssignStatement(AssignTarget.Self, null, Name(target), value, span);
            if (parts.Length > 2)
                throw new GrammarSyntaxException("an assignment target is Property, Child.Property or symbol.Property", target.Span);
            var property = new NameDecl(parts[1], GrammarSpan.FromBounds(target.Start + parts[0].Length + 1, target.End));
            if (parts[0] == "symbol") return new AssignStatement(AssignTarget.Symbol, null, property, value, span);
            return new AssignStatement(AssignTarget.Child, new NameDecl(parts[0], new GrammarSpan(target.Start, parts[0].Length)), property, value, span);
        }

        SymbolPropertyDecl ParseSymbolProperty()
        {
            var keyword = Advance();
            ExpectKeyword("property");
            var name = Name(Expect(TokenKind.Identifier, "a property name"));
            ExpectKeyword("for");
            var kinds = ParseKinds();
            Expect(TokenKind.Colon, "':'");
            var type = Code(AfterLast, CSharpCapture.Type, "a C# type");
            Expect(TokenKind.Equals, "'='");
            var value = Code(AfterLast, CSharpCapture.Statement, "a C# default value");
            var semicolon = Expect(TokenKind.Semicolon, "';'");
            return new SymbolPropertyDecl(name, kinds.ToArray(), type, value, GrammarSpan.FromBounds(keyword.Start, semicolon.End));
        }

        bool At(TokenKind kind) => Current.Kind == kind;

        bool AtKeyword(string word) => Current.Kind == TokenKind.Identifier && Current.Value == word;

        GrammarToken Advance()
        {
            var token = Current;
            if (token.Kind != TokenKind.End) _position++;
            return token;
        }

        GrammarToken Expect(TokenKind kind, string what)
        {
            if (!At(kind)) throw Error("expected " + what);
            return Advance();
        }

        GrammarToken ExpectKeyword(string word)
        {
            if (!AtKeyword(word)) throw Error($"expected '{word}'");
            return Advance();
        }

        string ExpectIdentifier(string what)
        {
            if (!At(TokenKind.Identifier)) throw Error("expected " + what);
            return Advance().Value;
        }

        string ExpectName(string what)
        {
            if (!At(TokenKind.Identifier) && !At(TokenKind.QualifiedName)) throw Error("expected " + what);
            return Advance().Value;
        }

        GrammarSyntaxException Error(string expected) => new($"{expected}, found {Describe(Current)}", Current.Span);

        static string Describe(GrammarToken token) => token.Kind switch
        {
            TokenKind.End => "end of input",
            TokenKind.String => "a string literal",
            TokenKind.Char => "a character literal",
            _ => $"'{token.Value}'",
        };
    }
}
