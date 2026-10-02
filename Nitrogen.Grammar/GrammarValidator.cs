namespace Nitrogen.Grammar;

/// <summary>Validates a set of modules together, so cross-module references and extensions resolve.</summary>
public static class GrammarValidator
{
    public static EquatableArray<GrammarDiagnostic> Validate(IEnumerable<ModuleDecl> modules) =>
        new Validation(modules).Run();

    sealed class Validation
    {
        readonly List<GrammarDiagnostic> _diagnostics = new();
        readonly GrammarAnalysis _analysis;

        public Validation(IEnumerable<ModuleDecl> modules)
        {
            _analysis = new GrammarAnalysis(modules, Report);
        }

        public EquatableArray<GrammarDiagnostic> Run()
        {
            foreach (var module in _analysis.PrimaryModules) CheckModule(module);
            CheckAlternativeNames();
            CheckLeftRecursion();
            CheckPropertyFlags();
            return _diagnostics.ToArray();
        }

        void CheckModule(ModuleDecl module)
        {
            foreach (var u in module.Usings)
                if (!_analysis.HasModule(u.Module))
                    Report(GrammarCodes.UnknownModule, $"unknown module '{u.Module}'", u.Span, module);

            CheckSymbols(module);

            foreach (var rule in _analysis.PrimaryRules(module))
            {
                switch (rule)
                {
                    case TokenRule token:
                        CheckExpr(token.Body, module, null, inToken: true);
                        break;
                    case SyntaxRule syntax:
                        CheckExpr(syntax.Body, module, null, inToken: false);
                        CheckClauses(syntax.Clauses, syntax.Body, module, syntax, EmitModel.IsAlias(syntax));
                        CheckSemantics(syntax.Semantics, syntax.Body, SemanticsWriter.PropertiesOf(syntax), syntax.Clauses, module, null, syntax.Name,
                            syntax.Name + "Node", syntax.Span, isAlternative: false, EmitModel.IsAlias(syntax));
                        break;
                    case ExtensibleRule extensible:
                        CheckPropertyDecls(extensible.Properties, module);
                        CheckAlternatives(extensible.Alternatives, module, module.Name + "." + extensible.Name, module.Name, extensible);
                        break;
                }
            }

            foreach (var extend in module.Extends)
            {
                var target = _analysis.Resolve(extend.Target, extend.Span, module, null, Report);
                if (target is null) continue;
                if (target.Rule is not ExtensibleRule)
                {
                    Report(GrammarCodes.NotExtensible, $"'{extend.Target}' is not an extensible rule", extend.Span, module);
                    continue;
                }
                CheckAlternatives(extend.Alternatives, module, target.Key, target.Module.Name, (ExtensibleRule)target.Rule);
            }
        }

        void CheckAlternatives(EquatableArray<Alternative> alternatives, ModuleDecl module, string pointKey, string implicitModule, ExtensibleRule point)
        {
            var partialTypeProperties = new HashSet<string>(alternatives.SelectMany(alternative => alternative.Clauses)
                .SelectMany(clause => new[] { clause.TypeProperty, clause.OperationProperty })
                .Where(property => property is not null)
                .Select(property => property!.Name), StringComparer.Ordinal);
            foreach (var alternative in alternatives)
            {
                if (alternative.Name.Length == 0)
                    Report(GrammarCodes.AlternativeNeedsName,
                        "an alternative that is not a single rule reference needs a name: '| Name = ...'", alternative.Span, module);

                CheckExpr(alternative.Body, module, implicitModule, inToken: false);
                CheckClauses(alternative.Clauses, alternative.Body, module, point, isAlias: false);

                bool postfix = _analysis.IsPostfix(alternative, module, implicitModule, pointKey);
                if (alternative.Precedence is int precedence && (precedence < 0 || precedence > 255))
                    Report(GrammarCodes.BadPrecedence, $"precedence {precedence} is outside 0..255", alternative.Span, module);
                else if (postfix && (alternative.Precedence ?? 0) == 0)
                    Report(GrammarCodes.BadPrecedence,
                        $"postfix alternative '{alternative.Name}' needs a precedence of 1..255", alternative.Span, module);

                if (!postfix && _analysis.IsNullable(alternative.Body, module, implicitModule))
                    Report(GrammarCodes.NullablePrefix, $"alternative '{alternative.Name}' can match empty input", alternative.Span, module);

                CheckSemantics(alternative.Semantics, alternative.Body, point.Properties, alternative.Clauses, module, implicitModule,
                    alternative.Name, alternative.Name + point.Name, alternative.Span, isAlternative: true, isAlias: false,
                    partialTypeProperties: partialTypeProperties);
            }
        }

