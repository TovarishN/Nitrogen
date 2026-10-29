namespace Nitrogen.Grammar;

internal sealed record QuoteHelperSymbol(ModuleDecl Module, QuoteHelperDecl Declaration)
{
    public string Key => Module.Name + "." + Declaration.Name.Name;
}
internal sealed record QuoteHelperInput(string Parameter, string Field, int Child, string Category, bool IsList, int Stride);
internal sealed record ResolvedQuoteHelper(QuoteHelperSymbol Symbol, string Category, IReadOnlyList<QuoteHelperInput> Inputs);

internal sealed class QuoteHelperAnalysis(GrammarAnalysis analysis)
{
    public QuoteHelperSymbol? Resolve(string name, ModuleDecl module, GrammarReport? report = null, GrammarSpan span = default)
    {
        var candidates = new List<QuoteHelperSymbol>();
        int dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            if (analysis.FindModule(name.Substring(0, dot)) is { } owner)
                candidates.AddRange(owner.Helpers.Where(h => h.Name.Name == name.Substring(dot + 1)).Select(h => new QuoteHelperSymbol(owner, h)));
        }
        else
        {
            candidates.AddRange(module.Helpers.Where(h => h.Name.Name == name).Select(h => new QuoteHelperSymbol(module, h)));
            if (candidates.Count == 0)
                foreach (var imported in module.Usings.Select(u => u.Module).Distinct(StringComparer.Ordinal))
                    if (analysis.FindModule(imported) is { } owner)
                        candidates.AddRange(owner.Helpers.Where(h => h.Name.Name == name).Select(h => new QuoteHelperSymbol(owner, h)));
        }
        if (candidates.Count == 1) return candidates[0];
        report?.Invoke(candidates.Count == 0 ? GrammarCodes.UnknownQuoteHelper : GrammarCodes.AmbiguousQuoteHelper,
            candidates.Count == 0 ? $"unknown quote helper '{name}'" : $"ambiguous quote helper '{name}'", span, module);
        return null;
    }

    public GrammarSymbol? Category(NameDecl name, ModuleDecl module, GrammarReport? report = null)
    {
        var category = analysis.Resolve(name.Name, name.Span, module, null);
        if (category?.Rule is SyntaxRule or ExtensibleRule) return category;
        report?.Invoke(GrammarCodes.QuoteHelperShape, $"'{name.Name}' must be a syntax category", name.Span, module);
        return null;
    }

    /// <summary>
    /// What a pattern capture accepts: a syntax category, or a named pattern (its category and qualified name).
    /// A descendant capture (<c>..Name</c>) must name a pattern and capture one node.
    /// </summary>
    public (GrammarSymbol Category, string? PatternName)? CaptureContract(QuoteParameter capture, ModuleDecl module, GrammarReport? report = null)
    {
        if (analysis.ResolvePattern(capture.Category.Name, module) is { } found)
        {
            if (capture.IsDescendant && capture.IsList)
                report?.Invoke(GrammarCodes.InvalidPatternCapture, $"descendant capture '{capture.Name.Name}' finds one node; it cannot be a list", capture.Span, module);
            return Category(found.Pattern.Category, found.Module) is { } category
                ? (category, found.Module.Name + "." + found.Pattern.Name.Name) : null;
        }
        if (capture.IsDescendant)
        {
            report?.Invoke(GrammarCodes.UnknownNamedPattern, $"'{capture.Category.Name}' is not a named pattern; a descendant capture needs one", capture.Category.Span, module);
            return null;
        }
        return Category(capture.Category, module, report) is { } symbol ? (symbol, null) : null;
    }

    /// <summary>Checks a pattern's captures: contracts, and that a descendant capture occurs once.</summary>
    public void CheckPatternCaptures(IReadOnlyList<QuoteParameter> captures, QuoteText pattern, GrammarSymbol? category, ModuleDecl module, GrammarReport report)
    {
        foreach (var capture in captures) CaptureContract(capture, module, report);
        var holes = QuotationWriter.Holes(pattern.Text, analysis, category).GroupBy(hole => hole.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var capture in captures)
            if (capture.IsDescendant && holes.TryGetValue(capture.Name.Name, out int count) && count > 1)
                report(GrammarCodes.InvalidPatternCapture, $"descendant capture '{capture.Name.Name}' may occur only once in a pattern", capture.Name.Span, module);
    }

    public void CheckDeclarations(ModuleDecl module, GrammarReport report)
    {
        var names = new HashSet<string>(module.Rules.Select(r => r.Name), StringComparer.Ordinal);
        foreach (var helper in module.Helpers)
        {
            if (!names.Add(helper.Name.Name))
                report(GrammarCodes.DuplicateQuoteHelper, $"duplicate rule/helper '{helper.Name.Name}'", helper.Name.Span, module);
            var category = Category(helper.ReturnCategory, module, report);
            var parameters = new Dictionary<string, QuoteParameter>(StringComparer.Ordinal);
            foreach (var parameter in helper.Parameters)
            {
                if (parameters.ContainsKey(parameter.Name.Name))
                    report(GrammarCodes.DuplicateQuoteParameter, $"duplicate parameter '{parameter.Name.Name}'", parameter.Name.Span, module);
                else parameters.Add(parameter.Name.Name, parameter);
                Category(parameter.Category, module, report);
            }
            CheckQuote(helper.Body, parameters);
            if (helper.Match is not { } match) continue;
            if (!parameters.TryGetValue(match.Selector.Name, out var selector) || selector.IsList)
                report(GrammarCodes.QuoteMatchSelector, "a match selector must name a scalar helper parameter", match.Selector.Span, module);
            foreach (var arm in match.Arms)
            {
                var captures = new Dictionary<string, QuoteParameter>(StringComparer.Ordinal);
                foreach (var capture in arm.Captures)
                {
                    if (capture.Name.Name == "_" || parameters.ContainsKey(capture.Name.Name) || captures.ContainsKey(capture.Name.Name))
                        report(GrammarCodes.QuoteMatchCapture, $"capture '{capture.Name.Name}' is reserved, duplicated or shadows a helper parameter", capture.Name.Span, module);
                    else captures.Add(capture.Name.Name, capture);
                }
                CheckPatternCaptures(arm.Captures, arm.Pattern, selector is null ? null : Category(selector.Category, module), module, report);
                CheckQuote(arm.Pattern, captures, selector is null ? null : Category(selector.Category, module), pattern: true);
                var scope = new Dictionary<string, QuoteParameter>(parameters, StringComparer.Ordinal);
                foreach (var capture in captures) scope[capture.Key] = capture.Value;
                CheckQuote(arm.Result, scope);
            }

            void CheckQuote(QuoteText quote, IReadOnlyDictionary<string, QuoteParameter> scope, GrammarSymbol? quoteCategory = null, bool pattern = false)
            {
                foreach (var hole in QuotationWriter.Holes(quote.Text, analysis, quoteCategory ?? category))
                {
                    if (pattern && hole.Name == "_") continue;
                    var span = new GrammarSpan(quote.Span.Start + hole.Start, hole.Length);
                    if (!scope.TryGetValue(hole.Name, out var parameter))
                        report(GrammarCodes.UnknownHole, $"'${hole.Name}' is not in this quote's scope", span, module);
                    else if (parameter.IsList != hole.IsList)
                        report(GrammarCodes.HoleShapeMismatch, $"'${hole.Name}' has the wrong scalar/list shape", span, module);
                }
            }
        }
        foreach (var pattern in module.Patterns)
        {
            if (!names.Add(pattern.Name.Name))
                report(GrammarCodes.DuplicateNamedPattern, $"duplicate rule/helper/pattern '{pattern.Name.Name}'", pattern.Name.Span, module);
            var category = Category(pattern.Category, module, report);
            foreach (var alternative in pattern.Alternatives)
            {
                var captures = new HashSet<string>(StringComparer.Ordinal);
                foreach (var capture in alternative.Captures)
                    if (capture.Name.Name == "_" || !captures.Add(capture.Name.Name))
                        report(GrammarCodes.QuoteMatchCapture, $"reserved or duplicate capture '{capture.Name.Name}'", capture.Name.Span, module);
                CheckPatternCaptures(alternative.Captures, alternative.Pattern, category, module, report);
                foreach (var hole in QuotationWriter.Holes(alternative.Pattern.Text, analysis, category))
                    if (hole.Name != "_" && !captures.Contains(hole.Name))
                        report(GrammarCodes.UnknownHole, $"'${hole.Name}' is not a capture of this alternative",
                            new GrammarSpan(alternative.Pattern.Span.Start + hole.Start, hole.Length), module);
            }
        }
    }

    public ResolvedQuoteHelper? Bind(QuoteHelperCall call, ModuleDecl module, Expr callerBody, string returnCategory,
        string? implicitModule, GrammarReport? report = null)
    {
        var symbol = Resolve(call.Helper.Name, module, report, call.Helper.Span);
        if (symbol is null) return null;
        var helper = symbol.Declaration;
        if (call.Arguments.Count != helper.Parameters.Count)
        {
            report?.Invoke(GrammarCodes.QuoteHelperArgumentCount, $"'{symbol.Key}' needs {helper.Parameters.Count} arguments", call.Span, module);
            return null;
        }
        var category = Category(helper.ReturnCategory, symbol.Module);
        if (category is null) return null;
        bool valid = category.Key == returnCategory;
        if (!valid)
            report?.Invoke(GrammarCodes.QuoteHelperCategory, $"'{symbol.Key}' returns {category.Key}, expected {returnCategory}", call.Helper.Span, module);
        var elements = SyntaxCodeWriter.Elements(callerBody);
        var inputs = new List<QuoteHelperInput>();
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            var argument = call.Arguments[i];
            var parameter = helper.Parameters[i];
            var field = elements.OfType<LabeledExpr>().FirstOrDefault(e => e.Label == argument.Name);
            var shape = field is null ? (Item: (ReferenceExpr?)null, IsList: false, Stride: 1) : QuotationWriter.Shape(field.Inner);
            var actual = shape.Item is { } reference ? analysis.Resolve(reference.Name, reference.Span, module, implicitModule) : null;
            var expected = Category(parameter.Category, symbol.Module);
            if (actual?.Rule is not (SyntaxRule or ExtensibleRule) || shape.IsList != parameter.IsList)
            {
                report?.Invoke(GrammarCodes.QuoteHelperShape, $"'{argument.Name}' must be a supported {(parameter.IsList ? "list" : "scalar")} syntax field", argument.Span, module);
                valid = false;
            }
            else if (actual.Key != expected?.Key)
            {
                report?.Invoke(GrammarCodes.QuoteHelperCategory, $"'{argument.Name}' has category {actual.Key}, expected {expected?.Key}", argument.Span, module);
                valid = false;
            }
            else inputs.Add(new(parameter.Name.Name, argument.Name, BindingWriter.ChildIndex(elements, argument.Name), actual.Key, shape.IsList, shape.Stride));
        }
        return valid ? new(symbol, category.Key, inputs) : null;
    }
}
