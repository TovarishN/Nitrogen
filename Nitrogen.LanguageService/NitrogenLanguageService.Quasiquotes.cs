using System.Text.Json;
using System.Text.RegularExpressions;
using Nitrogen.Grammar;
using Nitrogen.Workspace;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record QuoteRule(string Uri, string Text, ModuleDecl Module, string Name, string Category,
        Expr Body, GrammarSpan Span, IReadOnlyList<BindingClause> Clauses)
    {
        public string Id => Module.Name + "." + Name;
        public IEnumerable<LabeledExpr> Fields => Elements(Body).OfType<LabeledExpr>();
        public TextSpan NameSpan
        {
            get
            {
                var match = Regex.Match(Text[Span.Start..Body.Span.Start], @"\b" + Regex.Escape(Name) + @"\b");
                return new TextSpan(Span.Start + match.Index, match.Length);
            }
        }
    }

    sealed record QuoteContext(Document Document, QuoteRule Owner, NameDecl Template, IReadOnlyList<QuoteRule> Rules,
        Language? Language, IReadOnlyDictionary<string, QuoteHole> Holes);
    sealed record QuoteName(TextSpan Span, DocumentLocation Target, string Description, bool IsSplice = false);

    readonly Dictionary<string, (Dictionary<string, string> Texts, WorkspaceSnapshot? Snapshot)> _quoteLanguages = new(StringComparer.Ordinal);

    QuoteContext? QuoteAt(string uri, DocumentPosition position)
    {
        if (!uri.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase) || !TryDocument(uri, out var document)) return null;
        int offset = document.Lines.OffsetOf(position);
        var tree = document.Parsed.Tree;
        bool inTemplate = false;
        for (int node = 0; node < tree.NodeCount; node++)
            if (tree.Span(node).Start + 2 <= offset && offset <= tree.Span(node).End - 2 &&
                document.Language.Language.GetKindName(tree.Kind(node)) == "TemplateString")
            {
                inTemplate = true;
                break;
            }
        if (!inTemplate) return null;
        var (rules, modules) = QuoteRules(uri);
        var resolver = new GrammarAnalysis(modules);
        foreach (var owner in rules.Where(r => r.Uri == uri))
            foreach (var clause in owner.Clauses)
                if (clause is { Kind: BindingClauseKind.Rewrites, Target: { } template } &&
                    template.Span.Start <= offset && offset <= template.Span.End)
                {
                    var language = LanguageForQuote(uri);
                    string category = resolver.Resolve(owner.Category, owner.Span, owner.Module, null)?.Key ?? owner.Category;
                    string? implicitModule = category.Contains('.') ? category[..category.LastIndexOf('.')] : null;
                    var holes = new Dictionary<string, QuoteHole>(StringComparer.Ordinal);
                    foreach (var field in owner.Fields)
                    {
                        var shape = QuotationWriter.Shape(field.Inner);
                        if (shape.Item is { } reference && resolver.Resolve(reference.Name, reference.Span,
                            owner.Module, implicitModule) is { Rule: SyntaxRule or ExtensibleRule } resolved)
                            holes[field.Label] = new(resolved.Key, shape.IsList);
                    }
                    return new QuoteContext(document, owner with { Category = category }, template, rules, language, holes);
                }
        return null;
    }

    (List<QuoteRule> Rules, List<ModuleDecl> Modules) QuoteRules(string uri)
    {
        string path = new Uri(uri).LocalPath;
        var configured = _grammarLanguages.FirstOrDefault(l => l.Workspace.Paths.Contains(path, StringComparer.Ordinal));
        var paths = configured?.Files() ?? QuoteGrammarFiles(path) ?? NavigationScopeFor(path)?.Grammars.ToList() ?? [path];
        var rules = new List<QuoteRule>();
        var modules = new List<ModuleDecl>();
        foreach (string file in paths.Append(path).Distinct(StringComparer.Ordinal))
        {
            if (ReadNavigationText(file) is not { } text || GrammarParser.Parse(text).File is not { } model) continue;
            modules.AddRange(model.Modules);
            string fileUri = new Uri(file).AbsoluteUri;
            foreach (var module in model.Modules)
            {
                foreach (var rule in module.Rules)
                    switch (rule)
                    {
                        case SyntaxRule syntax:
                            rules.Add(new(fileUri, text, module, syntax.Name, module.Name + "." + syntax.Name, syntax.Body, syntax.Span, syntax.Clauses));
                            break;
                        case TokenRule token:
                            rules.Add(new(fileUri, text, module, token.Name, module.Name + "." + token.Name, token.Body, token.Span, []));
                            break;
                        case ExtensibleRule extensible:
                            foreach (var alternative in extensible.Alternatives)
                                Add(alternative, module.Name + "." + extensible.Name);
                            break;
                    }
                foreach (var extend in module.Extends)
                    foreach (var alternative in extend.Alternatives)
                        Add(alternative, extend.Target);

                void Add(Alternative alternative, string category) => rules.Add(new(fileUri, text, module,
                    alternative.Name, category, alternative.Body, alternative.Span, alternative.Clauses));
            }
        }
        return (rules, modules);
    }

    static IEnumerable<string> Literals(Expr body) => body switch
    {
        LiteralExpr literal => [literal.Value],
        SequenceExpr sequence => sequence.Items.SelectMany(Literals),
        ChoiceExpr choice => choice.Alternatives.SelectMany(Literals),
        LabeledExpr labeled => Literals(labeled.Inner),
        RepeatExpr repeat => Literals(repeat.Inner),
        SeparatedListExpr list => Literals(list.Item).Concat(Literals(list.Separator)),
        _ => [],
    };

    static IEnumerable<Expr> Elements(Expr body) => body is SequenceExpr sequence ? sequence.Items : [body];

    Language? LanguageForQuote(string uri)
    {
        string path = new Uri(uri).LocalPath;
        if (_grammarLanguages.FirstOrDefault(l => l.Workspace.Paths.Contains(path, StringComparer.Ordinal)) is { } configured)
            return configured.Snapshot?.Language;
        // A grammar opened by the general server may belong to a nested language configuration.
        return QuoteConfig(path) is { } config ? LocalQuoteLanguage(config, path) : null;
    }

    string? QuoteConfig(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        while (directory is not null)
        {
            string config = Path.Combine(directory, "nitrogen.json");
            if (File.Exists(config)) return config;
            if (directory == _root) break;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    static List<string> QuoteFiles(string root, JsonElement entry, string property) => entry.TryGetProperty(property, out var patterns)
        ? patterns.EnumerateArray().SelectMany(pattern =>
        {
            string full = Path.GetFullPath(Path.Combine(root, pattern.GetString()!));
            string dir = Path.GetDirectoryName(full)!;
            return Directory.Exists(dir) ? Directory.GetFiles(dir, Path.GetFileName(full)) : [];
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() : [];

    List<string>? QuoteGrammarFiles(string path)
    {
        if (QuoteConfig(path) is not { } config || ReadNavigationText(config) is not { } configuration) return null;
        try
        {
            using var json = JsonDocument.Parse(configuration);
            foreach (var entry in json.RootElement.GetProperty("languages").EnumerateArray())
            {
                var files = QuoteFiles(Path.GetDirectoryName(config)!, entry, "grammars");
                if (files.Contains(path, StringComparer.Ordinal)) return files;
            }
        }
        catch (JsonException) { }
        return null;
    }

    Language? LocalQuoteLanguage(string config, string path)
    {
        if (ReadNavigationText(config) is not { } configuration) return null;
        try
        {
            using var json = JsonDocument.Parse(configuration);
            string root = Path.GetDirectoryName(config)!;
            foreach (var entry in json.RootElement.GetProperty("languages").EnumerateArray())
            {
                var grammars = QuoteFiles(root, entry, "grammars");
                if (!grammars.Contains(path, StringComparer.Ordinal)) continue;
                string key = config + ":" + entry.GetProperty("name").GetString();
                var texts = new Dictionary<string, string>(StringComparer.Ordinal) { [config] = configuration };
                var sources = QuoteFiles(root, entry, "sources");
                foreach (string file in grammars.Concat(sources))
                    if (ReadNavigationText(file) is { } text) texts[file] = text;
                if (_quoteLanguages.TryGetValue(key, out var cached) && cached.Texts.Count == texts.Count &&
                    texts.All(p => cached.Texts.TryGetValue(p.Key, out var text) && text == p.Value)) return cached.Snapshot?.Language;
                var workspace = new GrammarWorkspace();
                if (entry.TryGetProperty("namespace", out var ns)) workspace.GeneratedNamespace = ns.GetString()!;
                if (entry.TryGetProperty("usings", out var usings))
                    foreach (var u in usings.EnumerateArray()) workspace.Usings.Add(u.GetString()!);
                foreach (string file in grammars)
                    if (texts.TryGetValue(file, out var text)) workspace.SetGrammar(file, text);
                foreach (string file in sources)
                    if (texts.TryGetValue(file, out var text)) workspace.Sources[file] = text;
                var snapshot = workspace.Compile();
                if (snapshot.Succeeded) cached.Snapshot?.Dispose();
                else
                {
                    snapshot.Dispose();
                    snapshot = cached.Snapshot;
                }
                _quoteLanguages[key] = (texts, snapshot);
                return snapshot?.Language;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    static DocumentLocation QuoteLocation(QuoteRule rule, TextSpan span) =>
        new(rule.Uri, new LineMap(rule.Text).RangeOf(span));

    IEnumerable<QuoteName> QuoteNames(QuoteContext quote)
    {
        using var parsed = ParseQuote(quote, quote.Template.Name);
        if (parsed is not { Success: true }) yield break;
        var tree = parsed.Tree;
        int leading = quote.Template.Name.Length - quote.Template.Name.TrimStart().Length;
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.ChildCount(node) != 0 || tree.Span(node).Length == 0) continue;
            if (tree.Kind(node) is SyntaxKinds.Hole or SyntaxKinds.ListHole)
            {
                var holeSpan = tree.Span(node);
                int marker = tree.Kind(node) == SyntaxKinds.ListHole ? 3 : 1;
                string label = quote.Template.Name.Substring(leading + holeSpan.Start + marker, holeSpan.Length - marker);
                if (quote.Owner.Fields.FirstOrDefault(f => f.Label == label) is { } field)
                    yield return new QuoteName(new TextSpan(quote.Template.Span.Start + leading + holeSpan.Start + marker, label.Length),
                        QuoteLocation(quote.Owner, new TextSpan(field.Span.Start, label.Length)),
                        $"`{label}` — field of `{quote.Owner.Name}`", IsSplice: true);
                continue;
            }
            int owner = node;
            while (owner >= 0 && SyntaxKinds.ModuleOf(tree.Kind(owner)) == 0) owner = tree.Parent(owner);
            if (owner < 0) continue;
            int kind = tree.Kind(owner);
            string? module = quote.Language!.Modules.FirstOrDefault(m => m.Id == SyntaxKinds.ModuleOf(kind))?.Name;
            var candidates = quote.Rules.Where(r => r.Id == module + "." + quote.Language!.GetKindName(kind)).ToArray();
            if (candidates.Length != 1) continue;
            var rule = candidates[0];
            var span = tree.Span(node);
            yield return new QuoteName(new TextSpan(quote.Template.Span.Start + leading + span.Start, span.Length),
                QuoteLocation(rule, rule.NameSpan), $"`{rule.Id}` — syntax rule");
        }
    }

    static ParseResult? ParseQuote(QuoteContext quote, string text)
    {
        try { return quote.Language?.ParseTemplate(text.Trim(), quote.Owner.Category, quote.Holes); }
        catch (ArgumentException) { return null; } // The retained snapshot can predate a category rename.
    }

    static (int Position, IReadOnlyList<(string Text, bool IsLiteral)> Items) ExpectedQuote(QuoteContext quote, string text)
    {
        try
        {
            int leading = text.Length - text.TrimStart().Length;
            var (at, items) = quote.Language?.ExpectedTemplate(text.TrimStart(), quote.Owner.Category, quote.Holes) ?? (-1, []);
            return (at < 0 ? at : at + leading, items);
        }
        catch (ArgumentException) { return (-1, []); }
    }

    QuoteName? QuoteNameAt(QuoteContext quote, int offset) => QuoteNames(quote)
        .FirstOrDefault(n => n.Span.Start <= offset && offset < n.Span.End);

    IReadOnlyList<DocumentLocation> QuoteDefinitions(QuoteContext quote, int offset) =>
        QuoteNameAt(quote, offset) is { } name ? [name.Target] : [];

    IReadOnlyList<DocumentLocation> QuoteReferences(QuoteContext quote, int offset, bool includeDeclaration)
    {
        if (QuoteNameAt(quote, offset) is not { } name) return [];
        var locations = new List<DocumentLocation>();
        if (includeDeclaration) locations.Add(name.Target);
        foreach (var rule in quote.Rules)
            foreach (var clause in rule.Clauses)
                if (clause is { Kind: BindingClauseKind.Rewrites, Target: { } template })
                {
                    var at = new LineMap(rule.Text).PositionOf(template.Span.Start);
                    if (QuoteAt(rule.Uri, at) is not { } context) continue;
                    foreach (var use in QuoteNames(context).Where(n => n.Target == name.Target))
                        locations.Add(new DocumentLocation(rule.Uri, new LineMap(rule.Text).RangeOf(use.Span)));
                }
        return locations.Distinct().ToArray();
    }

    IReadOnlyList<CompletionItem> QuoteCompletion(QuoteContext quote, int offset)
    {
        string text = quote.Document.Text;
        int start = offset;
        while (start > quote.Template.Span.Start && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        string prefix = text[start..offset];
        var replace = quote.Document.Lines.RangeOf(new TextSpan(start, offset - start));
        if (start > quote.Template.Span.Start && text[start - 1] == '$')
        {
            if (QuoteNameAt(quote, start) is { IsSplice: false }) return [];
            bool list = start >= 3 && text[(start - 3)..start] == "..$";
            int marker = start - (list ? 3 : 1) - quote.Template.Span.Start;
            if (!QuotationWriter.Holes(quote.Template.Name.Insert(offset - quote.Template.Span.Start, "X")).Any(h => h.Start == marker)) return [];
            return quote.Owner.Fields.Where(f => quote.Holes.TryGetValue(f.Label, out var hole) && hole.IsList == list && f.Label.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new CompletionItem(f.Label, CompletionKind.Field, "splice field", replace)).ToArray();
        }
        if (quote.Language is null) return [];
        string before = text[quote.Template.Span.Start..start];
        var (at, expected) = ExpectedQuote(quote, before + "\u0001");
        if (at < before.TrimEnd().Length) return [];
        var words = expected.Where(i => i.IsLiteral).Select(i => i.Text)
            .Concat(quote.Rules.SelectMany(r => Literals(r.Body)))
            .Where(word => SemanticTokenBuilder.IsWord(word) && word.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        // A complete expression can stop before a postfix operator. Probe the grammar's literals too:
        // its ordinary failure expectations then contain only "end of input".
        return words.Where(word =>
        {
            string candidate = before + word;
            var (end, _) = ExpectedQuote(quote, candidate + "\u0001");
            return end >= candidate.Length;
        }).Select(word => new CompletionItem(word, CompletionKind.Keyword, "template keyword", replace)).ToArray();
    }
}
