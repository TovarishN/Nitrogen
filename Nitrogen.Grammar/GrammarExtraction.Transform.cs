using System.Text;

namespace Nitrogen.Grammar;

public static partial class GrammarExtraction
{
    public static ExtractionTransform Transform(IReadOnlyList<ExtractionSource> sources,
        ExtractionSelection selection, string destinationUri, string destinationModule)
    {
        var problems = new List<ExtractionProblem>();
        var files = sources.Select(s => (Source: s, Parsed: GrammarParser.Parse(DocumentSignatures.Read(s.Text).MaskedText))).ToArray();
        void Problem(string message, string? uri = null, GrammarSpan? span = null) =>
            problems.Add(new("NX0002", message, uri ?? selection.Uri, span ?? selection.Removal));
        ExtractionTransform Failed() => new("", Array.Empty<ExtractionTextEdit>(), Array.Empty<ExtractionSymbolMove>(), problems);
        var source = sources.FirstOrDefault(s => s.Uri == selection.Uri);
        if (source is null || files.Any(f => f.Parsed.File is null))
        {
            Problem("All participating grammars must parse before extraction.");
            return Failed();
        }
        var nameCheck = GrammarParser.Parse("syntax module " + destinationModule + " { }");
        if (nameCheck.File is null || nameCheck.File.Modules.Count != 1 || nameCheck.File.Modules[0].Name != destinationModule ||
            files.Any(f => f.Parsed.File!.Modules.Any(m => m.Name == destinationModule)) || sources.Any(s => s.Uri == destinationUri))
        {
            Problem("Choose a unique module name and a new destination file.");
            return Failed();
        }
        var checkedSelection = Select(source, selection.Removal).Selection;
        if (checkedSelection is null || checkedSelection.Module != selection.Module || checkedSelection.Shape != selection.Shape ||
            !checkedSelection.Definitions.SequenceEqual(selection.Definitions))
        {
            Problem("The selected definitions have changed; select complete definitions again.");
            return Failed();
        }
        var modules = files.SelectMany(f => f.Parsed.File!.Modules).ToArray();
        var analysis = new GrammarAnalysis(modules);
        var owner = modules.First(m => m.Name == selection.Module);
        string? extensionTarget = selection.ExtensionTarget;
        if (selection.Shape == ExtractionShape.Alternatives)
        {
            var extension = owner.Extends.FirstOrDefault(e => e.Alternatives.Any(a => selection.Definitions.Contains(a.Span)));
            if (extension is not null)
                extensionTarget = analysis.Resolve(extension.Target, extension.Span, owner, null)?.Key;
            if (extensionTarget is null)
            {
                Problem("The selected extension target could not be resolved.");
                return Failed();
            }
        }
        var moves = selection.Shape == ExtractionShape.Rules
            ? owner.Rules.Where(r => selection.Definitions.Contains(r.Span))
                .Select(r => new ExtractionSymbolMove(owner.Name + "." + r.Name, destinationModule + "." + r.Name)).ToArray()
            : owner.Rules.OfType<ExtensibleRule>().SelectMany(r => r.Alternatives).Concat(owner.Extends.SelectMany(e => e.Alternatives))
                .Where(a => selection.Definitions.Contains(a.Span) && a.Name.Length > 0)
                .Select(a => new ExtractionSymbolMove(owner.Name + "." + a.Name, destinationModule + "." + a.Name)).ToArray();
        string Mapped(string key) => moves.FirstOrDefault(m => m.Before == key)?.After ?? key;
        bool IsMoved(string uri, GrammarSpan span) => uri == selection.Uri && selection.Removal.Start <= span.Start && span.End <= selection.Removal.End;
        var replacements = new List<ExtractionTextEdit>();
        void Replace(string uri, GrammarSpan span, string text)
        {
            var file = sources.First(s => s.Uri == uri);
            if (file.Text.Substring(span.Start, span.Length) != text)
                replacements.Add(new(uri, span, text));
        }
        var names = files.SelectMany(file => file.Parsed.File!.Modules.SelectMany(module =>
            ExtractionNames(analysis, module, file.Source.Text).Select(name =>
                (file.Source, Module: module, Name: name, Target: analysis.Resolve(name.Name, name.Span, module, PointModule(name.ImplicitPoint)))))).ToArray();
        foreach (var name in names.Where(n => n.Target is null))
            Problem("A rule dependency could not be resolved: " + name.Name.Name, name.Source.Uri, name.Name.Span);
        if (problems.Count > 0) return Failed();

        foreach(var file in files)
            foreach(var module in file.Parsed.File!.Modules)
                foreach(var context in analysis.ContextOccurrences(module))
                    if(IsMoved(file.Source.Uri,context.Name.Span))
                        Replace(file.Source.Uri,context.Name.Span,context.Symbol.Module.Name+"."+context.Symbol.Name.Name);
        var consumers = new HashSet<string>(names.Where(n => !IsMoved(n.Source.Uri, n.Name.Span) && Mapped(n.Target!.Key) != n.Target.Key)
            .Select(n => n.Module.Name), StringComparer.Ordinal);
        var destinationDeclaration = new ModuleDecl(destinationModule,
            owner.Usings.Select(u => u.Module).Append(owner.Name).Distinct().Select(name => new UsingDecl(name, default)).ToArray(),
            selection.Shape == ExtractionShape.Rules ? owner.Rules.Where(r => selection.Definitions.Contains(r.Span)).ToArray() : Array.Empty<RuleDecl>(),
            Array.Empty<ExtendDecl>(), default);
        // A lookup-only view of final ownership/imports. The emitted text is still compiled below;
        // no rewritten rule bodies or speculative parse results are used for validation here.
        var lookupModules = modules.Select(module => module with
        {
            Rules = module == owner && selection.Shape == ExtractionShape.Rules
                ? module.Rules.Where(r => !selection.Definitions.Contains(r.Span)).ToArray() : module.Rules,
            Usings = consumers.Contains(module.Name)
                ? module.Usings.Append(new UsingDecl(destinationModule, default)).ToArray() : module.Usings,
        }).Append(destinationDeclaration).ToArray();
        var lookup = new GrammarAnalysis(lookupModules);
        string NameFor(GrammarSymbol target, ModuleDecl module, GrammarSpan span, string? point = null, string? written = null) =>
            (written == target.Rule.Name || GrammarParser.CanUseUnqualifiedReference(target.Rule.Name)) &&
            lookup.Resolve(target.Rule.Name, span, module, PointModule(point is null ? null : Mapped(point)))?.Key == Mapped(target.Key)
                ? target.Rule.Name : Mapped(target.Key);
        foreach (var name in names)
        {
            bool moved = IsMoved(name.Source.Uri, name.Name.Span);
            // Explicit unrelated qualification remains intentional; newly imported names must
            // also preserve every existing unqualified referent, even if that now needs a prefix.
            if (!moved && name.Name.Name.Contains('.') && Mapped(name.Target!.Key) == name.Target.Key) continue;
            var context = moved ? destinationDeclaration : lookupModules.First(m => m.Name == name.Module.Name);
            Replace(name.Source.Uri, name.Name.Span, NameFor(name.Target!, context, name.Name.Span, name.Name.ImplicitPoint, name.Name.Name));
        }
        if (extensionTarget is not null)
            extensionTarget = NameFor(analysis.Resolve(extensionTarget, selection.Removal, owner, null)!, destinationDeclaration, selection.Removal);
        var importEdits = files.SelectMany(file => file.Parsed.File!.Modules.Where(m => consumers.Contains(m.Name)).Select(module =>
        {
            int at = module.Usings.Count > 0 ? module.Usings.Last().Span.End : ExtractionOpeningBrace(file.Source.Text, module.Span.Start).End;
            string lineEnd = file.Source.Text.Contains("\r\n") ? "\r\n" : "\n";
            return new ExtractionTextEdit(file.Source.Uri, new(at, 0), lineEnd + "  using " + destinationModule + ";" + lineEnd);
        })).ToArray();
        if (problems.Count > 0) return Failed();
        string movedText = source.Text.Substring(selection.Removal.Start, selection.Removal.Length);
        foreach (var replacement in replacements.Where(e => IsMoved(e.Uri, e.Span)).OrderByDescending(e => e.Span.Start))
            movedText = movedText.Remove(replacement.Span.Start - selection.Removal.Start, replacement.Span.Length)
                .Insert(replacement.Span.Start - selection.Removal.Start, replacement.NewText);
        string newline = source.Text.Contains("\r\n") ? "\r\n" : "\n";
        var builder = new StringBuilder("syntax module " + destinationModule + newline + "{" + newline);
        foreach (string imported in owner.Usings.Select(u => u.Module).Append(owner.Name).Distinct())
            builder.Append("  using ").Append(imported).Append(';').Append(newline);
        builder.Append(newline);
        if (selection.Shape == ExtractionShape.Alternatives)
            builder.Append("  extend syntax ").Append(extensionTarget).Append(newline).Append("  {").Append(newline);
        builder.Append(movedText);
        if (!movedText.EndsWith(newline, StringComparison.Ordinal)) builder.Append(newline);
        if (selection.Shape == ExtractionShape.Alternatives) builder.Append("  }").Append(newline);
        builder.Append('}').Append(newline);
        string destination = builder.ToString();
        var edits = replacements.Where(e => !IsMoved(e.Uri, e.Span)).ToList();
        var leadingImport = importEdits.FirstOrDefault(e => e.Uri == selection.Uri && e.Span.Start == selection.Removal.Start);
        edits.Add(new(selection.Uri, selection.Removal, leadingImport?.NewText ?? ""));
        edits.AddRange(importEdits.Where(e => e != leadingImport));
        var afterSources = sources.Select(s => new ExtractionSource(s.Uri, Apply(s.Text, edits.Where(e => e.Uri == s.Uri))))
            .Append(new ExtractionSource(destinationUri, destination)).OrderBy(s => s.Uri, StringComparer.Ordinal).ToArray();
        var compiled = GrammarCompiler.Compile(afterSources.Select(s => new GrammarInput(s.Uri, DocumentSignatures.Read(s.Text).MaskedText, "Extraction")));
        foreach (var diagnostic in compiled.Diagnostics.Where(d => d.Diagnostic.Severity == GrammarSeverity.Error))
            Problem(diagnostic.Diagnostic.Message, diagnostic.Path, diagnostic.Diagnostic.Span);
        if (problems.Count > 0) return Failed();
        var afterAnalysis = new GrammarAnalysis(afterSources.SelectMany(s => GrammarParser.Parse(DocumentSignatures.Read(s.Text).MaskedText).File!.Modules));
        foreach (var module in analysis.PrimaryModules)
            foreach (var rule in analysis.PrimaryRules(module).OfType<SyntaxRule>())
                if (rule.Clauses.Any(c => c.Kind == BindingClauseKind.Rewrites))
                {
                    var movedRule = afterAnalysis.Resolve(Mapped(module.Name + "." + rule.Name), rule.Span, module, null)!;
                    string beforeCategory = Mapped(QuotationWriter.Category(analysis, module, rule, null).Key);
                    string afterCategory = QuotationWriter.Category(afterAnalysis, movedRule.Module, movedRule.Rule, null).Key;
                    if (beforeCategory != afterCategory)
                        Problem("Extraction would change the quotation category for " + module.Name + "." + rule.Name +
                            " from " + beforeCategory + " to " + afterCategory + ". Retain this rewrite in its original category.");
                }
        foreach (string point in analysis.PointKeys)
        {
            var before = analysis.Point(point).Select(Identity).ToArray();
            var after = afterAnalysis.Point(Mapped(point)).Select(p => p.Module.Name == destinationModule
                ? owner.Name + "." + p.Alternative.Name : Identity(p)).ToArray();
            if (!before.SequenceEqual(after)) Problem("Extraction would change composed alternative order for " + point + ".");
        }
        return problems.Count > 0 ? Failed() : new(destination, edits, moves, problems);
    }

    static string Identity(PointAlternative alternative) => alternative.Module.Name + "." + alternative.Alternative.Name;

    static string Apply(string text, IEnumerable<ExtractionTextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Span.Start))
            text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        return text;
    }

    internal static IEnumerable<Expr> Children(Expr expression) => expression switch
    {
        SequenceExpr sequence => sequence.Items,
        ChoiceExpr choice => choice.Alternatives,
        ContextScopeExpr context => new[] { context.Inner },
        LabeledExpr labeled => new[] { labeled.Inner },
        RepeatExpr repeat => new[] { repeat.Inner },
        SeparatedListExpr list => new[] { list.Item, list.Separator },
        PredicateExpr predicate => new[] { predicate.Inner },
        IfExpr conditional => new[] { conditional.Then, conditional.Else },
        ComprehensionExpr comprehension => new[] { comprehension.Block.Body },
        _ => Array.Empty<Expr>(),
    };
}
