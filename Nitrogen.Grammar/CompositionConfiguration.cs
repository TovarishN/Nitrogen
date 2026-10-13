using System.IO;
using System.Text.RegularExpressions;

namespace Nitrogen.Grammar;

public sealed record CompositionConfigurationResult(EquatableArray<LanguageComposition> Compositions,
    EquatableArray<CompiledDiagnostic> Diagnostics);

/// <summary>Resolves composition membership from registered text only, never from disk discovery.</summary>
public static class CompositionConfiguration
{
    const string DefaultNamespace = "Nitrogen.Workspace.Grammar";
    static StringComparer Paths => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static CompositionConfigurationResult Resolve(IEnumerable<GrammarInput> registered)
    {
        var files = registered.GroupBy(f => Path.GetFullPath(f.Path), Paths).ToDictionary(g => g.Key, g => g.First(), Paths);
        var compositions = new List<LanguageComposition>();
        var diagnostics = new List<CompiledDiagnostic>();
        var represented = new HashSet<string>(Paths);
        foreach (var config in files.Values.Where(f => Path.GetFileName(f.Path) == "nitrogen.json").OrderBy(f => f.Path, Paths))
        {
            try
            {
                var json = ConfigurationJson.Read(config.Text);
                if (!json.TryGetValue("languages", out var entries)) continue;
                foreach (var item in Array(entries))
                {
                    var entry = item as Dictionary<string, object?> ?? throw new FormatException("expected language object");
                    string name = String(entry, "name"), start = String(entry, "start");
                    string? configuredNamespace = entry.ContainsKey("namespace") ? String(entry, "namespace") : null;
                    var roots = Match(Strings(entry, "grammars"), config.Path);
                    var definitions = roots.Select(f => (Input: f, Declaration: Declaration(f))).Where(d => d.Declaration is not null).ToArray();
                    if (definitions.Length > 1) throw new FormatException("multiple defining language declarations in one configured language");
                    var declared = definitions.FirstOrDefault();
                    string ns = declared.Declaration?.Namespace ?? (declared.Declaration is not null ? declared.Declaration.Name + ".Syntax" : configuredNamespace ?? DefaultNamespace);
                    if (declared.Declaration is { } language)
                    {
                        if (language.Name != name || language.Start != start || configuredNamespace is not null && configuredNamespace != ns)
                            throw new FormatException("configured language metadata conflicts with its grammar declaration");
                        foreach (var setting in language.Settings)
                        {
                            if (!entry.ContainsKey(setting.Name)) continue;
                            if (setting.Name is "grammars" or "sources")
                            {
                                var actual = new HashSet<string>(Strings(entry, setting.Name).Select(p => Relative(p, config.Path)), Paths);
                                var expected = new HashSet<string>(setting.Values.Select(v => Relative(v.Name, declared.Input!.Path)), Paths);
                                if (setting.Name == "grammars") expected.Add(Path.GetFullPath(declared.Input!.Path));
                                if (!actual.SetEquals(expected)) throw new FormatException("configured '" + setting.Name + "' conflicts with its grammar declaration");
                            }
                            else if (setting.Name is "usings" or "extensions")
                                if (!Strings(entry, setting.Name).OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(setting.Values.Select(v => v.Name).OrderBy(v => v, StringComparer.Ordinal)))
                                    throw new FormatException("configured '" + setting.Name + "' conflicts with its grammar declaration");
                        }
                        represented.Add(Path.GetFullPath(declared.Input!.Path));
                    }
                    Add(name, start, ns, Closure(roots, ns), config.Path);
                }
            }
            catch (Exception error) when (ConfigurationError(error)) { Error(config.Path, error.Message); }
        }
        foreach (var file in files.Values.Where(IsGrammar).OrderBy(f => f.Path, Paths))
        {
            if (represented.Contains(Path.GetFullPath(file.Path))) continue;
            try
            {
                if (Declaration(file) is not { } declaration) continue;
                Add(declaration.Name, declaration.Start, declaration.Namespace ?? declaration.Name + ".Syntax",
                    Closure(new[] { file }, declaration.Namespace ?? declaration.Name + ".Syntax"), file.Path);
            }
            catch (Exception error) when (ConfigurationError(error)) { Error(file.Path, error.Message); }
        }
        foreach (var duplicate in compositions.GroupBy(c => c.Namespace + "." + c.Name + "Composition").Where(g => g.Count() > 1))
            Error(duplicate.First().Grammars.FirstOrDefault()?.Path ?? "", "multiple definitions generate composition factory '" + duplicate.Key + "'");
        foreach (var group in compositions.SelectMany(c => c.Grammars).GroupBy(g => Path.GetFullPath(g.Path), Paths))
            if (group.Select(g => g.Namespace).Distinct().Count() > 1)
                Error(group.First().Path, "shared grammar is selected with conflicting namespaces");
        return new(compositions.ToArray(), diagnostics.ToArray());

        void Add(string name, string start, string ns, GrammarInput[] grammars, string origin)
        {
            if (grammars.Length == 0) throw new FormatException("no registered grammars match the configured language");
            var composition = new LanguageComposition(name, start, ns, grammars);
            var check = CompositionWriter.Write(composition);
            if (check.HasErrors)
                diagnostics.AddRange(check.Diagnostics.Select(d => d with { Path = origin }));
            else compositions.Add(composition);
        }
        GrammarInput[] Match(string[] patterns, string origin)
        {
            var matched = new List<GrammarInput>();
            foreach (string pattern in patterns)
            {
                string absolute = Relative(pattern, origin).Replace('\\', '/');
                var found = files.Values.Where(f => Matches(Path.GetFullPath(f.Path).Replace('\\', '/'), absolute)).ToArray();
                if (found.Length == 0) throw new FormatException("no registered inputs match '" + pattern + "'");
                matched.AddRange(found);
            }
            return matched.GroupBy(f => Path.GetFullPath(f.Path), Paths).Select(g => g.First()).ToArray();
        }
        GrammarInput[] Closure(IEnumerable<GrammarInput> roots, string ns)
        {
            var selected = new Dictionary<string, GrammarInput>(Paths);
            var active = new HashSet<string>(Paths);
            foreach (var root in roots) Visit(root);
            return selected.Values.ToArray();
            void Visit(GrammarInput file)
            {
                string path = Path.GetFullPath(file.Path);
                if (active.Contains(path)) throw new FormatException("cyclic grammar dependency");
                if (selected.ContainsKey(path)) return;
                if (active.Count >= 128) throw new FormatException("grammar dependency depth exceeds 128");
                var header = DocumentSignatures.Read(file.Text);
                var parsed = GrammarParser.Parse(header.MaskedText);
                if (header.Diagnostics.Any(d => d.Severity == GrammarSeverity.Error) || parsed.File is null)
                    throw new FormatException("invalid registered grammar '" + file.Path + "'");
                string fileNamespace = parsed.File.Languages.FirstOrDefault()?.Namespace ?? ns;
                selected[path] = file with { Text = header.MaskedText, Namespace = fileNamespace };
                active.Add(path);
                foreach (string dependency in parsed.File.Languages.SelectMany(l => l.Grammars))
                {
                    string target = Relative(dependency, file.Path);
                    if (!files.TryGetValue(target, out var input)) throw new FormatException("grammar dependency '" + dependency + "' is not a registered AdditionalFile");
                    Visit(input);
                }
                active.Remove(path);
            }
        }
        void Error(string path, string message) => diagnostics.Add(new(path, new("NGR0406", GrammarSeverity.Error, message, default)));
    }

