namespace Nitrogen.Grammar;

internal sealed record QuoteExtractionSelection(ModuleDecl Module, string Owner, Expr Body,
    BindingClause Clause, string Category, string? ImplicitModule, GrammarSpan Quote);
internal sealed record QuoteExtractionField(string Name, string Category, bool IsList);

/// <summary>Source-preserving transformations; the composed parser supplies actual hole names.</summary>
internal static class QuoteHelperExtraction
{
    internal static QuoteExtractionSelection? Select(IEnumerable<ModuleDecl> modules, string source,
        GrammarSpan selection, GrammarAnalysis analysis)
    {
        foreach (var module in modules)
        {
            foreach (var rule in module.Rules)
            {
                if (rule is SyntaxRule syntax && Find(syntax.Name, syntax.Body, syntax.Clauses,
                    QuotationWriter.Category(analysis, module, syntax, null).Key, null) is { } found)
                    return found;
                if (rule is ExtensibleRule point)
                    foreach (var alternative in point.Alternatives)
                        if (Find(alternative.Name, alternative.Body, alternative.Clauses,
                            module.Name + "." + point.Name, module.Name) is { } item) return item;
            }
            foreach (var extend in module.Extends)
            {
                var target = analysis.Resolve(extend.Target, extend.Span, module, null);
                if (target is null) continue;
                foreach (var alternative in extend.Alternatives)
                    if (Find(alternative.Name, alternative.Body, alternative.Clauses,
                        target.Key, target.Module.Name) is { } item) return item;
            }

            QuoteExtractionSelection? Find(string owner, Expr body, IEnumerable<BindingClause> clauses,
                string category, string? implicitModule)
            {
                foreach (var clause in clauses)
                {
                    if (clause.Kind != BindingClauseKind.Rewrites || clause.HelperCall is not null || clause.Target is null) continue;
                    var quote = new GrammarSpan(clause.Target.Span.Start - 2, clause.Target.Span.Length + 4);
                    bool caret = selection.Length == 0 && selection.Start >= quote.Start && selection.Start < quote.End;
                    bool whole = selection.Length > 0 && selection.Start <= quote.Start && selection.End >= quote.End &&
                        source.Substring(selection.Start, quote.Start - selection.Start).Trim().Length == 0 &&
                        source.Substring(quote.End, selection.End - quote.End).Trim().Length == 0;
                    if (caret || whole) return new(module, owner, body, clause, category, implicitModule, quote);
                }
                return null;
            }
        }
        return null;
    }

    internal static IReadOnlyList<QuoteExtractionField> Fields(QuoteExtractionSelection selected, GrammarAnalysis analysis)
    {
        var result = new List<QuoteExtractionField>();
        foreach (var field in SyntaxCodeWriter.Elements(selected.Body).OfType<LabeledExpr>())
        {
            var shape = QuotationWriter.Shape(field.Inner);
            if (shape.Item is not { } reference) continue;
            var category = analysis.Resolve(reference.Name, reference.Span, selected.Module, selected.ImplicitModule);
            if (category?.Rule is SyntaxRule or ExtensibleRule)
                result.Add(new(field.Label, category.Key, shape.IsList));
        }
        return result;
    }

    internal static ExtractionTransform Transform(ExtractionSource source, QuoteExtractionSelection selected,
        IReadOnlyList<QuoteExtractionField> parameters, ExtractionSource? destination, string destinationUri,
        string moduleName, string helperName, GrammarAnalysis analysis)
    {
        ExtractionTransform Refuse(string message) => new("", [], [], [new("NX0001", message, source.Uri, selected.Quote)]);
        bool Identifier(string text) => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') &&
            text.All(c => char.IsLetterOrDigit(c) || c == '_');
        if (!Identifier(helperName) || !moduleName.Split('.').All(Identifier)) return Refuse("Choose valid module and helper names.");
        var target = destination is null ? null : GrammarParser.Parse(DocumentSignatures.Read(destination.Text).MaskedText)
            .File?.Modules.FirstOrDefault(m => m.Name == moduleName);
        if (destination is not null && target is null) return Refuse("The destination file does not contain the selected module.");
        if (destination is null && analysis.FindModule(moduleName) is not null) return Refuse("The module already exists; choose an existing destination.");
        if (target is not null && (target.Rules.Any(r => r.Name == helperName) || target.Helpers.Any(h => h.Name.Name == helperName)))
            return Refuse("The destination already declares that rule or helper name.");
        string newline = source.Text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        string declaration = "syntax function " + helperName + "(" + string.Join(", ", parameters.Select(p =>
            p.Name + ": " + p.Category + (p.IsList ? "*" : ""))) + ") : " + selected.Category + " = " +
            source.Text.Substring(selected.Quote.Start, selected.Quote.Length) + ";";
        var edits = new List<ExtractionTextEdit>();
        bool sameModule = selected.Module.Name == moduleName;
        bool ambiguous = !sameModule && (selected.Module.Helpers.Any(h => h.Name.Name == helperName) ||
            selected.Module.Usings.Where(u => u.Module != moduleName).Any(u => analysis.FindModule(u.Module)?.Helpers.Any(h => h.Name.Name == helperName) == true));
        string call = (ambiguous ? moduleName + "." : "") + helperName + "(" + string.Join(", ", parameters.Select(p => p.Name)) + ")";
        edits.Add(new(source.Uri, selected.Quote, call));
        if (!sameModule && !selected.Module.Usings.Any(u => u.Module == moduleName))
        {
            int cursor = selected.Module.Span.Start;
            GrammarToken token;
            do { token = GrammarLexer.Next(source.Text, ref cursor); } while (token.Kind != TokenKind.End && token.Value != "{");
            if (token.Kind == TokenKind.End) return Refuse("Cannot locate the source module body.");
            edits.Add(new(source.Uri, new(cursor, 0), newline + "  using " + moduleName + ";" + newline));
        }
        string destinationText = "";
        if (destination is null) destinationText = "syntax module " + moduleName + newline + "{" + newline + "  " + declaration + newline + "}" + newline;
        else
        {
            string eol = destination.Text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            edits.Add(new(destination.Uri, new(target!.Span.End - 1, 0), eol + "  " + declaration + eol));
        }
        return new(destinationText, edits, [], []);
    }
}
