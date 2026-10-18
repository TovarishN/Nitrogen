using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

/// <summary>The size of a grammar function call's expansion, counted as the tree it stands for (shared parts once per use).</summary>
public sealed record GrammarExpansionSize(long Nodes, long Classes, int Depth);

/// <summary>
/// A grammar function call and what it expands to (spec: grammar functions). <see cref="Text"/> is the expansion as .ngr
/// source; null when evaluation failed or the expansion is too large to show.
/// </summary>
public sealed record GrammarCallExpansion(string Call, string Signature, DocumentRange Range, GrammarExpansionSize? Size, string? Text);

/// <summary>A refactoring the service offers at a range: its edits, or why it is unavailable there.</summary>
public sealed record ServiceRefactoring(string Title, IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>> Edits, string? DisabledReason = null);

/// <summary>
/// Editor support for grammar functions (spec: grammar functions): hover with the call's expansion size, the inline-expansion
/// refactoring, semantic tokens, completion of function names and rename of functions and their variables. Navigation is
/// in <see cref="GrammarRuleAt"/>, which shares the occurrence index.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    /// <summary>Expansions with more nodes than this are summarized but not shown or inlined.</summary>
    const long MaxShownExpansionNodes = 10_000;

    /// <summary>A hover shows at most this many lines of an expansion.</summary>
    const int MaxHoverExpansionLines = 40;

    /// <summary>The signature and meaning of each built-in grammar function (<see cref="GrammarFunctions.IsBuiltin"/>).</summary>
    static readonly Dictionary<string, (string Signature, string Meaning)> s_builtinDocs = new(StringComparer.Ordinal)
    {
        ["char"] = ("char(code: Grammar.Int) : Grammar.Char", "the character with this UTF-16 code."),
        ["code"] = ("code(c: Grammar.Char) : Grammar.Int", "the UTF-16 code of a character."),
        ["upper"] = ("upper(c: Grammar.Char) : Grammar.Char", "the character in upper case (invariant culture)."),
        ["lower"] = ("lower(c: Grammar.Char) : Grammar.Char", "the character in lower case (invariant culture)."),
        ["length"] = ("length(text: Grammar.Text) : Grammar.Int", "the number of characters in a text."),
    };

    static readonly HashSet<string> s_grammarWords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "choice", "seq", "let", "where", "in", "or", "and", "not", "function", "syntax", "token",
        "module", "using", "precedence", "except", "declares", "references", "scope", "dynamic", "lowers", "rewrites", "match",
        "char", "code", "upper", "lower", "length",
    };

    /// <summary>The grammar function call at <paramref name="position"/> in a rule body and its expansion; null elsewhere.</summary>
    public GrammarCallExpansion? ExpandGrammarCall(string uri, DocumentPosition position)
    {
        if (GrammarOccurrencesOf(uri) is not { } scope) return null;
        int offset = new LineMap(scope.Text).OffsetOf(position);
        return CallExpansion(scope, call => call.Span.Start <= offset && offset < call.Span.End);
    }

    /// <summary>Replaces the grammar function call under <paramref name="range"/> with its expansion as .ngr text.</summary>
    public ServiceRefactoring? InlineGrammarCall(string uri, DocumentRange range)
    {
        if (GrammarOccurrencesOf(uri) is not { } scope) return null;
        var lines = new LineMap(scope.Text);
        int start = lines.OffsetOf(range.Start), end = lines.OffsetOf(range.End);
        GrammarCallExpr? site = null;
        var expansion = CallExpansion(scope, call =>
        {
            bool hit = call.Span.Start <= start && end <= call.Span.End && (start < call.Span.End || start == end && start == call.Span.End);
            if (hit) site = call;
            return hit;
        }, inlineContext: true);
        if (expansion is null || site is null) return null;
        string title = $"Inline expansion of {expansion.Call}";
        if (expansion.Size is not { } size) return new(title, new Dictionary<string, IReadOnlyList<DocumentEdit>>(), "The call does not expand; see its diagnostics.");
        if (expansion.Text is not { } text)
            return new(title, new Dictionary<string, IReadOnlyList<DocumentEdit>>(), $"The expansion is too large to inline ({size.Nodes} nodes).");
        return new(title, new Dictionary<string, IReadOnlyList<DocumentEdit>>
        {
            [uri] = [new DocumentEdit(lines.RangeOf(new(site.Span.Start, site.Span.Length)), text)],
        });
    }

    /// <summary>The first call in the current file's rule bodies that <paramref name="matches"/>, with its expansion.</summary>
    GrammarCallExpansion? CallExpansion(GrammarOccurrences scope, Func<GrammarCallExpr, bool> matches, bool inlineContext = false)
    {
        if (scope.Files.FirstOrDefault(f => f.Path == scope.Path) is not { } current) return null;
        (GrammarCallExpr Call, ModuleDecl Module, int Level)? found = null;
        foreach (var module in current.File.Modules)
        {
            foreach (var (body, _) in RuleBodies(module))
                if (Find(body, 0) is { } hit) { found = (hit.Call, module, hit.Level); break; }
            if (found is not null) break;
        }
        if (found is not { } site) return null;

        Expr? expansion = null;
        GrammarFunctions.Expand(scope.Files.SelectMany(f => f.File.Modules), (call, expanded) =>
        {
            if (ReferenceEquals(call, site.Call)) expansion = expanded;
        });
        string callText = scope.Text.Substring(site.Call.Span.Start, site.Call.Span.Length);
        string signature = FindGrammarFunction(scope, site.Call.Name.Name, site.Module) is { } function
            ? GrammarFunctionSignature(function.Declaration) : site.Call.Name.Name;
        var range = new LineMap(scope.Text).RangeOf(new(site.Call.Span.Start, site.Call.Span.Length));
        if (expansion is null) return new(callText, signature, range, null, null);
        var size = ExpansionSize(expansion);
        string? text = null;
        if (size.Nodes <= MaxShownExpansionNodes)
        {
            // Inlined text continues lines under the call's line, indented one step further.
            int lineStart = scope.Text.LastIndexOf('\n', Math.Max(0, site.Call.Span.Start - 1)) + 1;
            int indentEnd = lineStart;
            while (indentEnd < site.Call.Span.Start && scope.Text[indentEnd] is ' ' or '\t') indentEnd++;
            text = inlineContext
                ? GrammarExpressionText.Layout(expansion, site.Level, scope.Text[lineStart..indentEnd] + "  ")
                : GrammarExpressionText.Layout(expansion);
        }
        return new(callText, signature, range, size, text);

        (GrammarCallExpr Call, int Level)? Find(Expr expression, int level)
        {
            if (expression is GrammarCallExpr call && matches(call)) return (call, level);
            foreach (var child in GrammarExtraction.Children(expression))
                if (Find(child, GrammarExpressionText.ChildLevel(expression)) is { } hit) return hit;
            return null;
        }
    }

    static IEnumerable<(Expr Body, bool InToken)> RuleBodies(ModuleDecl module)
    {
        foreach (var rule in module.Rules)
            switch (rule)
            {
                case TokenRule token: yield return (token.Body, true); break;
                case SyntaxRule syntax: yield return (syntax.Body, false); break;
                case ExtensibleRule extensible:
                    foreach (var alternative in extensible.Alternatives) yield return (alternative.Body, false);
                    break;
            }
        foreach (var extend in module.Extends)
            foreach (var alternative in extend.Alternatives) yield return (alternative.Body, false);
    }

    /// <summary>Nodes, classes and depth of the tree an expansion stands for; shared subtrees are counted once per use.</summary>
    internal static GrammarExpansionSize ExpansionSize(Expr root)
    {
        var memo = new Dictionary<Expr, GrammarExpansionSize>(ReferenceEqualityComparer.Instance);
        return Size(root);

        GrammarExpansionSize Size(Expr expression)
        {
            if (memo.TryGetValue(expression, out var known)) return known;
            long nodes = 1, classes = expression is CharClassExpr ? 1 : 0;
            int depth = 0;
            foreach (var child in GrammarExtraction.Children(expression))
            {
                var size = Size(child);
                nodes += size.Nodes;
                classes += size.Classes;
                depth = Math.Max(depth, size.Depth);
            }
            return memo[expression] = new(nodes, classes, depth + 1);
        }
    }

    sealed record GrammarFunctionTarget(ModuleDecl Module, GrammarFunctionDecl Declaration, string Path);

    /// <summary>The function a call names from <paramref name="from"/>: qualified, the module's own, or one of its <c>using</c>s.</summary>
    static GrammarFunctionTarget? FindGrammarFunction(GrammarOccurrences scope, string name, ModuleDecl from)
    {
        var modules = scope.Files.SelectMany(f => f.File.Modules.Select(m => (Module: m, f.Path))).ToList();
        int dot = name.LastIndexOf('.');
        var owners = dot >= 0 ? [name[..dot]] : new[] { from.Name }.Concat(from.Usings.Select(u => u.Module));
        string local = dot >= 0 ? name[(dot + 1)..] : name;
        foreach (string owner in owners)
            foreach (var (module, path) in modules.Where(m => m.Module.Name == owner))
                if (module.Functions.FirstOrDefault(f => f.Name.Name == local) is { } function)
                    return new(module, function, path);
        return null;
    }

    static GrammarFunctionTarget? FunctionOf(GrammarOccurrences scope, string identity)
    {
        string qualified = identity["function ".Length..].Split(' ')[0];
        int dot = qualified.LastIndexOf('.');
        string owner = qualified[..dot], local = qualified[(dot + 1)..];
        foreach (var file in scope.Files)
            foreach (var module in file.File.Modules.Where(m => m.Name == owner))
                if (module.Functions.FirstOrDefault(f => f.Name.Name == local) is { } function) return new(module, function, file.Path);
        return null;
    }

    static string GrammarFunctionSignature(GrammarFunctionDecl function) =>
        $"function {function.Name.Name}(" + string.Join(", ", function.Parameters.Select(p => $"{p.Name.Name}: {p.Category.Name}" + (p.IsList ? "*" : ""))) +
        $") : {function.ReturnType.Name}";

    HoverInfo? GrammarFunctionHover(string uri, DocumentPosition position)
    {
        if (GrammarOccurrencesOf(uri) is not { } scope) return null;
        var lines = new LineMap(scope.Text);
        int offset = lines.OffsetOf(position);
        var under = OccurrencesAt(scope, offset);
        if (under.Any(o => o.Kind == GrammarNameKind.Rule)) return null;
        var found = under.FirstOrDefault();
        if (found is null)
        {
            // Elsewhere on a call (its arguments, parentheses): the call's expansion.
            if (CallExpansion(scope, call => call.Span.Start <= offset && offset < call.Span.End) is not { } call) return null;
            return new HoverInfo($"```ngr\n{call.Signature}\n```\n" + ExpansionMarkdown(call), call.Range);
        }
        var range = lines.RangeOf(new(found.Span.Start, found.Span.Length));
        if (found.Kind == GrammarNameKind.Builtin)
        {
            var (signature, meaning) = s_builtinDocs[scope.Text.Substring(found.Span.Start, found.Span.Length)];
            return new HoverInfo($"```ngr\n{signature}\n```\nBuilt-in grammar function: {meaning}", range);
        }
        string functionIdentity = string.Join(' ', found.Identity.Split(' ').Take(2));
        if (FunctionOf(scope, functionIdentity) is not { } function) return null;
        if (found.Kind != GrammarNameKind.Function)
        {
            string name = scope.Text.Substring(found.Span.Start, found.Span.Length);
            var definition = scope.Occurrences.FirstOrDefault(o => o.Identity == found.Identity && o.IsDefinition);
            string shown = found.Kind == GrammarNameKind.Parameter && function.Declaration.Parameters
                .FirstOrDefault(p => p.Name.Name == name && definition is not null && p.Name.Span.Start == definition.Span.Start) is { } parameter
                ? $"`parameter {name}: {parameter.Category.Name}{(parameter.IsList ? "*" : "")}`"
                : $"`variable {name}`";
            return new HoverInfo($"{shown} — in `{function.Declaration.Name.Name}`", range);
        }
        var text = new StringBuilder($"```ngr\n{GrammarFunctionSignature(function.Declaration)}\n```\n");
        var at = new LineMap(scope.Files.First(f => f.Path == function.Path).Text).PositionOf(function.Declaration.Name.Span.Start);
        text.Append($"Grammar function of `{function.Module.Name}` — declared in {FileName(new Uri(function.Path).AbsoluteUri)} line {at.Line + 1}.");
        if (CallExpansion(scope, call => call.Name.Span.Start == found.Span.Start) is { } expansion)
            text.Append("\n\n").Append(ExpansionMarkdown(expansion));
        return new HoverInfo(text.ToString(), range);
    }

    /// <summary>A call's expansion for a hover: its size, then its text up to <see cref="MaxHoverExpansionLines"/> lines.</summary>
    static string ExpansionMarkdown(GrammarCallExpansion expansion)
    {
        if (expansion.Size is not { } size) return "The call does not expand; see its diagnostics.";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Expands to {size.Nodes} nodes, {size.Classes} {(size.Classes == 1 ? "class" : "classes")}, depth {size.Depth}");
        if (expansion.Text is not { } shown) return text.Append(" — too large to show.").ToString();
        string[] lines = shown.Split('\n');
        text.Append(":\n```ngr\n").AppendJoin('\n', lines.Take(MaxHoverExpansionLines)).Append("\n```");
        if (lines.Length > MaxHoverExpansionLines)
            text.Append(CultureInfo.InvariantCulture, $"\n{lines.Length - MaxHoverExpansionLines} more lines; the inline refactoring's preview shows the whole expansion.");
        return text.ToString();
    }

    /// <summary>
    /// The grammar function diagnostics (NGR0150–NGR0159) of the modules declared in this grammar, checked and expanded over
    /// its navigation scope: unknown names, wrong argument types and counts, evaluation errors and limits.
    /// </summary>
    IEnumerable<ServiceDiagnostic> GrammarFunctionDiagnostics(string uri)
    {
        if (GrammarOccurrencesOf(uri) is not { } scope || scope.Files.FirstOrDefault(f => f.Path == scope.Path) is not { } current) return [];
        var lines = new LineMap(scope.Text);
        return GrammarFunctions.Expand(scope.Files.SelectMany(f => f.File.Modules)).Diagnostics
            .Where(d => current.File.Modules.Any(m => m.Name == d.Module && m.Span.Start <= d.Span.Start && d.Span.Start + d.Span.Length <= m.Span.End))
            .Select(d => new ServiceDiagnostic(lines.RangeOf(new(d.Span.Start, d.Span.Length)),
                d.Severity == GrammarSeverity.Error ? ServiceSeverity.Error : ServiceSeverity.Warning, d.Code, d.Message))
            .ToList();
    }

    /// <summary>The uses in this grammar of the function, variable or built-in at <paramref name="position"/>; declarations are writes.</summary>
    IReadOnlyList<DocumentHighlight> GrammarFunctionHighlights(string uri, DocumentPosition position)
    {
        if (GrammarOccurrencesOf(uri) is not { } scope) return [];
        var lines = new LineMap(scope.Text);
        int offset = lines.OffsetOf(position);
        var found = OccurrencesAt(scope, offset, o => o.Kind != GrammarNameKind.Rule).FirstOrDefault();
        if (found is null) return [];
        return scope.Occurrences.Where(o => o.Identity == found.Identity && o.Path == scope.Path)
            .Select(o => new DocumentHighlight(lines.RangeOf(new(o.Span.Start, o.Span.Length)), o.IsDefinition ? HighlightKind.Write : HighlightKind.Read))
            .Distinct().ToList();
    }

    /// <summary>Function names as functions, parameters as parameters, <c>let</c> and loop variables as variables, in an open grammar.</summary>
    IEnumerable<AssistToken> GrammarFunctionTokens(Document document)
    {
        if (!IsGrammarDocument(document.Uri)) return [];
        if (GrammarParser.Parse(DocumentSignatures.Read(document.Text).MaskedText).File is not { } file) return [];
        var calls = new List<NameDecl>();
        var occurrences = CollectGrammarOccurrences([new GrammarScopeFile(document.Uri, document.Text, file)], (_, name) => calls.Add(name));
        return occurrences.Where(o => o.Kind != GrammarNameKind.Rule).Select(o => new AssistToken(new(o.Span.Start, o.Span.Length), o.Kind switch
            {
                GrammarNameKind.Function or GrammarNameKind.Builtin => TokenType.Function,
                GrammarNameKind.Parameter => TokenType.Parameter,
                _ => TokenType.Variable,
            }))
            .Concat(calls.Select(c => new AssistToken(new(c.Span.Start, c.Span.Length), TokenType.Function)))
            .DistinctBy(t => t.Span.Start).ToList();
    }

    /// <summary>The grammar functions callable where a call may start: the module's own and those of its <c>using</c>s.</summary>
    IReadOnlyList<CompletionItem> GrammarFunctionCompletion(Document document, int offset)
    {
        if (!IsGrammarDocument(document.Uri)) return [];
        string text = document.Text;
        int wordStart = offset;
        while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_')) wordStart--;
        if (wordStart > 0 && text[wordStart - 1] == '.') return [];
        var (at, expected) = document.Language.Language.Expected(text[..wordStart] + "\u0001", document.Start);
        if (at < 0 || !string.IsNullOrWhiteSpace(text[Math.Min(at, wordStart)..Math.Max(at, wordStart)]) ||
            !expected.Any(e => !e.IsLiteral && e.Text == "CallHead")) return [];

        // The text being typed may not parse; a placeholder name in its place usually does.
        string masked = DocumentSignatures.Read(text).MaskedText;
        const string Placeholder = "NitrogenCompletionName";
        var file = GrammarParser.Parse(masked[..wordStart] + Placeholder + masked[offset..]).File;
        var module = file?.Modules.FirstOrDefault(m => m.Span.Start <= wordStart && wordStart < m.Span.End);
        string? moduleName = module?.Name;
        IEnumerable<string> usings = module?.Usings.Select(u => u.Module) ?? [];
        if (module is null)
        {
            // Fall back to the text: the last module header before the cursor and its usings.
            var header = Regex.Matches(masked[..wordStart], @"\bsyntax\s+module\s+([A-Za-z_][\w.]*)").LastOrDefault();
            if (header is null) return [];
            moduleName = header.Groups[1].Value;
            usings = Regex.Matches(masked[header.Index..], @"\busing\s+([A-Za-z_][\w.]*)\s*;").Select(m => m.Groups[1].Value).ToList();
        }

        var modules = new List<ModuleDecl>();
        if (file is not null) modules.AddRange(file.Modules);
        if (Uri.TryCreate(document.Uri, UriKind.Absolute, out var parsed) && parsed.IsFile && NavigationScopeFor(parsed.LocalPath) is { } scope)
            foreach (string path in scope.Grammars.Where(p => p != parsed.LocalPath))
                if (ReadNavigationText(path) is { } other && GrammarParser.Parse(DocumentSignatures.Read(other).MaskedText).File is { } otherFile)
                    modules.AddRange(otherFile.Modules);

        string word = text[wordStart..offset];
        var replace = new DocumentRange(document.Lines.PositionOf(wordStart), document.Lines.PositionOf(offset));
        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string owner in usings.Prepend(moduleName!).Distinct(StringComparer.Ordinal))
            foreach (var function in modules.Where(m => m.Name == owner).SelectMany(m => m.Functions))
                if (function.Name.Name.StartsWith(word, StringComparison.Ordinal) && seen.Add(function.Name.Name))
                    items.Add(new CompletionItem(function.Name.Name, CompletionKind.Function,
                        GrammarFunctionSignature(function) + (owner == moduleName ? "" : $" ({owner})"), replace));
        return items;
    }

    /// <summary>The function, parameter or variable a rename at <paramref name="position"/> would change; null for anything else.</summary>
    (GrammarOccurrences Scope, GrammarRuleOccurrence Target)? GrammarFunctionRenameTarget(string uri, DocumentPosition position)
    {
        if (GrammarOccurrencesOf(uri) is not { } scope) return null;
        int offset = new LineMap(scope.Text).OffsetOf(position);
        var found = OccurrencesAt(scope, offset, o => o.Kind != GrammarNameKind.Rule).FirstOrDefault();
        if (found is null) return null;
        if (found.Kind == GrammarNameKind.Builtin)
            throw new RenameRefusedException($"'{NameText(scope, found)}' is a built-in grammar function.");
        if (!scope.Occurrences.Any(o => o.Identity == found.Identity && o.IsDefinition))
            throw new RenameRefusedException($"'{NameText(scope, found)}' is not declared in this grammar's scope.");
        return (scope, found);
    }

    /// <summary>The written name of an occurrence; a qualified call's last segment.</summary>
    static string NameText(GrammarOccurrences scope, GrammarRuleOccurrence occurrence) =>
        LastSegment(scope, occurrence) is var (start, length) ? TextOf(scope, occurrence.Path).Substring(start, length) : "";

    static (int Start, int Length) LastSegment(GrammarOccurrences scope, GrammarRuleOccurrence occurrence)
    {
        string written = TextOf(scope, occurrence.Path).Substring(occurrence.Span.Start, occurrence.Span.Length);
        int dot = written.LastIndexOf('.');
        return dot < 0 ? (occurrence.Span.Start, occurrence.Span.Length) : (occurrence.Span.Start + dot + 1, occurrence.Span.Length - dot - 1);
    }

    static string TextOf(GrammarOccurrences scope, string path) => scope.Files.First(f => f.Path == path).Text;

    DocumentRange PrepareGrammarFunctionRename(GrammarOccurrences scope, GrammarRuleOccurrence target)
    {
        var (start, length) = LastSegment(scope, target);
        return new LineMap(scope.Text).RangeOf(new(start, length));
    }

    IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>> RenameGrammarFunction(GrammarOccurrences scope, GrammarRuleOccurrence target, string newName)
    {
        string oldName = NameText(scope, target);
        if (!Regex.IsMatch(newName, @"^[A-Za-z_][A-Za-z0-9_]*$") || s_grammarWords.Contains(newName))
            throw new RenameRefusedException($"'{newName}' is not a valid {(target.Kind == GrammarNameKind.Function ? "function" : "variable")} name.");
        var uses = scope.Occurrences.Where(o => o.Identity == target.Identity).ToList();
        var definition = uses.First(o => o.IsDefinition);
        if (target.Kind == GrammarNameKind.Function)
        {
            var function = FunctionOf(scope, target.Identity) ?? throw new RenameRefusedException("Nothing to rename here.");
            if (function.Module.Functions.Any(f => f.Name.Name == newName) || function.Module.Helpers.Any(h => h.Name.Name == newName))
                throw new RenameRefusedException($"'{newName}' already names a function in module '{function.Module.Name}'.");
        }
        else
        {
            // A name already used inside the function would be captured or would shadow: refuse rather than change meaning.
            var function = FunctionOf(scope, string.Join(' ', target.Identity.Split(' ').Take(2))) ?? throw new RenameRefusedException("Nothing to rename here.");
            var span = function.Declaration.Span;
            if (scope.Occurrences.Any(o => o.Path == definition.Path && span.Start <= o.Span.Start && o.Span.End <= span.End && NameText(scope, o) == newName))
                throw new RenameRefusedException($"'{newName}' is already used in '{function.Declaration.Name.Name}'.");
        }
        if (newName == oldName) return new Dictionary<string, IReadOnlyList<DocumentEdit>>();
        return uses.GroupBy(o => o.Path).ToDictionary(g => new Uri(g.Key).AbsoluteUri, g =>
        {
            var lines = new LineMap(TextOf(scope, g.Key));
            return (IReadOnlyList<DocumentEdit>)g.Select(o => LastSegment(scope, o)).Distinct()
                .Select(s => new DocumentEdit(lines.RangeOf(new(s.Start, s.Length)), newName)).ToList();
        }, StringComparer.Ordinal);
    }
}