        /// <summary>The module's symbol kinds and those of the modules it uses (issue 237).</summary>
        HashSet<string> SymbolKinds(ModuleDecl module)
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var decl in module.Symbols)
                foreach (var kind in decl.Kinds) kinds.Add(kind.Name);
            foreach (var u in module.Usings)
                if (_analysis.FindModule(u.Module) is { } used)
                    foreach (var decl in used.Symbols)
                        foreach (var kind in decl.Kinds) kinds.Add(kind.Name);
            return kinds;
        }

        void CheckSymbols(ModuleDecl module)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var decl in module.Symbols)
                foreach (var kind in decl.Kinds)
                    if (!declared.Add(kind.Name))
                        Report(GrammarCodes.DuplicateSymbolKind, $"symbol kind '{kind.Name}' is declared twice", kind.Span, module);

            var kinds = SymbolKinds(module);
            var symbolProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in module.Usings)
                if (_analysis.FindModule(u.Module) is { } used)
                    foreach (var property in used.SymbolProperties) symbolProperties.Add(property.Name.Name);
            foreach (var property in module.SymbolProperties)
            {
                foreach (var kind in property.Kinds)
                    if (!kinds.Contains(kind.Name))
                        Report(GrammarCodes.UnknownSymbolKind, $"unknown symbol kind '{kind.Name}'", kind.Span, module);
                if (!symbolProperties.Add(property.Name.Name))
                    Report(GrammarCodes.DuplicateProperty,
                        $"symbol property '{property.Name.Name}' is declared twice; list every kind in one declaration", property.Name.Span, module);
            }

            var builtins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var builtin in module.Builtins)
            {
                if (!kinds.Contains(builtin.Kind.Name))
                    Report(GrammarCodes.UnknownSymbolKind, $"unknown symbol kind '{builtin.Kind.Name}'", builtin.Kind.Span, module);
                if (builtin.Scope is { } scope && !OpensScope(module, scope.Name))
                    Report(GrammarCodes.BadBuiltinScope,
                        $"'{scope.Name}' is not a syntax rule of this module that opens a scope", scope.Span, module);
                foreach (var name in builtin.Names)
                    if (!builtins.Add(builtin.Kind.Name + " " + builtin.Scope?.Name + " " + name.Name))
                        Report(GrammarCodes.DuplicateBuiltin, $"built-in {builtin.Kind.Name} '{name.Name}' is declared twice", name.Span, module);
            }
        }

        void CheckPropertyDecls(IReadOnlyList<PropertyDecl> properties, ModuleDecl module)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties)
                if (!names.Add(property.Name.Name))
                    Report(GrammarCodes.DuplicateProperty, $"property '{property.Name.Name}' is declared twice", property.Name.Span, module);
        }

        /// <summary>A rule's or alternative's semantics block (issue 239); <paramref name="own"/> are the properties its node has.</summary>
        void CheckSemantics(SemanticsBlock? block, Expr body, IReadOnlyList<PropertyDecl> own, EquatableArray<BindingClause> clauses,
            ModuleDecl module, string? implicitModule, string ruleName, string viewName, GrammarSpan ruleSpan, bool isAlternative, bool isAlias,
            HashSet<string>? partialTypeProperties = null)
        {
            if (isAlias)
            {
                if (block is not null)
                    Report(GrammarCodes.SemanticsOnAlias,
                        "a rule that is one reference or a choice of references produces no node, so it has no semantics", block.Span, module);
                return;
            }

            var defined = new HashSet<string>(StringComparer.Ordinal);
            if (block is not null)
            {
                if (isAlternative)
                    foreach (var property in block.Properties)
                        Report(GrammarCodes.PropertyOnAlternative,
                            $"declare property '{property.Name.Name}' on the extensible rule: its alternatives share it", property.Name.Span, module);
                else
                    CheckPropertyDecls(block.Properties, module);

                var children = new Dictionary<string, Expr>(StringComparer.Ordinal);
                foreach (var (name, element, _) in ViewWriter.ChildSlots(SyntaxCodeWriter.Elements(body), viewName))
                    if (!children.ContainsKey(name)) children[name] = element;

                var assigned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var assign in block.Statements.OfType<AssignStatement>())
                {
                    string target = assign.Target == AssignTarget.Child ? assign.Child!.Name + "." : assign.Target == AssignTarget.Symbol ? "symbol." : "";
                    if (!assigned.Add(target + assign.Property.Name))
                        Report(GrammarCodes.DuplicateProperty, $"'{target}{assign.Property.Name}' is assigned twice", assign.Span, module);

                    if (assign.Target == AssignTarget.Self)
                    {
                        CheckAssign(own, assign.Property, PropertyDirection.Out, ruleName, module);
                        defined.Add(assign.Property.Name);
                    }
                    else if (assign.Target == AssignTarget.Symbol)
                    {
                        CheckSymbolAssign(assign.Property, clauses, module);
                    }
                    else if (!children.TryGetValue(assign.Child!.Name, out var element))
                    {
                        Report(GrammarCodes.UnknownSemanticChild, $"'{assign.Child.Name}' is not a child of {ruleName}", assign.Child.Span, module);
                    }
                    else if (ChildRule(element, module, implicitModule) is not { } child)
                    {
                        Report(GrammarCodes.UnknownSemanticChild,
                            $"'{assign.Child.Name}' is not one syntax rule reference, so it has no properties", assign.Child.Span, module);
                    }
                    else
                    {
                        CheckAssign(SemanticsWriter.PropertiesOf(child), assign.Property, PropertyDirection.In, child.Name, module);
                    }
                }

                foreach (var check in block.Statements.OfType<CheckStatement>())
                {
                    if (check.At is not { } at) continue;
                    if (!children.TryGetValue(at.Name, out var element))
                        Report(GrammarCodes.UnknownSemanticChild, $"'{at.Name}' is not a child of {ruleName}", at.Span, module);
                    else if ((element is LabeledExpr labeled ? labeled.Inner : element) is RepeatExpr or SeparatedListExpr)
                        Report(GrammarCodes.UnknownSemanticChild, $"'{at.Name}' may be absent or repeated; `at` needs one child", at.Span, module);
                }
            }

            foreach (var property in own)
                if (property.Direction == PropertyDirection.Out && !defined.Contains(property.Name.Name) &&
                    partialTypeProperties?.Contains(property.Name.Name) != true)
                    Warn(GrammarCodes.UndefinedProperty,
                        $"{ruleName} does not define out property '{property.Name.Name}'; its default applies", ruleSpan, module);
        }

        void CheckAssign(IReadOnlyList<PropertyDecl> properties, NameDecl name, PropertyDirection direction, string owner, ModuleDecl module)
        {
            var property = properties.FirstOrDefault(p => p.Name.Name == name.Name);
            if (property is null)
                Report(GrammarCodes.UnknownProperty, $"'{name.Name}' is not a property of {owner}", name.Span, module);
            else if (property.Direction != direction)
                Report(GrammarCodes.WrongPropertyDirection, direction == PropertyDirection.Out
                    ? $"'{name.Name}' is an in property: the parent of a {owner} assigns it"
                    : $"'{name.Name}' is an out property: {owner} defines it itself", name.Span, module);
        }

        void CheckSymbolAssign(NameDecl name, EquatableArray<BindingClause> clauses, ModuleDecl module)
        {
            var declares = clauses.FirstOrDefault(c => c.Kind == BindingClauseKind.Declares);
            if (declares is null)
            {
                Report(GrammarCodes.SymbolWithoutDeclaration, "'symbol' needs a 'declares' clause on this rule", name.Span, module);
                return;
            }
            string kind = declares.Kinds[0].Name;
            if (!SymbolProperties(module).Any(p => p.Name.Name == name.Name && p.Kinds.Any(k => k.Name == kind)))
                Report(GrammarCodes.UnknownProperty, $"'{name.Name}' is not a symbol property of kind '{kind}'", name.Span, module);
        }

        /// <summary>The module's symbol properties and those of the modules it uses (issue 239).</summary>
        IEnumerable<SymbolPropertyDecl> SymbolProperties(ModuleDecl module)
        {
            foreach (var property in module.SymbolProperties) yield return property;
            foreach (var u in module.Usings)
                if (_analysis.FindModule(u.Module) is { } used)
                    foreach (var property in used.SymbolProperties) yield return property;
        }

        /// <summary>
        /// The syntax or extensible rule a semantics child names (issue 239): its reference through a
        /// label, an optional, a list, or a group holding exactly one such reference; null otherwise.
        /// </summary>
        RuleDecl? ChildRule(Expr element, ModuleDecl module, string? implicitModule)
        {
            var bare = Unlabel(element);
            if (bare is RepeatExpr repeat) bare = Unlabel(repeat.Inner);
            else if (bare is SeparatedListExpr list) bare = Unlabel(list.Item);
            if (bare is SequenceExpr group)
            {
                var rules = group.Items.Select(Unlabel).OfType<ReferenceExpr>()
                    .Select(r => _analysis.Resolve(r.Name, r.Span, module, implicitModule)?.Rule)
                    .Where(r => r is SyntaxRule or ExtensibleRule)
                    .ToList();
                return rules.Count == 1 ? rules[0] : null;
            }
            if (bare is not ReferenceExpr reference) return null;
            var resolved = _analysis.Resolve(reference.Name, reference.Span, module, implicitModule)?.Rule;
            return resolved is SyntaxRule or ExtensibleRule ? resolved : null;
        }

        static Expr Unlabel(Expr expr) => expr is LabeledExpr labeled ? labeled.Inner : expr;

        /// <summary>One <c>hover</c> and one <c>expected</c> property per language (issue 239).</summary>
        void CheckPropertyFlags()
        {
            bool hover = false, expected = false;
            foreach (var module in _analysis.PrimaryModules)
                foreach (var rule in _analysis.PrimaryRules(module))
                    foreach (var property in SemanticsWriter.PropertiesOf(rule))
                    {
                        if (property.Hover)
                        {
                            if (hover) Report(GrammarCodes.DuplicatePropertyFlag, "only one property of a language is shown on hover", property.Name.Span, module);
                            hover = true;
                        }
                        if (property.Expected)
                        {
                            if (expected) Report(GrammarCodes.DuplicatePropertyFlag, "only one property of a language is the expected type", property.Name.Span, module);
                            expected = true;
                        }
                    }
        }

        void Warn(string code, string message, GrammarSpan span, ModuleDecl module) =>
            _diagnostics.Add(new GrammarDiagnostic(code, GrammarSeverity.Warning, message, span, module.Name));

        bool OpensScope(ModuleDecl module, string rule) =>
            _analysis.PrimaryRules(module).OfType<SyntaxRule>()
                .Any(r => r.Name == rule && r.Clauses.Any(c => c.Kind == BindingClauseKind.Scope));

        void CheckClauses(EquatableArray<BindingClause> clauses, Expr body, ModuleDecl module, RuleDecl owner, bool isAlias)
        {
            if (clauses.Count == 0) return;
            if (isAlias)
            {
                Report(GrammarCodes.BindingOnAlias,
                    "a rule that is one reference or a choice of references produces no node, so it cannot bind", clauses[0].Span, module);
                return;
            }

            var kinds = SymbolKinds(module);
            var labels = new HashSet<string>(
                SyntaxCodeWriter.Elements(body).OfType<LabeledExpr>().Where(l => l.Inner is not PredicateExpr).Select(l => l.Label),
                StringComparer.Ordinal);
            var seen = new HashSet<BindingClauseKind>();
            foreach (var clause in clauses)
            {
                var key = clause.Kind is BindingClauseKind.LowersLiteral or BindingClauseKind.LowersText or BindingClauseKind.LowersSequence
                    or BindingClauseKind.LowersValue or BindingClauseKind.LowersReference or BindingClauseKind.LowersRepeat
                    or BindingClauseKind.LowersTemplate or BindingClauseKind.LowersExpand
                    ? BindingClauseKind.Lowers : clause.Kind;
                if (!seen.Add(key))
                    Report(GrammarCodes.DuplicateClause, $"a rule takes at most one '{Keyword(clause.Kind)}' clause", clause.Span, module);
                foreach (var kind in clause.Kinds)
                    if (!kinds.Contains(kind.Name))
                        Report(GrammarCodes.UnknownSymbolKind, $"unknown symbol kind '{kind.Name}'", kind.Span, module);
                if (clause.Qualifier is { } qualifier && !kinds.Contains(qualifier.Name))
                    Report(GrammarCodes.UnknownSymbolKind, $"unknown symbol kind '{qualifier.Name}'", qualifier.Span, module);
                if (clause.Kind is BindingClauseKind.Declares or BindingClauseKind.References or BindingClauseKind.LowersLiteral
                    or BindingClauseKind.LowersText or BindingClauseKind.LowersSequence
                    or BindingClauseKind.LowersTemplate or BindingClauseKind.LowersExpand
                    && clause.Field != "this" && !labels.Contains(clause.Field))
                    Report(GrammarCodes.UnknownBindingField,
                        $"'{clause.Field}' is not a label of this rule's elements; name a labeled element or 'this'", clause.FieldSpan, module);
                if (clause.Kind is BindingClauseKind.LowersValue or BindingClauseKind.LowersReference)
                {
                    var properties = SemanticsWriter.PropertiesOf(owner);
                    if (clause.Kind == BindingClauseKind.LowersValue)
                    {
                        var property = properties.FirstOrDefault(candidate => candidate.Name.Name == clause.Field);
                        if (property is null || property.Direction != PropertyDirection.Out ||
                            property.Type.Text.Replace(" ", "") is not ("float" or "float?"))
                            Report(GrammarCodes.InvalidValueProperty,
                                $"'{clause.Field}' must be an out float or float? property of '{owner.Name}'",
                                clause.FieldSpan, module);
                    }
                    if (clause.TypeProperty is { } typeName)
                    {
                        var typeProperty = properties.FirstOrDefault(candidate => candidate.Name.Name == typeName.Name);
                        if (typeProperty is null || typeProperty.Direction != PropertyDirection.Out ||
                            typeProperty.Type.Text.Replace(" ", "") is not ("SemanticType" or "SemanticType?" or
                                "Nitrogen.Semantic.SemanticType" or "Nitrogen.Semantic.SemanticType?" or
                                "global::Nitrogen.Semantic.SemanticType" or "global::Nitrogen.Semantic.SemanticType?"))
                            Report(GrammarCodes.InvalidValueProperty,
                                $"'{typeName.Name}' must be an out SemanticType or SemanticType? property of '{owner.Name}'",
                                typeName.Span, module);
                    }
                    if (clause.Kind == BindingClauseKind.LowersReference &&
                        !clauses.Any(candidate => candidate.Kind == BindingClauseKind.References))
                        Report(GrammarCodes.InvalidValueProperty,
                            "reference lowering needs a references clause on the same rule", clause.Span, module);
                    if (clause.InitializerProperty is { } initializerName)
                    {
                        var initializerProperty = properties.FirstOrDefault(candidate => candidate.Name.Name == initializerName.Name);
                        if (initializerProperty is null || initializerProperty.Direction != PropertyDirection.Out ||
                            initializerProperty.Type.Text.Replace(" ", "") is not "int?")
                            Report(GrammarCodes.InvalidValueProperty,
                                $"'{initializerName.Name}' must be an out int? initializer node property of '{owner.Name}'",
                                initializerName.Span, module);
                    }
                }
                if (clause.Kind == BindingClauseKind.LowersTemplate &&
                    !clauses.Any(candidate => candidate.Kind == BindingClauseKind.Declares))
                    Report(GrammarCodes.InvalidTemplateClause,
                        "a template needs a declares clause on the same rule: the symbol expansions name", clause.Span, module);
                if (clause.Kind == BindingClauseKind.LowersExpand &&
                    !clauses.Any(candidate => candidate.Kind == BindingClauseKind.References && candidate.Field == clause.Field))
                    Report(GrammarCodes.InvalidTemplateClause,
                        $"an expansion needs a references clause on its field '{clause.Field}'", clause.FieldSpan, module);
                if (clause.OperationProperty is { } operationName)
                {
                    var operationProperty = SemanticsWriter.PropertiesOf(owner)
                        .FirstOrDefault(candidate => candidate.Name.Name == operationName.Name);
                    if (operationProperty is null || operationProperty.Direction != PropertyDirection.Out ||
                        operationProperty.Type.Text.Replace(" ", "") is not ("OperationSignature" or "OperationSignature?" or
                            "Nitrogen.Semantic.OperationSignature" or "Nitrogen.Semantic.OperationSignature?" or
                            "global::Nitrogen.Semantic.OperationSignature" or "global::Nitrogen.Semantic.OperationSignature?"))
                        Report(GrammarCodes.InvalidValueProperty,
                            $"'{operationName.Name}' must be an out OperationSignature or OperationSignature? property of '{owner.Name}'",
                            operationName.Span, module);
                }
                foreach (var argument in clause.Arguments)
                {
                    if (!labels.Contains(argument.Name))
                        Report(GrammarCodes.UnknownBindingField,
                            $"'{argument.Name}' is not a label of this rule's elements; name a labeled element", argument.Span, module);
                    else if (argument.SequenceElementType is not null || argument.InferSequence ||
                             clause.Kind is BindingClauseKind.LowersTemplate or BindingClauseKind.LowersExpand)
                    {
                        var field = SyntaxCodeWriter.Elements(body).OfType<LabeledExpr>()
                            .First(element => element.Label == argument.Name);
                        if (field.Inner is not RepeatExpr { Kind: RepeatKind.ZeroOrMore or RepeatKind.OneOrMore }
                            and not SeparatedListExpr)
                            Report(GrammarCodes.SequenceArgumentNeedsList,
                                $"'{argument.Name}' must label a repeated or separated list", argument.Span, module);
                    }
                    else if (argument.OptionalElementType is not null)
                    {
                        var field = SyntaxCodeWriter.Elements(body).OfType<LabeledExpr>()
                            .First(element => element.Label == argument.Name);
                        if (field.Inner is not RepeatExpr { Kind: RepeatKind.Optional })
                            Report(GrammarCodes.OptionalArgumentNeedsOptionalField,
                                $"'{argument.Name}' must label an optional element", argument.Span, module);
                    }
                }
                if (clause.Kind == BindingClauseKind.Declares && clause.Target is { } type
                    && type.Name.IndexOf('.') < 0 && !labels.Contains(type.Name))
                    Report(GrammarCodes.UnknownBindingField,
                        $"'{type.Name}' is not a label of this rule's elements; name a labeled element or a qualified type", type.Span, module);
            }
        }

        static string Keyword(BindingClauseKind kind) => kind switch
        {
            BindingClauseKind.Declares => "declares",
            BindingClauseKind.References => "references",
            BindingClauseKind.Scope => "scope",
            BindingClauseKind.Lowers or BindingClauseKind.LowersLiteral or BindingClauseKind.LowersText
                or BindingClauseKind.LowersSequence or BindingClauseKind.LowersValue
                or BindingClauseKind.LowersReference or BindingClauseKind.LowersRepeat
                or BindingClauseKind.LowersTemplate or BindingClauseKind.LowersExpand => "lowers",
            _ => "dynamic",
        };

        void CheckExpr(Expr expr, ModuleDecl module, string? implicitModule, bool inToken)
        {
            switch (expr)
            {
                case SequenceExpr sequence:
                {
                    var labels = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in sequence.Items)
                    {
                        if (item is LabeledExpr labeled && !labels.Add(labeled.Label))
                            Report(GrammarCodes.DuplicateLabel, $"label '{labeled.Label}' is used twice", labeled.Span, module);
                        CheckExpr(item, module, implicitModule, inToken);
                    }
                    break;
                }
                case ChoiceExpr choice:
                    foreach (var alternative in choice.Alternatives) CheckExpr(alternative, module, implicitModule, inToken);
                    CheckShadowing(choice, module, implicitModule, inToken);
                    break;
                case LabeledExpr labeled:
                    CheckExpr(labeled.Inner, module, implicitModule, inToken);
                    break;
                case RepeatExpr repeat:
                    CheckExpr(repeat.Inner, module, implicitModule, inToken);
                    break;
                case SeparatedListExpr list:
                    CheckExpr(list.Item, module, implicitModule, inToken);
                    CheckExpr(list.Separator, module, implicitModule, inToken);
                    break;
                case PredicateExpr predicate:
                    CheckExpr(predicate.Inner, module, implicitModule, inToken);
                    break;
                case ReferenceExpr reference:
                {
                    var symbol = _analysis.Resolve(reference.Name, reference.Span, module, implicitModule, Report);
                    if (symbol is not null && inToken && symbol.Rule is not TokenRule)
                    {
                        string kind = symbol.Rule is SyntaxRule ? "syntax rule" : "extensible rule";
                        Report(GrammarCodes.TokenReferencesSyntax, $"token rule cannot reference {kind} '{reference.Name}'", reference.Span, module);
                    }
                    break;
                }
            }
        }

        void CheckShadowing(ChoiceExpr choice, ModuleDecl module, string? implicitModule, bool inToken)
        {
            for (int later = 1; later < choice.Alternatives.Count; later++)
                for (int earlier = 0; earlier < later; earlier++)
                    if (Shadows(choice.Alternatives[earlier], choice.Alternatives[later], module, implicitModule, inToken))
                    {
                        Report(GrammarCodes.UnreachableAlternative,
                            $"alternative {later + 1} can never match: alternative {earlier + 1} matches first",
                            choice.Alternatives[later].Span, module);
                        break;
                    }
        }

        bool Shadows(Expr earlier, Expr later, ModuleDecl module, string? implicitModule, bool inToken)
        {
            if (_analysis.IsNullable(earlier, module, implicitModule)) return true;
            if (earlier is LiteralExpr a && later is LiteralExpr b && b.Value.StartsWith(a.Value, StringComparison.Ordinal))
            {
                // In syntax rules an identifier-like literal is a keyword and needs a boundary after it.
                bool keywordBoundary = !inToken && IsKeywordLike(a.Value)
                    && b.Value.Length > a.Value.Length && GrammarLexer.IsIdentifierPart(b.Value[a.Value.Length]);
                return !keywordBoundary;
            }
            return false;
        }

        internal static bool IsKeywordLike(string literal) =>
            GrammarLexer.IsIdentifierStart(literal[0]) && literal.All(GrammarLexer.IsIdentifierPart);

        void CheckAlternativeNames()
        {
            foreach (string key in _analysis.PointKeys)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in _analysis.Point(key))
                    if (p.Alternative.Name.Length > 0 && !seen.Add(p.Alternative.Name))
                        Report(GrammarCodes.DuplicateAlternative,
                            $"extension point '{key}' has two alternatives named '{p.Alternative.Name}'", p.Alternative.Span, p.Module);
            }
        }

        void CheckLeftRecursion()
        {
            var order = new List<string>();
            var owners = new Dictionary<string, (ModuleDecl Module, RuleDecl Rule)>(StringComparer.Ordinal);
            var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var module in _analysis.PrimaryModules)
                foreach (var rule in _analysis.PrimaryRules(module))
                {
                    string key = module.Name + "." + rule.Name;
                    order.Add(key);
                    owners[key] = (module, rule);
                    var firsts = edges[key] = new HashSet<string>(StringComparer.Ordinal);
                    switch (rule)
                    {
                        case TokenRule token:
                            AddFirsts(token.Body, module, null, firsts);
                            break;
                        case SyntaxRule syntax:
                            AddFirsts(syntax.Body, module, null, firsts);
                            break;
                        case ExtensibleRule:
                            foreach (var p in _analysis.Point(key))
                                if (!_analysis.IsPostfix(p, key)) AddFirsts(p.Alternative.Body, p.Module, p.ImplicitModule, firsts);
                            break;
                    }
                }

            foreach (var component in StronglyConnected(order, edges))
            {
                if (component.Count == 1 && !edges[component[0]].Contains(component[0])) continue;
                var (module, rule) = owners[component[0]];
                Report(GrammarCodes.LeftRecursion,
                    $"left recursion through {string.Join(" -> ", component)} -> {component[0]};"
                    + " left recursion is only allowed as a postfix alternative of an extensible rule",
                    rule.Span, module);
            }
        }

        void AddFirsts(Expr expr, ModuleDecl module, string? implicitModule, HashSet<string> firsts)
        {
            switch (expr)
            {
                case ReferenceExpr reference:
                    if (_analysis.Resolve(reference.Name, reference.Span, module, implicitModule) is { } symbol)
                        firsts.Add(symbol.Key);
                    break;
                case SequenceExpr sequence:
                    foreach (var item in sequence.Items)
                    {
                        AddFirsts(item, module, implicitModule, firsts);
                        if (!_analysis.IsNullable(item, module, implicitModule)) break;
                    }
                    break;
                case ChoiceExpr choice:
                    foreach (var alternative in choice.Alternatives) AddFirsts(alternative, module, implicitModule, firsts);
                    break;
                case LabeledExpr labeled:
                    AddFirsts(labeled.Inner, module, implicitModule, firsts);
                    break;
                case RepeatExpr repeat:
                    AddFirsts(repeat.Inner, module, implicitModule, firsts);
                    break;
                case PredicateExpr predicate:
                    AddFirsts(predicate.Inner, module, implicitModule, firsts);
                    break;
                case SeparatedListExpr list:
                    AddFirsts(list.Item, module, implicitModule, firsts);
                    if (_analysis.IsNullable(list.Item, module, implicitModule)) AddFirsts(list.Separator, module, implicitModule, firsts);
                    break;
            }
        }

        /// <summary>Tarjan's algorithm; each component is sorted by declaration order, components by their first member.</summary>
        static List<List<string>> StronglyConnected(List<string> order, Dictionary<string, HashSet<string>> edges)
        {
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < order.Count; i++) position[order[i]] = i;
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new Dictionary<string, int>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            var result = new List<List<string>>();
            int counter = 0;

            void Visit(string v)
            {
                index[v] = low[v] = counter++;
                stack.Push(v);
                onStack.Add(v);
                foreach (string w in edges[v].OrderBy(w => position[w]))
                {
                    if (!index.ContainsKey(w))
                    {
                        Visit(w);
                        low[v] = Math.Min(low[v], low[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }
                }
                if (low[v] != index[v]) return;
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                } while (member != v);
                component.Sort((a, b) => position[a].CompareTo(position[b]));
                result.Add(component);
            }

            foreach (string v in order)
                if (!index.ContainsKey(v)) Visit(v);
            result.Sort((a, b) => position[a[0]].CompareTo(position[b[0]]));
            return result;
        }

        void Report(string code, string message, GrammarSpan span, ModuleDecl module) =>
            _diagnostics.Add(new GrammarDiagnostic(code, GrammarSeverity.Error, message, span, module.Name));
    }
}
