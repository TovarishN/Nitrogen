using System.Text.RegularExpressions;
using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

internal sealed class QuoteHelperSourceIndex
{
    internal sealed record Occurrence(string Identity, string Path, GrammarSpan Span, bool Declaration, string Kind);
    readonly IReadOnlyDictionary<string, string> _texts;
    readonly List<(string Path, ModuleDecl Module)> _modules = [];
    readonly List<Occurrence> _occurrences = [];
    readonly GrammarAnalysis _analysis;
    readonly Language? _language;
    readonly HashSet<string> _unparsedHelpers = new(StringComparer.Ordinal);
    internal IEnumerable<string> Paths => _texts.Keys;

    public QuoteHelperSourceIndex(IReadOnlyDictionary<string, string> texts, string? completingPath = null, int completingOffset = 0, Language? language = null, Func<Language?>? languageFactory = null)
    {
        _texts = texts;
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
                }
            }
            if (parsed.File is { } file)
                _modules.AddRange(file.Modules.Select(module => (path, module)));
        }
        _analysis = new(_modules.Select(m => m.Module));
        _language = language ?? (_modules.Any(m => m.Module.Helpers.Count > 0) ? languageFactory?.Invoke() : null);
        foreach (var (path, module) in _modules)
        {
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
                var category = _analysis.Helpers.Category(helper.ReturnCategory, module);
                var parameters = helper.Parameters.GroupBy(p => p.Name.Name).Select(group => group.First()).ToDictionary(p => p.Name.Name,
                    p => new QuoteHole(_analysis.Helpers.Category(p.Category, module)?.Key ?? p.Category.Name, p.IsList));
                try
                {
                    using var parsedBody = category is null ? null : _language?.ParseTemplate(helper.Body.Text, category.Key, parameters);
                    if (parsedBody is not { Success: true }) { _unparsedHelpers.Add(id); continue; }
                    var tree = parsedBody.Tree;
                    for (int node = 0; node < tree.NodeCount; node++)
                    {
                        if (tree.Kind(node) is not (SyntaxKinds.Hole or SyntaxKinds.ListHole)) continue;
                        int marker = tree.Kind(node) == SyntaxKinds.ListHole ? 3 : 1;
                        var span = tree.Span(node);
                        string name = tree.GetText(node).ToString()[marker..];
                        Add(id + ":parameter:" + name, path,
                            new(helper.Body.Span.Start + span.Start + marker, name.Length), false, "parameter");
                    }
                }
                catch (ArgumentException) { _unparsedHelpers.Add(id); }

            }
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
        if (target.Kind is not ("helper" or "parameter"))
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
            ? identity == target.Identity || identity.StartsWith(target.Identity + ":parameter:", StringComparison.Ordinal)
                ? "helper:" + renames![target.Identity["helper:".Length..]] + identity[target.Identity.Length..] : identity
            : identity == target.Identity ? target.Identity[..(target.Identity.LastIndexOf(':') + 1)] + newName : identity;
        var beforeParameters = _occurrences.Where(o => o.Kind == "parameter").Distinct()
            .Select(o => (Identity: Expected(o.Identity), o.Path, Start: o.Span.Start + replacements.Where(e => e.Path == o.Path && e.Span.End <= o.Span.Start).Sum(e => newName.Length - e.Span.Length), o.Declaration))
            .ToHashSet();
        var afterParameters = after._occurrences.Where(o => o.Kind == "parameter").Distinct()
            .Select(o => (o.Identity, o.Path, o.Span.Start, o.Declaration)).ToHashSet();
        if (!beforeParameters.SetEquals(afterParameters))
            throw new RenameRefusedException("Rename would change the helper quote's parsed parameter structure.");
        return changed;
    }
}