/// <summary>A grammar expression as .ngr source, with the fewest parentheses its precedence needs.</summary>
internal static class GrammarExpressionText
{
    /// <summary>Precedence levels: 0 a choice may stand here, 1 a sequence, 2 an element (label, prefix, postfix), 3 a primary only.</summary>
    public static string Format(Expr expression, int level = 0)
    {
        var text = new StringBuilder();
        Write(text, expression, level);
        return text.ToString();
    }

    /// <summary>
    /// <see cref="Format"/>, but a choice that would make its line longer than <paramref name="width"/> is broken into one
    /// alternative per line, each continuation line starting with <paramref name="indent"/>. A parenthesized choice becomes
    /// a block whose alternatives are indented under its parentheses.
    /// </summary>
    public static string Layout(Expr expression, int level = 0, string indent = "", int width = 80)
    {
        var text = new StringBuilder();
        WriteLaidOut(text, expression, level, indent, width);
        return text.ToString();
    }

    static void WriteLaidOut(StringBuilder text, Expr expression, int level, string indent, int width)
    {
        string flat = Format(expression, level);
        if (indent.Length + flat.Length <= width) { text.Append(flat); return; }
        switch (expression)
        {
            case ChoiceExpr { Alternatives.Count: 1 } single: WriteLaidOut(text, single.Alternatives[0], level, indent, width); break;
            case SequenceExpr { Items.Count: 1 } single: WriteLaidOut(text, single.Items[0], level, indent, width); break;
            case ChoiceExpr { Alternatives.Count: > 1 } choice when level > 0:
                text.Append('(');
                for (int i = 0; i < choice.Alternatives.Count; i++)
                {
                    text.Append('\n').Append(indent).Append(i == 0 ? "    " : "  / ");
                    WriteLaidOut(text, choice.Alternatives[i], 1, indent + "    ", width);
                }
                text.Append('\n').Append(indent).Append(')');
                break;
            case ChoiceExpr { Alternatives.Count: > 1 } choice:
                for (int i = 0; i < choice.Alternatives.Count; i++)
                {
                    if (i > 0) text.Append('\n').Append(indent).Append("/ ");
                    WriteLaidOut(text, choice.Alternatives[i], 1, indent + "  ", width);
                }
                break;
            case SequenceExpr { Items.Count: > 1 } sequence:
                if (level > 1) text.Append('(');
                for (int i = 0; i < sequence.Items.Count; i++)
                {
                    if (i > 0) text.Append(' ');
                    WriteLaidOut(text, sequence.Items[i], 2, indent, width);
                }
                if (level > 1) text.Append(')');
                break;
            case LabeledExpr labeled when level <= 2:
                text.Append(labeled.Label).Append(':');
                WriteLaidOut(text, labeled.Inner, labeled.Inner is LabeledExpr ? 3 : 2, indent, width);
                break;
            case RepeatExpr repeat when level <= 2:
                WriteLaidOut(text, repeat.Inner, 3, indent, width);
                text.Append(repeat.Kind switch { RepeatKind.Optional => '?', RepeatKind.ZeroOrMore => '*', _ => '+' });
                break;
            case PredicateExpr predicate when level <= 2:
                text.Append(predicate.Kind == PredicateKind.Not ? '!' : '&');
                WriteLaidOut(text, predicate.Inner, 3, indent, width);
                break;
            default: text.Append(flat); break;
        }
    }

