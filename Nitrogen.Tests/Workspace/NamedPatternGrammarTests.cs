using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class NamedPatternGrammarTests
{
    internal const string Xml = """
        syntax module X {
          token Name = ['a'..'z']+;
          token Content = [^ '<']+;
          syntax Doc = Root:Element;
          syntax Element = Empty / Paired;
          syntax Empty = "<" Tag:Name "/>";
          syntax Paired = "<" Open:Name ">" Children:Node* "</" Close:Name ">" {
            MATCH
          }
          syntax Node = Element / Text;
          syntax Text = Content;
          pattern Secret : Element =
            | <[ <password>..$_</password> ]>
            | <[ <secret/> ]>;
          EXTRA
        }
        """;

    internal static string Grammar(string match, string extra = "") => Xml.Replace("MATCH", match).Replace("EXTRA", extra);

    internal static WorkspaceSnapshot Compile(string grammar, string composition = "X", string start = "X.Doc")
    {
        var workspace = new GrammarWorkspace { CompositionName = composition, StartRule = start };
        workspace.SetGrammar("patterns.ngr", grammar);
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    internal static IReadOnlyList<WorkspaceDiagnostic> Validate(string grammar)
    {
        var workspace = new GrammarWorkspace { CompositionName = "X", StartRule = "X.Doc" };
        workspace.SetGrammar("patterns.ngr", grammar);
        return workspace.ValidateCompilation();
    }

    internal static (string Code, int Start, int Length, string Message)[] Diagnostics(WorkspaceSnapshot snapshot, string input, string start = "X.Doc")
    {
        using var parsed = snapshot.Parse(input, start);
        Assert.True(parsed.Success, input);
        var project = new Project(snapshot.Language!);
        project.Set("input.xml", parsed.Tree);
        using var semantics = new ProjectSemantics(project);
        return semantics["input.xml"].Diagnostics().Select(d => (d.Code, d.Span.Start, d.Span.Length, d.Message)).ToArray();
    }

    [Fact]
    public void Diagnostic_using_named_pattern_reports()
    {
        using var snapshot = Compile(Grammar("""match (s: Secret) <[ <credentials>..$_ $s ..$_</credentials> ]> => warning XP1001 : $"secret {s}";"""));
        var warning = Assert.Single(Diagnostics(snapshot, "<credentials><user>sa</user><secret/></credentials>"));
        Assert.Equal("XP1001", warning.Code);
        Assert.Equal("secret <secret/>", warning.Message);
        Assert.Empty(Diagnostics(snapshot, "<credentials><user>sa</user></credentials>"));
    }

    [Fact]
    public void Descendant_capture_reports_on_outer_node()
    {
        using var snapshot = Compile(Grammar("""match (s: ..Secret) <[ <credentials>..$_ $s ..$_</credentials> ]> => warning XP1002 : $"contains {s}";"""));
        const string input = "<credentials><group><password>a</password></group><user>b</user></credentials>";
        var warning = Assert.Single(Diagnostics(snapshot, input));
        Assert.Equal((0, input.Length), (warning.Start, warning.Length));
        Assert.Equal("contains <password>a</password>", warning.Message);
    }

    [Fact]
    public void Syntax_function_arm_uses_named_pattern()
    {
        using var snapshot = Compile(Grammar("", """
            syntax function Redact(e: Element) : Element = match e {
              | (s: Secret) <[ $s ]> => <[ <redacted/> ]>;
              | _ => <[ $e ]>;
            };
            """));
        var helper = snapshot.Language!.Modules.SelectMany(m => m.QuoteHelpers).Single(h => h.Name == "X.Redact");
        Assert.Equal("X.Secret", helper.Match!.Arms[0].Captures["s"].PatternName);
        Assert.Empty(snapshot.Language.ValidateQuoteHelpers());
    }

    [Fact]
    public void Generated_module_lists_named_patterns()
    {
        using var snapshot = Compile(Grammar(""));
        var spec = Assert.Single(snapshot.Language!.Modules.SelectMany(m => m.NamedPatterns));
        Assert.Equal(("X.Secret", "X.Element", 2), (spec.Name, spec.Category, spec.Alternatives.Count));
        Assert.Equal("patterns.ngr", spec.Alternatives[0].Source.Path);
        Assert.NotNull(snapshot.Language.NamedPattern("X.Secret"));
    }

    [Fact]
    public void Unknown_pattern_reports_NGR0163()
    {
        var diagnostics = Validate(Grammar("""match (s: ..Missing) <[ <a>..$_ $s ..$_</a> ]> => warning XP1001 : "m";"""));
        Assert.Contains(diagnostics, d => d.IsError && d.Code == "NGR0163");
    }

    [Fact]
    public void Duplicate_pattern_or_rule_name_reports_NGR0164()
    {
        var diagnostics = Validate(Grammar("", "pattern Text : Element = <[ <x/> ]>;"));
        Assert.Contains(diagnostics, d => d.IsError && d.Code == "NGR0164");
    }

    [Theory]
    [InlineData("""match (s: ..Secret*) <[ <a>..$s</a> ]> => warning XP1001 : "m";""")]
    [InlineData("""match (s: ..Secret) <[ <a>$s $s</a> ]> => warning XP1001 : "m";""")]
    public void Descendant_list_or_repeated_descendant_reports_NGR0166(string match)
    {
        Assert.Contains(Validate(Grammar(match)), d => d.IsError && d.Code == "NGR0166");
    }

    [Fact]
    public void Extending_module_uses_pattern_by_simple_and_qualified_name()
    {
        string grammar = Grammar("") + """

            syntax module Y {
              using X;
              syntax Wrapper = "[" Inner:Element "]" {
                match (s: Secret) <[ [ $s ] ]> => warning XP1003 : "simple";
                match (t: X.Secret) <[ [ $t ] ]> => warning XP1004 : "qualified";
              }
            }
            """;
        using var snapshot = Compile(grammar, "Y", "Y.Wrapper");
        Assert.Equal(["XP1003", "XP1004"], Diagnostics(snapshot, "[<secret/>]", "Y.Wrapper").Select(d => d.Code).Order().ToArray());
    }

    [Fact]
    public void Imported_pattern_does_not_silently_replace_a_local_category()
    {
        string grammar = Grammar("", "pattern Wrapped : Element = <[ <w/> ]>;") + """

            syntax module Y {
              using X;
              syntax Wrapped = "[" Inner:Element "]";
              syntax Holder = "{" Item:Wrapped "}" {
                match (w: Wrapped) <[ { $w } ]> => warning XP1005 : "m";
              }
            }
            """;
        var workspace = new GrammarWorkspace { CompositionName = "Y", StartRule = "Y.Holder" };
        workspace.SetGrammar("patterns.ngr", grammar);
        Assert.Contains(workspace.ValidateCompilation(), d => d.IsError && d.Code == Nitrogen.Grammar.GrammarCodes.AmbiguousReference);
    }

    [Fact]
    public void Ambiguous_imported_pattern_is_reported()
    {
        string grammar = Grammar("", "") + """

            syntax module Z {
              using X;
              pattern Secret : X.Element = <[ <z/> ]>;
            }
            syntax module Y {
              using X;
              using Z;
              syntax Holder = "{" Item:X.Element "}" {
                match (s: ..Secret) <[ { $s } ]> => warning XP1006 : "m";
              }
            }
            """;
        var workspace = new GrammarWorkspace { CompositionName = "Y", StartRule = "Y.Holder" };
        workspace.SetGrammar("patterns.ngr", grammar);
        Assert.Contains(workspace.ValidateCompilation(), d => d.IsError && d.Code == Nitrogen.Grammar.GrammarCodes.AmbiguousReference);
    }

    [Fact]
    public void Non_consuming_cycle_fails_composition_with_NQP0008()
    {
        var diagnostics = Validate(Grammar("", """
            pattern A : Element = (x: B) <[ $x ]>;
            pattern B : Element = (y: A) <[ $y ]>;
            """));
        Assert.Contains(diagnostics, d => d.IsError && d.Code == "NQP0008" && d.Path == "patterns.ngr");
    }
}