    internal static bool Matches(string path, string pattern)
    {
        string expression = "\\A" + Regex.Escape(pattern).Replace("\\*\\*/", "(?:.*/)?").Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "\\z";
        return Regex.IsMatch(path, expression, RegexOptions.CultureInvariant | (Path.DirectorySeparatorChar == '\\' ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
    }
    static string Relative(string path, string origin)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new FormatException("grammar input must be a local registered path");
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(origin))!, path));
    }
    static bool IsGrammar(GrammarInput input) => input.Path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase) ||
        DocumentSignatures.Read(input.Text).Signatures.Any(s => s.Mode == SignatureMode.Grammar);
    static LanguageDecl? Declaration(GrammarInput input) => GrammarParser.Parse(DocumentSignatures.Read(input.Text).MaskedText).File?.Languages.FirstOrDefault();
    static List<object?> Array(object? value) => value as List<object?> ?? throw new FormatException("expected JSON array");
    static string String(Dictionary<string, object?> value, string key) => value.TryGetValue(key, out var item) && item is string text && !string.IsNullOrWhiteSpace(text)
        ? text : throw new FormatException("expected non-empty language '" + key + "'");
    static string[] Strings(Dictionary<string, object?> value, string key) => !value.TryGetValue(key, out var item) ? new string[0] :
        Array(item).Select(v => v is string text ? text : throw new FormatException("expected string in '" + key + "'")).ToArray();
    static bool ConfigurationError(Exception error) => error is FormatException or ArgumentException or NotSupportedException or PathTooLongException;
}