    /// <summary>The level an expression's children are written at.</summary>
    public static int ChildLevel(Expr parent) => parent switch
    {
        ChoiceExpr => 1,
        SequenceExpr => 2,
        LabeledExpr => 2,
        SeparatedListExpr => 0,
        _ => 3,
    };

    static int LevelOf(Expr expression) => expression switch
    {
        ChoiceExpr { Alternatives.Count: > 1 } => 0,
        SequenceExpr { Items.Count: > 1 } => 1,
        LabeledExpr or RepeatExpr or PredicateExpr => 2,
        _ => 3,
    };

    static void Write(StringBuilder text, Expr expression, int level)
    {
        bool parenthesize = LevelOf(expression) < level;
        if (parenthesize) text.Append('(');
        switch (expression)
        {
            case ChoiceExpr { Alternatives.Count: 1 } single: Write(text, single.Alternatives[0], level); break;
            case SequenceExpr { Items.Count: 1 } single: Write(text, single.Items[0], level); break;
            case SequenceExpr { Items.Count: 0 }: text.Append("()"); break;
            case ChoiceExpr choice: Join(choice.Alternatives, " / ", 1); break;
            case SequenceExpr sequence: Join(sequence.Items, " ", 2); break;
            case LabeledExpr labeled:
                text.Append(labeled.Label).Append(':');
                Write(text, labeled.Inner, labeled.Inner is LabeledExpr ? 3 : 2);
                break;
            case RepeatExpr repeat:
                Write(text, repeat.Inner, 3);
                text.Append(repeat.Kind switch { RepeatKind.Optional => '?', RepeatKind.ZeroOrMore => '*', _ => '+' });
                break;
            case PredicateExpr predicate:
                text.Append(predicate.Kind == PredicateKind.Not ? '!' : '&');
                Write(text, predicate.Inner, 3);
                break;
            case SeparatedListExpr list:
                text.Append('(');
                Write(text, list.Item, 0);
                text.Append("; ");
                Write(text, list.Separator, 0);
                text.Append(')').Append(list.AtLeastOne ? '+' : '*');
                break;
            case LiteralExpr literal: text.Append(Quote(literal.Value, '"')); break;
            case CharClassExpr characters:
                text.Append('[');
                if (characters.Negated) text.Append("^ ");
                text.Append(string.Join(" ", characters.Ranges.Select(r => r.First == r.Last
                    ? Quote(r.First.ToString(), '\'') : Quote(r.First.ToString(), '\'') + ".." + Quote(r.Last.ToString(), '\''))));
                text.Append(']');
                break;
            case AnyCharExpr: text.Append('.'); break;
            case ReferenceExpr reference: text.Append(reference.Name); break;
            default: text.Append(GrammarDumper.Dump(expression)); break;
        }
        if (parenthesize) text.Append(')');

        void Join(IEnumerable<Expr> items, string separator, int itemLevel)
        {
            bool first = true;
            foreach (var item in items)
            {
                if (!first) text.Append(separator);
                first = false;
                Write(text, item, itemLevel);
            }
        }
    }

    /// <summary>A string or character literal with the escapes the grammar lexer reads.</summary>
    static string Quote(string value, char quote)
    {
        var text = new StringBuilder().Append(quote);
        foreach (char c in value)
            text.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                '\\' => "\\\\",
                '"' when quote == '"' => "\\\"",
                '\'' when quote == '\'' => "\\'",
                _ when c < ' ' || c > '~' => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        return text.Append(quote).ToString();
    }
}
