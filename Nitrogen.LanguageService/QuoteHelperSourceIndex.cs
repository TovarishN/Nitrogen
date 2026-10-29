using System.Text.RegularExpressions;
using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

internal sealed class QuoteHelperSourceIndex
{
    internal sealed record Occurrence(string Identity, string Path, GrammarSpan Span, bool Declaration, string Kind);
    readonly IReadOnlyDictionary<string, string> _texts;
    readonly List<(string Path, ModuleDecl Module)> _modules = [];
    readonly List<Occurrence> _occurrences = [];
    sealed record QuotePart(int Kind, int Children, string Text, string? Capture, string? DiscardCategory = null);
    sealed record Discard(string Path, TextSpan Span, PatternHoleSite Site);
    sealed record PatternContext(string Path, QuoteText Quote, string Category, IReadOnlyDictionary<string, QuoteHole> Captures);
    readonly List<Discard> _discards = [];
    readonly List<PatternContext> _patterns = [];
    readonly Dictionary<string, QuotePart[]> _quoteStructures = new(StringComparer.Ordinal);
    readonly GrammarAnalysis _analysis;
    readonly Language? _language;
    readonly HashSet<string> _unparsedHelpers = new(StringComparer.Ordinal);
    internal IEnumerable<string> Paths => _texts.Keys;

