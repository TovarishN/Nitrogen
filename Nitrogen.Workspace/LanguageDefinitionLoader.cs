using Microsoft.CodeAnalysis.CSharp;
using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

public static partial class LanguageDefinitionLoader
{
    public static DefinitionResult Load(string rootPath, InputSnapshot inputs)
    {
        var issues = new List<AnalysisIssue>();
        var grammars = new Dictionary<string, DocumentInput>(DefinitionPaths.Comparer);
        var sources = new Dictionary<string, DocumentInput>(DefinitionPaths.Comparer);
        var active = new HashSet<string>(DefinitionPaths.Comparer);
        var modules = new List<ModuleDecl>();
        var usings = new HashSet<string>(StringComparer.Ordinal);
        LanguageDecl? declaration = null;
        string root;
        try { root = DefinitionPaths.Canonical(rootPath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, [DefinitionPaths.Error(rootPath, error.Message)]);
        }
        Visit(root, root, default);
        if (declaration is null && issues.Count == 0) issues.Add(DefinitionPaths.Error(root, "defining grammar requires a language declaration"));
        if (issues.Any(i => i.IsError) || declaration is null) return new(null, issues);
        string ns = declaration.Namespace ?? declaration.Name + ".Syntax";
        if (ns.Split('.').Any(segment => !SyntaxFacts.IsValidIdentifier(segment)))
            issues.Add(DefinitionPaths.Error(root, $"invalid generated namespace '{ns}'", declaration.Span));
        int dot = declaration.Start.LastIndexOf('.');
        if (!modules.Any(m => m.Name == declaration.Start[..dot] && m.Rules.Any(r => r.Name == declaration.Start[(dot + 1)..])))
            issues.Add(DefinitionPaths.Error(root, $"unknown start rule '{declaration.Start}'", declaration.Settings.First(s => s.Name == "start").Span));
        if (issues.Any(i => i.IsError)) return new(null, issues);
        return new(new LanguageDefinition(root, declaration.Name, declaration.Start, ns,
            grammars.Values.ToArray(), sources.Values.ToArray(), usings.Order(StringComparer.Ordinal).ToArray(),
            declaration.Extensions.ToArray(), declaration.Presentation.ToArray()), issues);

        void Visit(string path, string origin, GrammarSpan span)
        {
            if (active.Contains(path))
            {
                issues.Add(DefinitionPaths.Error(origin, $"grammar dependency cycle includes '{path}'", span));
                return;
            }
            if (grammars.ContainsKey(path)) return;
            if (active.Count >= 128)
            {
                issues.Add(DefinitionPaths.Error(origin, "grammar dependency depth exceeds 128", span));
                return;
            }
            if (DefinitionPaths.Find(inputs, path) is not { } input)
            {
                issues.Add(DefinitionPaths.Error(origin, $"missing registered grammar input '{path}'", span));
                return;
            }
            var header = DocumentSignatures.Read(input.Text);
            foreach (var diagnostic in header.Diagnostics) Add(diagnostic, path);
            if (header.Signatures.Any(s => s.Mode != SignatureMode.Grammar))
                issues.Add(DefinitionPaths.Error(path, "language definition has a non-grammar signature"));
            var parsed = GrammarParser.Parse(header.MaskedText);
            foreach (var diagnostic in parsed.Diagnostics) Add(diagnostic, path);
            if (parsed.File is not { } file) return;
            grammars[path] = input with { Path = path, Text = header.MaskedText };
            modules.AddRange(file.Modules);
            if (DefinitionPaths.Comparer.Equals(path, root)) declaration = file.Languages.FirstOrDefault();
            active.Add(path);
            foreach (var language in file.Languages)
            {
                foreach (string value in language.Usings) usings.Add(value);
                foreach (var setting in language.Settings.Where(s => s.Name is "grammars" or "sources"))
                    foreach (var value in setting.Values)
                        try
                        {
                            string dependency = DefinitionPaths.RelativeTo(value.Name, path);
                            if (setting.Name == "grammars") Visit(dependency, path, value.Span);
                            else if (DefinitionPaths.Find(inputs, dependency) is { } source)
                                sources[dependency] = source with { Path = dependency };
                            else issues.Add(DefinitionPaths.Error(path, $"missing registered helper input '{dependency}'", value.Span));
                        }
                        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                        {
                            issues.Add(DefinitionPaths.Error(path, error.Message, value.Span));
                        }
            }
            active.Remove(path);
        }

        void Add(GrammarDiagnostic diagnostic, string path) => issues.Add(new AnalysisIssue(diagnostic.Code,
            diagnostic.Message, new AnalysisLocation(path, diagnostic.Span.Start, diagnostic.Span.Length), [], "nitrogen",
            diagnostic.Severity == GrammarSeverity.Error));
    }
}
