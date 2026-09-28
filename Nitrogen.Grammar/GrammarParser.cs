using System.Globalization;

namespace Nitrogen.Grammar;

public sealed record GrammarParseResult(GrammarFile? File, EquatableArray<GrammarDiagnostic> Diagnostics)
{
    public bool Success => File is not null;
}

/// <summary>The bootstrap parser for <c>.ngr</c> files. It stops at the first syntax error.</summary>
public static class GrammarParser
{
    public static GrammarParseResult Parse(string text)
    {
        try
        {
            var parser = new Parser(text);
            return new GrammarParseResult(parser.ParseFile(), default);
        }
        catch (GrammarSyntaxException error)
        {
            var diagnostic = new GrammarDiagnostic(GrammarCodes.Syntax, GrammarSeverity.Error, error.Message, error.Span);
            return new GrammarParseResult(null, new[] { diagnostic });
        }
    }

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

        public GrammarFile ParseFile()
        {
            var modules = new List<ModuleDecl>();
            while (!At(TokenKind.End)) modules.Add(ParseModule());
            return new GrammarFile(modules.ToArray());
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
            while (!At(TokenKind.RBrace))
            {
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
                GrammarSpan.FromBounds(start.Start, close.End), symbols.ToArray(), builtins.ToArray(), symbolProperties.ToArray());
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
            return new Alternative(name, isImplicit, body, precedence, associativity, GrammarSpan.FromBounds(bar.Start, end), clauses.ToArray(), semantics);
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

        bool IsElementStart() => Current.Kind switch
        {
            TokenKind.String or TokenKind.LBracket or TokenKind.LParen or TokenKind.Dot
                or TokenKind.Bang or TokenKind.Amp or TokenKind.QualifiedName => true,
            TokenKind.Identifier => Current.Value != "precedence" && Current.Value != "except" && !IsClauseWord(Current.Value),
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
            var open = Advance();
            bool negated = false;
            if (At(TokenKind.Caret))
            {
                Advance();
                negated = true;
            }
            var ranges = new List<CharRange>();
            while (At(TokenKind.Char))
            {
                var first = Advance();
                char low = first.Value[0], high = low;
                if (At(TokenKind.DotDot))
                {
                    Advance();
                    var last = Expect(TokenKind.Char, "a character literal");
                    high = last.Value[0];
                    if (high < low) throw new GrammarSyntaxException("empty character range", GrammarSpan.FromBounds(first.Start, last.End));
                }
                ranges.Add(new CharRange(low, high));
            }
            var close = Expect(TokenKind.RBracket, "a character literal or ']'");
            if (ranges.Count == 0)
                throw new GrammarSyntaxException("a character class needs at least one character", GrammarSpan.FromBounds(open.Start, close.End));
            return new CharClassExpr(ranges.ToArray(), negated, GrammarSpan.FromBounds(open.Start, close.End));
        }

        Expr ParseGroup()
        {
            var open = Advance();
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

        static bool IsClauseWord(string word) => word is "declares" or "references" or "scope" or "dynamic" or "lowers";

        static NameDecl Name(GrammarToken token) => new(token.Value, token.Span);

        /// <summary>Binding clauses (issue 237) after a rule body or an alternative; none is fine.</summary>
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
                        false, export, GrammarSpan.FromBounds(keyword.Start, end), Target: type));
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
                    if (AtKeyword("literal") && Next.Kind is TokenKind.Identifier or TokenKind.QualifiedName)
                    {
                        Advance();
                        var type = Name(Advance());
                        var field = Expect(TokenKind.Identifier, "a field label or 'this'");
                        clauses.Add(new BindingClause(BindingClauseKind.LowersLiteral, default, field.Value, field.Span,
                            false, false, GrammarSpan.FromBounds(keyword.Start, field.End), Target: type));
                    }
                    else
                    {
                        if (!At(TokenKind.Identifier) && !At(TokenKind.QualifiedName)) throw Error("expected an operation name or 'literal'");
                        var operation = Name(Advance());
                        Expect(TokenKind.LParen, "'('");
                        var arguments = new List<NameDecl>();
                        if (!At(TokenKind.RParen))
                        {
                            arguments.Add(Name(Expect(TokenKind.Identifier, "a field label")));
                            while (At(TokenKind.Comma))
                            {
                                Advance();
                                arguments.Add(Name(Expect(TokenKind.Identifier, "a field label")));
                            }
                        }
                        var close = Expect(TokenKind.RParen, "',' or ')'");
                        clauses.Add(new BindingClause(BindingClauseKind.Lowers, default, "", default, false, false,
                            GrammarSpan.FromBounds(keyword.Start, close.End), Target: operation, Arguments: arguments.ToArray()));
                    }
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
                else if ((At(TokenKind.Identifier) || At(TokenKind.QualifiedName)) && Next.Kind == TokenKind.Equals) statements.Add(ParseAssign());
                else throw Error("expected 'out', 'in', 'check', an assignment or '}'");
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