    public QuoteHelperSourceIndex(IReadOnlyDictionary<string, string> texts, string? completingPath = null, int completingOffset = 0, Language? language = null, Func<Language?>? languageFactory = null)
    {
        _texts = texts;
        var completionRepairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, original) in texts)
        {
            string text = DocumentSignatures.Read(original).MaskedText;
            var parsed = GrammarParser.Parse(text);
            if (parsed.File is null && path == completingPath)
            {
                var prefix = Regex.Match(text[..Math.Min(completingOffset, text.Length)], @"\brewrites\s+([A-Za-z_][\w.]*|)$");
                if (prefix.Success)
                {
                    text = text[..prefix.Groups[1].Index] + "<[ ]>" + text[completingOffset..];
                    parsed = GrammarParser.Parse(text);
                    if (parsed.File is not null) completionRepairs.Add(path);
                }
                else
                {
                    var typed = Regex.Match(text[..Math.Min(completingOffset, text.Length)], @"(?:\bmatch\s+|:\s*)([A-Za-z_][\w.]*|)$");
                    if (typed.Success)
                    {
                        text = text[..typed.Groups[1].Index] + "NitrogenCompletionCategory" + text[completingOffset..];
                        parsed = GrammarParser.Parse(text);
                        if (parsed.File is not null) completionRepairs.Add(path);
                    }
                }
            }
            if (parsed.File is { } file)
                _modules.AddRange(file.Modules.Select(module => (path, module)));
        }
        _analysis = new(_modules.Select(m => m.Module));
        _language = language ?? (_modules.Any(m => m.Module.Helpers.Count > 0) ? languageFactory?.Invoke() : null);
        foreach (var (path, module) in _modules)
        {
            // Completion-only repairs shift later offsets; never expose them as navigation identities.
            if (completionRepairs.Contains(path)) continue;
            foreach (var helper in module.Helpers)
            {
                string id = "helper:" + module.Name + "." + helper.Name.Name;
                Add(id, path, helper.Name.Span, true, "helper");
                AddCategory(path, module, helper.ReturnCategory);
                foreach (var parameter in helper.Parameters)
                {
                    string parameterId = id + ":parameter:" + parameter.Name.Name;
                    Add(parameterId, path, parameter.Name.Span, true, "parameter");
                    AddCategory(path, module, parameter.Category);
                }
                var parameters = helper.Parameters.GroupBy(parameter => parameter.Name.Name).Select(group => group.First())
                    .ToDictionary(parameter => parameter.Name.Name, parameter =>
                        (Shape: new QuoteHole(_analysis.Helpers.Category(parameter.Category, module)?.Key ?? parameter.Category.Name, parameter.IsList),
                         Identity: id + ":parameter:" + parameter.Name.Name), StringComparer.Ordinal);
                IndexQuote(helper.Body, helper.ReturnCategory, parameters, id, "fallback");
                if (helper.Match is not { } match) continue;
                if (parameters.TryGetValue(match.Selector.Name, out var selector))
                    Add(selector.Identity, path, match.Selector.Span, false, "parameter");
                for (int armIndex = 0; armIndex < match.Arms.Count; armIndex++)
                {
                    var arm = match.Arms[armIndex];
                    string scopeId = id + ":arm:" + armIndex;
                    var captures = new Dictionary<string, (QuoteHole Shape, string Identity)>(StringComparer.Ordinal);
                    foreach (var capture in arm.Captures)
                    {
                        string captureId = scopeId + ":parameter:" + capture.Name.Name;
                        Add(captureId, path, capture.Name.Span, true, "parameter");
                        AddCategory(path, module, capture.Category);
                        AddPatternReference(path, module, capture.Category);
                        captures[capture.Name.Name] = (new(_analysis.Helpers.Category(capture.Category, module)?.Key ?? capture.Category.Name, capture.IsList), captureId);
                    }
                    var selectorParameter = helper.Parameters.FirstOrDefault(parameter => parameter.Name.Name == match.Selector.Name);
                    if (selectorParameter is not null) IndexQuote(arm.Pattern, selectorParameter.Category, captures, scopeId, "arm:" + armIndex + ":pattern", pattern: true);
                    var scope = new Dictionary<string, (QuoteHole Shape, string Identity)>(parameters, StringComparer.Ordinal);
                    foreach (var capture in captures) scope[capture.Key] = capture.Value;
                    IndexQuote(arm.Result, helper.ReturnCategory, scope, scopeId, "arm:" + armIndex + ":result");
                }

                void IndexQuote(QuoteText quote, NameDecl categoryName,
                    IReadOnlyDictionary<string, (QuoteHole Shape, string Identity)> scope, string scopeId, string slot, bool pattern = false)
                {
                    try
                    {
                        var category = _analysis.Helpers.Category(categoryName, module);
                        var shapes = scope.ToDictionary(pair => pair.Key, pair => pair.Value.Shape, StringComparer.Ordinal);
                        if (pattern && category is not null) _patterns.Add(new(path, quote, category.Key, shapes));
                        using var parsed = category is null ? null : pattern
                            ? _language?.ParsePattern(quote.Text, category.Key, shapes)
                            : _language?.ParseTemplate(quote.Text, category.Key, shapes);
                        if (parsed is not { Success: true, HasErrors: false })
                        {
                            _unparsedHelpers.Add(scopeId);
                            _unparsedHelpers.Add(id);
                            return;
                        }
                        var tree = parsed.Tree;
                        var structure = new QuotePart[tree.NodeCount];
                        for (int node = 0; node < tree.NodeCount; node++)
                        {
                            int kind = tree.Kind(node), children = tree.ChildCount(node);
                            bool hole = kind is SyntaxKinds.Hole or SyntaxKinds.ListHole;
                            bool discard = tree.PatternHoleSites.TryGetValue(node, out var site);
                            string? capture = hole && !discard ? scope[tree.GetText(node)[(kind == SyntaxKinds.Hole ? 1 : 3)..].ToString()].Identity : null;
                            structure[node] = new(kind, children, !hole && children == 0 ? tree.GetText(node).ToString() : "", capture, site?.Category);
                            if (discard)
                            {
                                var span = tree.Span(node);
                                _discards.Add(new(path, new(quote.Span.Start + span.Start, span.Length), site!));
                            }
                        }
                        _quoteStructures[id + ":quote:" + slot] = structure;
                        for (int node = 0; node < tree.NodeCount; node++)
                        {
                            if (tree.Kind(node) is not (SyntaxKinds.Hole or SyntaxKinds.ListHole)) continue;
                            int marker = tree.Kind(node) == SyntaxKinds.ListHole ? 3 : 1;
                            var span = tree.Span(node);
                            string name = tree.GetText(node)[marker..].ToString();
                            if (scope.TryGetValue(name, out var capture))
                                Add(capture.Identity, path, new(quote.Span.Start + span.Start + marker, name.Length), false, "parameter");
                        }
                    }
                    catch (ArgumentException) { _unparsedHelpers.Add(scopeId); _unparsedHelpers.Add(id); }
                }

            }
            foreach (var pattern in module.Patterns)
            {
                Add(PatternId(module.Name, pattern.Name.Name), path, pattern.Name.Span, true, "pattern");
                foreach (var capture in pattern.Alternatives.SelectMany(alternative => alternative.Captures))
                    AddPatternReference(path, module, capture.Category);
            }
            foreach (var match in DiagnosticMatches(module))
                foreach (var capture in match.Captures.Concat(match.Context?.Captures ?? []))
                    AddPatternReference(path, module, capture.Category);
            foreach (var rule in module.Rules)
                if (rule is SyntaxRule syntax) Calls(path, module, syntax.Body, syntax.Clauses);
                else if (rule is ExtensibleRule point)
                    foreach (var alternative in point.Alternatives) Calls(path, module, alternative.Body, alternative.Clauses);
            foreach (var extend in module.Extends)
                foreach (var alternative in extend.Alternatives) Calls(path, module, alternative.Body, alternative.Clauses);
        }
    }

    void Add(string id, string path, GrammarSpan span, bool declaration, string kind) =>
        _occurrences.Add(new(id, path, span, declaration, kind));

    static string PatternId(string module, string name) => "pattern:" + module + ":" + name;

    // Only the last segment of a qualified reference is the pattern's own name, so renames keep the qualifier.
    void AddPatternReference(string path, ModuleDecl module, NameDecl name)
    {
        if (_analysis.ResolvePattern(name.Name, module) is not { } found) return;
        int dot = name.Name.LastIndexOf('.');
        Add(PatternId(found.Module.Name, found.Pattern.Name.Name), path,
            new GrammarSpan(name.Span.Start + dot + 1, name.Name.Length - dot - 1), false, "pattern");
    }

    static IEnumerable<MatchDiagnosticStatement> DiagnosticMatches(ModuleDecl module) =>
        module.Rules.SelectMany(rule => rule switch
        {
            SyntaxRule syntax => [syntax.Semantics],
            ExtensibleRule point => point.Alternatives.Select(alternative => alternative.Semantics),
            _ => [],
        }).Concat(module.Extends.SelectMany(extend => extend.Alternatives.Select(alternative => alternative.Semantics)))
        .SelectMany(block => block?.Statements.OfType<MatchDiagnosticStatement>() ?? []);

    /// <summary>The signature of the named pattern at <paramref name="position"/>, if any.</summary>
    public HoverInfo? PatternHover(string path, DocumentPosition position)
    {
        if (At(path, position) is not { Kind: "pattern" } at) return null;
        string[] parts = at.Identity.Split(':');
        var owner = _modules.Select(m => m.Module).First(m => m.Name == parts[1]);
        var pattern = owner.Patterns.First(p => p.Name.Name == parts[2]);
        string category = _analysis.Helpers.Category(pattern.Category, owner)?.Key ?? pattern.Category.Name;
        int count = pattern.Alternatives.Count;
        return new HoverInfo($"```\npattern {owner.Name}.{pattern.Name.Name} : {category}\n```\n{count} alternative{(count == 1 ? "" : "s")}",
            Location(at).Range);
    }

    void AddCategory(string path, ModuleDecl module, NameDecl name)
    {
        if (_analysis.Helpers.Category(name, module) is not { } category) return;
        string id = "category:" + category.Key;
        Add(id, path, name.Span, false, "category");
        var owner = _modules.FirstOrDefault(m => ReferenceEquals(m.Module, category.Module));
        if (owner.Module is null) return;
        int position = category.Rule.Span.Start;
        int skip = category.Rule is ExtensibleRule ? 2 : 1;
        for (int i = 0; i < skip; i++) GrammarLexer.Next(_texts[owner.Path], ref position);
        var token = GrammarLexer.Next(_texts[owner.Path], ref position);
        Add(id, owner.Path, token.Span, true, "category");
    }

    void Calls(string path, ModuleDecl module, Expr body, IEnumerable<BindingClause> clauses)
    {
        foreach (var call in clauses.Select(c => c.HelperCall).OfType<QuoteHelperCall>())
        {
            if (_analysis.Helpers.Resolve(call.Helper.Name, module) is { } helper)
            {
                int dot = call.Helper.Name.LastIndexOf('.');
                Add("helper:" + helper.Key, path,
                    new(call.Helper.Span.Start + dot + 1, call.Helper.Name.Length - dot - 1), false, "helper");
            }
            foreach (var argument in call.Arguments)
                if (SyntaxCodeWriter.Elements(body).OfType<LabeledExpr>().FirstOrDefault(f => f.Label == argument.Name) is { } field)
                {
                    string id = "field:" + path + ":" + body.Span.Start + ":" + field.Label;
                    Add(id, path, new(field.Span.Start, field.Label.Length), true, "field");
                    Add(id, path, argument.Span, false, "field");
                }
        }
    }

    public HoverInfo? DiscardHover(string path, DocumentPosition position)
    {
        if (!_texts.TryGetValue(path, out var text)) return null;
        var lines = new LineMap(text);
        int offset = lines.OffsetOf(position);
        var discard = _discards.FirstOrDefault(d => d.Path == path && d.Span.Start <= offset && offset < d.Span.End);
        return discard is null ? null : new HoverInfo(
            $"`{(discard.Site.IsList ? "..$_" : "$_")}` — {(discard.Site.IsList ? "list" : "scalar")} discard of `{discard.Site.Category}`",
            lines.RangeOf(discard.Span));
    }

    IReadOnlyList<CompletionItem>? PatternCompletion(string path, int offset)
    {
        var context = _patterns.FirstOrDefault(p => p.Path == path && p.Quote.Span.Start <= offset && offset <= p.Quote.Span.End);
        if (context is null || _language is null) return null;
        string text = context.Quote.Text;
        int local = offset - context.Quote.Span.Start, start = local, end = local;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        if (start == 0 || text[start - 1] != '$') return null;
        bool list = start >= 3 && text[(start - 3)..start] == "..$";
        int marker = start - (list ? 3 : 1);
        string prefix = text[start..local];
        if (!QuotationWriter.Holes(text[..start] + "_" + text[end..]).Any(h => h.Start == marker)) return [];
        var result = new List<CompletionItem>();
        var replace = new LineMap(_texts[path]).RangeOf(new(context.Quote.Span.Start + start, local - start));
        foreach (string name in context.Captures.Where(c => c.Value.IsList == list).Select(c => c.Key).Append("_"))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var parsed = _language.ParsePattern(text[..start] + name + text[end..], context.Category, context.Captures);
            bool accepted = parsed.Success && !parsed.HasErrors && Enumerable.Range(0, parsed.Tree.NodeCount)
                .Any(n => parsed.Tree.Kind(n) == (list ? SyntaxKinds.ListHole : SyntaxKinds.Hole) && parsed.Tree.Span(n).Start == marker);
            if (!parsed.Success)
            {
                string probe = text[..start] + name;
                accepted = name == "_"
                    ? _language.AcceptsPatternDiscardPrefix(probe, context.Category, context.Captures, marker)
                    : _language.ExpectedPattern(probe + "\u0001", context.Category, context.Captures).Position >= probe.Length;
            }
            if (!accepted) continue;
            result.Add(new(name, CompletionKind.Field, name == "_" ? "discard syntax" : "pattern capture", replace));
        }
        return result;
    }

    public Occurrence? At(string path, DocumentPosition position)
    {
        if (!_texts.TryGetValue(path, out string? text)) return null;
        int offset = new LineMap(text).OffsetOf(position);
        return _occurrences.FirstOrDefault(o => o.Path == path && o.Span.Start <= offset && offset < o.Span.End);
    }

    DocumentLocation Location(Occurrence occurrence) => new(new Uri(occurrence.Path).AbsoluteUri,
        new LineMap(_texts[occurrence.Path]).RangeOf(new(occurrence.Span.Start, occurrence.Span.Length)));

    public IReadOnlyList<DocumentLocation> Definition(string path, DocumentPosition position) => At(path, position) is { } at
        ? _occurrences.Where(o => o.Identity == at.Identity && o.Declaration).Select(Location).Distinct().ToArray() : [];

    public IReadOnlyList<DocumentLocation> References(string path, DocumentPosition position, bool includeDeclaration) => At(path, position) is { } at
        ? _occurrences.Where(o => o.Identity == at.Identity && (includeDeclaration || !o.Declaration)).Select(Location).Distinct().ToArray() : [];

    public IReadOnlyList<CompletionItem>? Completion(string path, DocumentPosition position)
    {
        if (!_texts.TryGetValue(path, out string? text)) return null;
        int offset = new LineMap(text).OffsetOf(position);
        if (PatternCompletion(path, offset) is { } patternItems) return patternItems;
        var descendant = Regex.Match(text[..offset], @":\s*\.\.([A-Za-z_][\w.]*|)$");
        if (descendant.Success)
        {
            var current = _modules.FirstOrDefault(m => m.Path == path && m.Module.Span.Start <= offset && offset <= m.Module.Span.End).Module;
            if (current is null) return [];
            return _modules.Where(m => ReferenceEquals(m.Module, current) || current.Usings.Any(u => u.Module == m.Module.Name))
                .SelectMany(m => m.Module.Patterns.Select(p => ReferenceEquals(m.Module, current) ? p.Name.Name : m.Module.Name + "." + p.Name.Name))
                .Where(name => name.StartsWith(descendant.Groups[1].Value, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)
                .Select(name => new CompletionItem(name, CompletionKind.Class, "named pattern",
                    new LineMap(text).RangeOf(new(descendant.Groups[1].Index, offset - descendant.Groups[1].Index)))).ToArray();
        }
        var selectorPrefix = Regex.Match(text[..offset], @"\bmatch\s+([A-Za-z_][\w]*|)$");
        var helperContext = _modules.Where(module => module.Path == path).SelectMany(module => module.Module.Helpers)
            .FirstOrDefault(helper => helper.Span.Start <= offset && offset <= helper.Span.End);
        bool inQuote = helperContext is not null && new[] { helperContext.Body }
            .Concat(helperContext.Match?.Arms.SelectMany(arm => new[] { arm.Pattern, arm.Result }) ?? [])
            .Any(quote => quote.Span.Start <= offset && offset <= quote.Span.End);
        if (selectorPrefix.Success && helperContext is not null && !inQuote)
            return helperContext.Parameters.Where(parameter => !parameter.IsList && parameter.Name.Name.StartsWith(selectorPrefix.Groups[1].Value, StringComparison.Ordinal))
                .Select(parameter => new CompletionItem(parameter.Name.Name, CompletionKind.Field, "scalar match selector",
                    new LineMap(text).RangeOf(new(selectorPrefix.Groups[1].Index, offset - selectorPrefix.Groups[1].Index)))).ToArray();
        var categoryPrefix = Regex.Match(text[..offset], @":\s*([A-Za-z_][\w.]*|)$");
        if (categoryPrefix.Success && helperContext is not null && !inQuote)
        {
            var owner = _modules.First(module => module.Path == path && module.Module.Helpers.Contains(helperContext)).Module;
            // Arm captures may also name a pattern; helper parameters and return types may not.
            bool armCapture = helperContext.Match?.Arms.Any(arm => arm.Span.Start <= offset && offset <= arm.Pattern.Span.Start) == true;
            return _modules.SelectMany(module => module.Module.Rules.Where(rule => rule is SyntaxRule or ExtensibleRule)
                    .Select(rule => (Owner: module.Module, Name: rule.Name, Detail: "syntax category"))
                    .Concat(armCapture ? module.Module.Patterns.Select(p => (Owner: module.Module, Name: p.Name.Name, Detail: "named pattern")) : []))
                .Where(entry => ReferenceEquals(entry.Owner, owner) || owner.Usings.Any(imported => imported.Module == entry.Owner.Name) || categoryPrefix.Groups[1].Value.Contains('.'))
                .Select(entry => (Name: ReferenceEquals(entry.Owner, owner) ? entry.Name : entry.Owner.Name + "." + entry.Name, entry.Detail))
                .Where(entry => entry.Name.StartsWith(categoryPrefix.Groups[1].Value, StringComparison.Ordinal)).DistinctBy(entry => entry.Name)
                .Select(entry => new CompletionItem(entry.Name, CompletionKind.Class, entry.Detail,
                    new LineMap(text).RangeOf(new(categoryPrefix.Groups[1].Index, offset - categoryPrefix.Groups[1].Index)))).ToArray();
        }
        var prefix = Regex.Match(text[..offset], @"\brewrites\s+([A-Za-z_][\w.]*|)$");
        if (!prefix.Success) return null;
        var module = _modules.FirstOrDefault(m => m.Path == path && m.Module.Span.Start <= offset && offset <= m.Module.Span.End).Module;
        if (module is null) return [];
        var visible = _modules.Where(m => ReferenceEquals(m.Module, module) || module.Usings.Any(u => u.Module == m.Module.Name));
        var result = new List<CompletionItem>();
        foreach (var (_, owner) in visible)
            foreach (var helper in owner.Helpers)
            {
                var resolved = _analysis.Helpers.Resolve(helper.Name.Name, module);
                string name = resolved?.Module.Name == owner.Name ? helper.Name.Name : owner.Name + "." + helper.Name.Name;
                if (!name.StartsWith(prefix.Groups[1].Value, StringComparison.Ordinal)) continue;
                string detail = name + "(" + string.Join(", ", helper.Parameters.Select(p => p.Name.Name + ": " +
                    (_analysis.Helpers.Category(p.Category, owner)?.Key ?? p.Category.Name) + (p.IsList ? "*" : ""))) + ") : " +
                    (_analysis.Helpers.Category(helper.ReturnCategory, owner)?.Key ?? helper.ReturnCategory.Name);
                result.Add(new(name, CompletionKind.Function, detail,
                    new LineMap(text).RangeOf(new(prefix.Groups[1].Index, offset - prefix.Groups[1].Index))));
            }
        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>> Rename(string path, DocumentPosition position, string newName)
    {
        var target = At(path, position) ?? throw new RenameRefusedException("Nothing to rename here.");
        if (target.Kind is not ("helper" or "parameter" or "pattern"))
            throw new RenameRefusedException("This syntax category/field is used by a quote helper; update its resolved consumers together.");
        if (!Regex.IsMatch(newName, @"^[A-Za-z_]\w*$")) throw new RenameRefusedException("Choose a valid helper/parameter name.");
        if (target.Kind == "parameter" && _unparsedHelpers.Contains(target.Identity[..target.Identity.IndexOf(":parameter:", StringComparison.Ordinal)]))
            throw new RenameRefusedException("The helper quote must parse before renaming its parameters.");
        var changed = new Dictionary<string, IReadOnlyList<DocumentEdit>>(StringComparer.Ordinal);
        var proposed = new Dictionary<string, string>(_texts, StringComparer.Ordinal);
        foreach (var group in _occurrences.Where(o => o.Identity == target.Identity).Distinct().GroupBy(o => o.Path))
        {
            var edits = group.Select(o => new DocumentEdit(Location(o).Range, newName)).Distinct().ToArray();
            changed[new Uri(group.Key).AbsoluteUri] = edits;
            string text = proposed[group.Key];
            foreach (var occurrence in group.DistinctBy(o => o.Span).OrderByDescending(o => o.Span.Start))
                text = text.Remove(occurrence.Span.Start, occurrence.Span.Length).Insert(occurrence.Span.Start, newName);
            proposed[group.Key] = text;
        }
        var checkedGrammar = GrammarCompiler.Compile(proposed.Select(p => new GrammarInput(p.Key, DocumentSignatures.Read(p.Value).MaskedText, "Nitrogen.Rename")));
        if (checkedGrammar.HasErrors) throw new RenameRefusedException("Rename would make the grammar invalid: " + checkedGrammar.Diagnostics.First().Diagnostic.Message);
        var replacements = _occurrences.Where(o => o.Identity == target.Identity).DistinctBy(o => (o.Path, o.Span)).ToArray();
        var sourceEdits = replacements.Select(o => new ExtractionTextEdit(new Uri(o.Path).AbsoluteUri, o.Span, newName)).ToArray();
        var renames = target.Kind == "helper" ? new Dictionary<string, string>
        {
            [target.Identity["helper:".Length..]] = target.Identity["helper:".Length..(target.Identity.LastIndexOf('.') + 1)] + newName,
        } : null;
        if (!QuoteHelperBindings.Preserved(_texts.Select(p => new ExtractionSource(new Uri(p.Key).AbsoluteUri, p.Value)),
            proposed.Select(p => new ExtractionSource(new Uri(p.Key).AbsoluteUri, p.Value)), sourceEdits, renames))
            throw new RenameRefusedException("Rename would change an existing helper call's resolution.");
        var after = new QuoteHelperSourceIndex(proposed, language: _language);
        string Expected(string identity) => target.Kind == "helper"
            ? identity == target.Identity || identity.StartsWith(target.Identity + ":", StringComparison.Ordinal)
                ? "helper:" + renames![target.Identity["helper:".Length..]] + identity[target.Identity.Length..] : identity
            : identity == target.Identity ? target.Identity[..(target.Identity.LastIndexOf(':') + 1)] + newName : identity;
        var beforeParameters = _occurrences.Where(o => o.Kind == "parameter").Distinct()
            .Select(o => (Identity: Expected(o.Identity), o.Path, Start: o.Span.Start + replacements.Where(e => e.Path == o.Path && e.Span.End <= o.Span.Start).Sum(e => newName.Length - e.Span.Length), o.Declaration))
            .ToHashSet();
        var afterParameters = after._occurrences.Where(o => o.Kind == "parameter").Distinct()
            .Select(o => (o.Identity, o.Path, o.Span.Start, o.Declaration)).ToHashSet();
        if (!beforeParameters.SetEquals(afterParameters))
            throw new RenameRefusedException("Rename would change the helper quote's parsed parameter structure.");
        if (_quoteStructures.Count != after._quoteStructures.Count)
            throw new RenameRefusedException("Rename would change the helper quote's parsed syntax structure.");
        foreach (var (identity, parts) in _quoteStructures)
        {
            var expected = parts.Select(part => part.Capture is { } capture ? part with { Capture = Expected(capture) } : part);
            if (!after._quoteStructures.TryGetValue(Expected(identity), out var actual) || !expected.SequenceEqual(actual))
                throw new RenameRefusedException("Rename would change the helper quote's parsed syntax structure.");
        }
        return changed;
    }
}
