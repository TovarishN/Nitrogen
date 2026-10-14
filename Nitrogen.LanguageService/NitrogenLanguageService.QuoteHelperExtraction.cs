using Nitrogen.Grammar;
using Nitrogen.Workspace;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    readonly Dictionary<string, PendingExtraction> _quoteExtractions = new(StringComparer.Ordinal);

    static QuoteExtractionSelection? QuoteExtractionSelect(LanguageDefinition definition, string path, string text,
        DocumentRange range, out GrammarAnalysis analysis)
    {
        var modules = definition.Grammars.SelectMany(g => GrammarParser.Parse(DocumentSignatures.Read(g.Text).MaskedText).File?.Modules ?? default).ToArray();
        analysis = new(modules);
        var source = GrammarParser.Parse(DocumentSignatures.Read(text).MaskedText).File;
        var lines = new LineMap(text);
        return source is null ? null : QuoteHelperExtraction.Select(source.Modules, text,
            GrammarSpan.FromBounds(lines.OffsetOf(range.Start), lines.OffsetOf(range.End)), analysis);
    }

    static IReadOnlyList<QuoteExtractionField> QuoteExtractionParameters(LanguageDefinition definition,
        QuoteExtractionSelection selected, GrammarAnalysis analysis)
    {
        using var syntax = QuoteHelperValidation.CompileSyntax(definition.Grammars.Select(g => new GrammarInput(g.Path, g.Text, definition.GeneratedNamespace)));
        if (!syntax.Succeeded) throw new InvalidOperationException(syntax.Diagnostics.FirstOrDefault()?.Message ?? "The composed syntax must compile.");
        var fields = QuoteHelperExtraction.Fields(selected, analysis);
        var holes = fields.ToDictionary(f => f.Name, f => new QuoteHole(f.Category, f.IsList), StringComparer.Ordinal);
        using var parsed = syntax.Language!.ParseTemplate(selected.Clause.Target!.Name, selected.Category, holes);
        if (!parsed.Success) throw new InvalidOperationException("The complete quote must parse before extraction.");
        var tree = parsed.Tree;
        var names = Enumerable.Range(0, tree.NodeCount).Where(n => tree.Kind(n) is SyntaxKinds.Hole or SyntaxKinds.ListHole)
            .OrderBy(n => tree.Span(n).Start).Select(n => tree.GetText(n).ToString()[(tree.Kind(n) == SyntaxKinds.ListHole ? 3 : 1)..])
            .Distinct(StringComparer.Ordinal).ToArray();
        return names.Select(name => fields.First(f => f.Name == name)).ToArray();
    }

    public QuoteHelperExtractionPreparation PrepareQuoteHelperExtraction(string uri, DocumentRange range)
    {
        QuoteHelperExtractionPreparation Refuse(string message) => new(null, null, null, null, message);
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var source) || !source.IsFile || IsEmbedded(uri))
                return Refuse("Extraction requires a workspace grammar file.");
            string? root = ExtractionRoot(source.LocalPath);
            if (root is null) return Refuse("Open a workspace folder before extracting a quote helper.");
            var inputs = ExtractionInputs(root);
            if (inputs.Files.Count >= WorkspaceInputReader.MaxFiles) return Refuse("The workspace input limit was reached.");
            if (!inputs.Files.TryGetValue(source.LocalPath, out var input)) return Refuse("The grammar is outside the registered workspace inputs.");
            var composition = ExtractionCompositions(inputs).FirstOrDefault(c => c.Definition.Grammars.Any(g => SamePath(g.Path, source.LocalPath)));
            if (composition is null) return Refuse("No valid configured or declared language composes this grammar.");
            var selected = QuoteExtractionSelect(composition.Definition, source.LocalPath, input.Text, range, out var analysis);
            if (selected is null)
            {
                int offset = new LineMap(input.Text).OffsetOf(range.Start);
                var model = GrammarParser.Parse(DocumentSignatures.Read(input.Text).MaskedText).File;
                if (model?.Modules.SelectMany(module => module.Helpers).Any(helper => helper.Match is not null &&
                    helper.Span.Start <= offset && offset < helper.Span.End) == true)
                    return Refuse("Match helpers and subpatterns cannot be extracted; select a complete inline return quote from a grammar rule.");
                return Refuse("Place the caret inside an inline quote, or select the whole quote.");
            }
            _ = QuoteExtractionParameters(composition.Definition, selected, analysis);
            foreach (string expired in _quoteExtractions.Where(e => DateTime.UtcNow - e.Value.Created > TimeSpan.FromMinutes(10)).Select(e => e.Key).ToArray())
                _quoteExtractions.Remove(expired);
            if (_quoteExtractions.Count >= 16) _quoteExtractions.Remove(_quoteExtractions.Keys.First());
            string id = Guid.NewGuid().ToString("N");
            _quoteExtractions[id] = new(root, inputs, uri, range, DateTime.UtcNow);
            string suggested = selected.Owner + "Quote";
            while (selected.Module.Rules.Any(r => r.Name == suggested) || selected.Module.Helpers.Any(h => h.Name.Name == suggested)) suggested += "Helper";
            var destinations = inputs.Files.Values.SelectMany(file =>
                GrammarParser.Parse(DocumentSignatures.Read(file.Text).MaskedText).File?.Modules.Select(module =>
                    new QuoteHelperDestination(new Uri(file.Path).AbsoluteUri, module.Name)) ?? []).ToArray();
            return new(id, suggested, selected.Module.Name, uri, null, destinations);
        }
        catch (Exception error) when (ExtractionFailure(error)) { return Refuse("Cannot prepare quote extraction: " + error.Message); }
    }

    public ExtractionProposal ResolveQuoteHelperExtraction(QuoteHelperExtractionRequest request)
    {
        ExtractionProposal Refuse(string message) => new(request.DestinationUri, "", [], [], message, !request.ExistingDestination);
        try
        {
            if (!_quoteExtractions.TryGetValue(request.Id, out var pending) || DateTime.UtcNow - pending.Created > TimeSpan.FromMinutes(10))
                return Refuse("The quote extraction proposal expired; select the quote again.");
            string sourcePath = new Uri(pending.Uri).LocalPath;
            if (ExtractionRoot(sourcePath) != pending.Root) return Refuse("The workspace changed; prepare extraction again.");
            var current = ExtractionInputs(pending.Root);
            string? Changed(InputSnapshot fresh) => pending.Inputs.Files.Where(f => !fresh.Files.TryGetValue(f.Key, out var input) || input.Text != f.Value.Text)
                .Select(f => f.Key).FirstOrDefault() ?? fresh.Files.Keys.FirstOrDefault(path => !pending.Inputs.Files.ContainsKey(path));
            string Stale(string path) => "Workspace file '" + Path.GetRelativePath(pending.Root, path) + "' changed while extraction was being prepared; select the quote again.";
            if (Changed(current) is { } changed) return Refuse(Stale(changed));
            if (!Uri.TryCreate(request.DestinationUri, UriKind.Absolute, out var destination) || !destination.IsFile)
                return Refuse("Choose a local grammar file inside the workspace.");
            string destinationPath = Path.GetFullPath(destination.LocalPath);
            if (!UnderExtractionRoot(destinationPath, pending.Root) || ExtractionLinkedPath(pending.Root, destinationPath))
                return Refuse("Choose a destination inside the workspace without symbolic links.");
            if (request.ExistingDestination)
            {
                if (!current.Files.ContainsKey(destinationPath)) return Refuse("Choose an existing registered grammar file.");
            }
            else if (ExtractionDestination(pending.Root, destinationPath, current) is { } pathProblem) return Refuse(pathProblem);
            var compositions = ExtractionCompositions(current);
            var affected = compositions.Where(c => c.Definition.Grammars.Any(g => SamePath(g.Path, sourcePath))).ToArray();
            if (affected.Length == 0) return Refuse("The language configuration changed.");
            var allEdits = new List<ExtractionTextEdit>();
            ExtractionTransform? transform = null;
            foreach (var composition in affected)
            {
                var input = current.Files[sourcePath];
                var selected = QuoteExtractionSelect(composition.Definition, sourcePath, input.Text, pending.Range, out var analysis);
                if (selected is null) return Refuse("The selected inline quote changed.");
                var parameters = QuoteExtractionParameters(composition.Definition, selected, analysis);
                var result = QuoteHelperExtraction.Transform(new(pending.Uri, input.Text), selected, parameters,
                    request.ExistingDestination ? new(destination.AbsoluteUri, current.Files[destinationPath].Text) : null,
                    destination.AbsoluteUri, request.Module, request.HelperName, analysis);
                if (result.Problems.Count > 0) return Refuse(result.Problems[0].Message);
                if (transform is not null && (result.DestinationText != transform.DestinationText || !result.Edits.SequenceEqual(transform.Edits)))
                    return Refuse("The quote has incompatible dependencies across its language compositions.");
                transform = result;
                allEdits.AddRange(result.Edits);
                // Existing dependencies are already composed; declaration-based configurations must not duplicate them.
                if (!composition.Definition.Grammars.Any(g => SamePath(g.Path, destinationPath)))
                    allEdits.AddRange(ExtractionConfigurationEdits(composition, result, destinationPath, current));
            }
            foreach (var unrelated in compositions.Except(affected))
                if (ExtractionIncludes(unrelated, destinationPath) || unrelated.Definition.Grammars.Any(g => SamePath(g.Path, destinationPath)))
                    return Refuse("The destination also belongs to unrelated language '" + unrelated.Definition.Name + "'.");
            var edits = allEdits.Distinct().ToArray();
            foreach (var group in edits.GroupBy(e => e.Uri))
            {
                string path = new Uri(group.Key).LocalPath;
                if (!UnderExtractionRoot(path, pending.Root) || ExtractionLinkedPath(pending.Root, path)) return Refuse("Extraction would edit a dependency outside the workspace or through a symbolic link.");
                var ordered = group.OrderBy(e => e.Span.Start).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                    if (ordered[i - 1].Span.End > ordered[i].Span.Start || ordered[i - 1].Span.Start == ordered[i].Span.Start)
                        return Refuse("The proposed changes conflict across language compositions.");
            }
            var files = current.Files.ToDictionary(f => f.Key, f => f.Value);
            foreach (var group in edits.GroupBy(e => new Uri(e.Uri).LocalPath))
            {
                string text = files[group.Key].Text;
                foreach (var edit in group.OrderByDescending(e => e.Span.Start)) text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
                files[group.Key] = files[group.Key] with { Text = text };
            }
            if (!request.ExistingDestination) files[destinationPath] = new(destinationPath, transform!.DestinationText, 0);
            var proposed = ExtractionCompositions(current with { Files = files });
            foreach (var before in affected)
            {
                if (ValidateExtractionComposition(before.Definition) is { } baseline) return Refuse("The original language must compile: " + baseline);
                var after = proposed.FirstOrDefault(c => c.Id == before.Id);
                if (after is null || !after.Definition.Grammars.Any(g => SamePath(g.Path, destinationPath))) return Refuse("The proposed configuration does not compose the helper module.");
                if (!QuoteHelperBindings.Preserved(before.Definition.Grammars.Select(g => new ExtractionSource(new Uri(g.Path).AbsoluteUri, g.Text)),
                    after.Definition.Grammars.Select(g => new ExtractionSource(new Uri(g.Path).AbsoluteUri, g.Text)), edits))
                    return Refuse("Extraction would change an existing helper call's resolution; choose a different helper name or module.");
                if (ValidateExtractionComposition(after.Definition) is { } error) return Refuse("Extraction cannot preserve the language: " + error);
            }
            if (Changed(ExtractionInputs(pending.Root)) is { } finalChanged) return Refuse(Stale(finalChanged));
            if (!request.ExistingDestination && ExtractionDestination(pending.Root, destinationPath, ExtractionInputs(pending.Root)) is { } finalProblem) return Refuse(finalProblem);
            var changes = edits.GroupBy(e => e.Uri).Select(group =>
            {
                string path = new Uri(group.Key).LocalPath;
                string text = current.Files[path].Text;
                var lines = new LineMap(text);
                return new ExtractionDocumentChange(group.Key, ExtractionVersion(path), group.Select(e =>
                    new DocumentEdit(lines.RangeOf(new(e.Span.Start, e.Span.Length)), e.NewText)).ToArray(), text);
            }).ToArray();
            return new(destination.AbsoluteUri, transform!.DestinationText, changes, [], null, !request.ExistingDestination);
        }
        catch (Exception error) when (ExtractionFailure(error)) { return Refuse("Cannot extract this quote: " + error.Message); }
    }
}
