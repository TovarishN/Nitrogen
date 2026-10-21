using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DiagnosticCaptureMessageTests
{
    const string Grammar = """
        syntax module M {
          token Word = ['a'..'z' '0'..'9']+;
          syntax Doc = Entry*;
          syntax Item = Value:Word { out Size:int=0; Size=Value.Text.Length; }
          syntax Entry = "f" "(" Items:(Item; ",")* ")" {
            match (item:Item) <[ f(..$_, $item, ..$_) ]> => warning CAP1001 : $"{item.Text}:{item.Size}:{Text}";
          }
        }
        """;

    static WorkspaceSnapshot Compile(string grammar)
    {
        var workspace = new GrammarWorkspace { CompositionName = "M", StartRule = "M.Doc" };
        workspace.SetGrammar("capture-messages.ngr", grammar);
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    static IReadOnlyList<SemanticDiagnostic> Diagnostics(WorkspaceSnapshot snapshot, string text)
    {
        using var parsed = snapshot.Parse(text, "M.Doc");
        Assert.True(parsed.Success);
        var project = new Project(snapshot.Language!);
        project.Set("input", parsed.Tree);
        using var semantics = new ProjectSemantics(project);
        return semantics["input"].Diagnostics();
    }

    static string XmlCaptureGrammar(string path)
    {
        string grammar = File.ReadAllText(path);
        int start = grammar.IndexOf("    match", StringComparison.Ordinal);
        int end = grammar.IndexOf("\n  }", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return grammar[..start] + """
                match (pass: Text)
                  <[ <credentials ..$_>..$_<password ..$_>$pass</password>..$_</credentials> ]>
                  => warning XML1001 : $"A password is stored directly inside credentials; use a secret-ref instead. {pass.Text}";
            """ + grammar[end..];
    }

    [Theory]
    [InlineData("<credentials><password>hunter2</password></credentials>", "hunter2")]
    [InlineData("<credentials><user>alice</user><password>secret</password><other /></credentials>", "secret")]
    public void The_requested_xml_capture_is_available_in_the_warning(string input, string expected)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../examples/XmlPatterns/Xml.ngr"));
        string grammar = XmlCaptureGrammar(path);
        var workspace = new GrammarWorkspace { CompositionName = "Xml", StartRule = "Xml.Document" };
        workspace.SetGrammar(path, grammar);
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        using var parsed = snapshot.Parse(input, "Xml.Document");
        Assert.True(parsed.Success);
        var project = new Project(snapshot.Language!);
        project.Set("input.xml", parsed.Tree);
        using var semantics = new ProjectSemantics(project);
        var warning = Assert.Single(semantics["input.xml"].Diagnostics());
        Assert.Equal("XML1001", warning.Code);
        Assert.EndsWith("instead. " + expected, warning.Message);
    }

    [Fact]
    public async Task Configured_XML_capture_messages_reach_the_CLI_and_language_service()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-capture-xml-").FullName;
        try
        {
            string original = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../examples/XmlPatterns/Xml.ngr"));
            string grammar = XmlCaptureGrammar(original);
            string path = Path.Combine(root, "Xml.ngr"), input = Path.Combine(root, "input.xml");
            File.WriteAllText(path, grammar);
            File.WriteAllText(input, "<credentials><password>demo</password></credentials>");
            File.WriteAllText(Path.Combine(root, "nitrogen.json"), """
                { "languages": [{ "name": "Xml", "extensions": [".xml"], "start": "Xml.Document", "grammars": ["Xml.ngr"] }] }
                """);
            var output = new StringWriter();
            Assert.Equal(0, await Nitrogen.Cli.NitrogenCli.RunAsync(["check", input], output, CancellationToken.None));
            Assert.Contains("warning XML1001:", output.ToString());
            Assert.Contains("instead. demo", output.ToString());
            using var service = new Nitrogen.LanguageService.NitrogenLanguageService(Nitrogen.Cli.LspCommand.Registry());
            service.ConfigureWorkspace(root);
            service.Open(new Uri(path).AbsoluteUri, 1, grammar);
            Assert.Empty(service.Diagnostics(new Uri(path).AbsoluteUri));
            service.Open(new Uri(input).AbsoluteUri, 1, File.ReadAllText(input));
            var issue = Assert.Single(service.Diagnostics(new Uri(input).AbsoluteUri));
            Assert.Equal("XML1001", issue.Code);
            Assert.EndsWith("instead. demo", issue.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Scalar_captures_can_be_interpolated_directly()
    {
        using var snapshot = Compile(Grammar.Replace("{item.Text}:{item.Size}:{Text}", "{item}"));
        Assert.Equal("abc", Assert.Single(Diagnostics(snapshot, "f(abc)")).Message);
    }

    [Fact]
    public void Imported_capture_categories_keep_their_typed_properties()
    {
        const string grammar = """
            syntax module A {
              syntax Item = "x" { out Value:int=0; Value=7; }
            }
            syntax module M {
              using A;
              syntax Doc = Entry;
              syntax Entry = "f" "(" Child:Item ")" {
                match (captured:Item) <[ f($captured) ]> => warning CAP1001 : $"{captured.Value}:{captured.Text}";
              }
            }
            """;
        using var snapshot = Compile(grammar);
        Assert.Equal("7:x", Assert.Single(Diagnostics(snapshot, "f(x)")).Message);
    }

    [Fact]
    public void Capture_names_that_are_CSharp_keywords_can_be_escaped_in_messages()
    {
        string grammar = Grammar.Replace("item", "event").Replace("{event.", "{@event.");
        using var snapshot = Compile(grammar);
        Assert.Equal("abc:3:f(abc)", Assert.Single(Diagnostics(snapshot, "f(abc)")).Message);
    }

    [Fact]
    public void A_capture_is_not_visible_in_a_later_ordinary_check()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("capture-messages.ngr", Grammar.Replace("{item.Text}:{item.Size}:{Text}\";",
            "{item.Text}:{item.Size}:{Text}\"; check OTHER false : $\"{item.Text}\";"));
        Assert.Contains(workspace.Compile().Diagnostics, d => d.Code == "CS0103" && d.Path == "capture-messages.ngr");
    }

    [Fact]
    public void Scalar_captures_are_typed_views_in_the_original_semantic_context()
    {
        using var snapshot = Compile(Grammar);
        var issue = Assert.Single(Diagnostics(snapshot, "f(abc, def)"));
        Assert.Equal("CAP1001", issue.Code);
        Assert.Equal("abc:3:f(abc, def)", issue.Message);
        Assert.Equal(DiagnosticSeverity.Warning, issue.Severity);
        Assert.Equal(new TextSpan(0, 11), issue.Span);
    }

    [Theory]
    [InlineData("f()", "0:")]
    [InlineData("f(abc)", "1:abc")]
    [InlineData("f(a, bb, ccc)", "3:a|bb|ccc")]
    public void List_captures_support_count_and_enumeration(string input, string expected)
    {
        string grammar = Grammar.Replace("(item:Item) <[ f(..$_, $item, ..$_) ]>", "(items:Item*) <[ f(..$items) ]>")
            .Replace("{item.Text}:{item.Size}:{Text}", "{items.Count}:{string.Join(\"|\", global::System.Linq.Enumerable.Select(items, x => x.Text))}");
        using var snapshot = Compile(grammar);
        Assert.Equal(expected, Assert.Single(Diagnostics(snapshot, input)).Message);
    }

    [Fact]
    public void Repeated_captures_use_the_successful_backtracked_binding()
    {
        using var snapshot = Compile(Grammar.Replace("$item, ..$_)", "$item, ..$_, $item, ..$_)"));
        Assert.Equal("bb:2:f(a, bb, bb)", Assert.Single(Diagnostics(snapshot, "f(a, bb, bb)")).Message);
        Assert.Empty(Diagnostics(snapshot, "f(a, bb)"));
    }

    [Fact]
    public void A_mismatch_does_not_evaluate_the_message()
    {
        using var snapshot = Compile(Grammar.Replace("$\"{item.Text}:{item.Size}:{Text}\"", "throw new global::System.InvalidOperationException(\"message executed\")"));
        Assert.Empty(Diagnostics(snapshot, "f()"));
    }

    [Fact]
    public void Captures_are_local_to_each_message()
    {
        string grammar = Grammar.Replace("$\"{item.Text}:{item.Size}:{Text}\";", """
            $"first:{item.Text}";
            match (item:Item) <[ f(..$_, $item) ]> => warning CAP1002 : $"last:{item.Text}";
            """);
        using var snapshot = Compile(grammar);
        Assert.Equal(new[] { "first:a", "last:bb" }, Diagnostics(snapshot, "f(a, bb)").Select(i => i.Message));
    }

    [Fact]
    public void Captures_cannot_shadow_containing_view_members()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("capture-messages.ngr", Grammar.Replace("item", "Items"));
        Assert.Contains(workspace.ValidateCompilation(), d => d.Code == "NGR0149");
    }
}
