using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Binding;
using Nitrogen.Semantics;
using XmlPatterns.Syntax;
using Xunit;

namespace Nitrogen.Tests;

public sealed class XmlPatternExampleTests
{
    static readonly Language Language = XmlPatternsComposition.CreateBuilder().Build();

    static IReadOnlyList<SemanticDiagnostic> Analyze(string source)
    {
        using var parsed = Language.Parse(source, XmlPatternsComposition.Start);
        if (!parsed.Success)
        {
            var errors = new List<SemanticDiagnostic>();
            foreach (var diagnostic in parsed.Diagnostics)
                errors.Add(new("XML0000", diagnostic.Span, parsed.FormatMessage(diagnostic)));
            return errors;
        }
        var project = new Project(Language);
        project.Set("input.xml", parsed.Tree);
        return new ProjectSemantics(project)["input.xml"].Diagnostics();
    }
    static string Example(string file) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../examples/XmlPatterns", file));

    [Fact]
    public void Sample_reports_one_warning_at_the_credentials_element()
    {
        string source = File.ReadAllText(Example("sample.xml"));
        var issue = Assert.Single(Analyze(source));
        Assert.Equal("XML1001", issue.Code);
        Assert.EndsWith("example-only-password", issue.Message);
        Assert.Equal(DiagnosticSeverity.Warning, issue.Severity);
        Assert.Equal(source.IndexOf("<credentials source", StringComparison.Ordinal), issue.Span.Start);
        Assert.StartsWith("<credentials", source.Substring(issue.Span.Start, issue.Span.Length));
    }

    [Theory]
    [InlineData("<credentials><password>demo</password></credentials>", 1)]
    [InlineData("<root><credentials mode='local'><password a='1' b=\"2\">some <b>text</b></password></credentials></root>", 0)]
    [InlineData("<root><credentials><password></password></credentials><credentials><password>x</password></credentials></root>", 1)]
    [InlineData("<credentials><secret-ref key='password' /></credentials>", 0)]
    [InlineData("<root><password>demo</password></root>", 0)]
    [InlineData("<Credentials><password>demo</password></Credentials>", 0)]
    [InlineData("<credentials><wrapper><password>demo</password></wrapper></credentials>", 0)]
    [InlineData("<credentials><user>alice</user><password>demo</password></credentials>", 1)]
    [InlineData("<credentials><password>demo</password><user>alice</user></credentials>", 1)]
    [InlineData("<credentials><user>alice</user><password>demo</password><other /></credentials>", 1)]
    [InlineData("<credentials><password /></credentials>", 0)]
    [InlineData("<root><!-- <credentials><password>x</password></credentials> --></root>", 0)]
    public void Rule_matches_structure_with_a_scalar_text_capture(string source, int count)
    {
        var issues = Analyze(source);
        Assert.Equal(count, issues.Count);
        Assert.All(issues, issue => Assert.Equal(DiagnosticSeverity.Warning, issue.Severity));
    }

    [Theory]
    [InlineData("<credentials><password>x</password></wrong>", "XML0001")]
    [InlineData("<credentials><password>x</credentials>", "XML0000")]
    public void Invalid_xml_is_rejected_before_matching(string source, string code)
    {
        var issues = Analyze(source);
        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Code == code);
        Assert.All(issues, issue => Assert.Equal(DiagnosticSeverity.Error, issue.Severity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editor_configuration_loads_and_checks_the_xml_grammar(bool repositoryRoot)
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        string exampleRoot = Path.GetDirectoryName(Example("nitrogen.json"))!;
        service.ConfigureWorkspace(repositoryRoot ? Path.GetFullPath(Path.Combine(exampleRoot, "../..")) : exampleRoot);
        string grammarUri = new Uri(Example("Xml.ngr")).AbsoluteUri;
        service.Open(grammarUri, 1, File.ReadAllText(Example("Xml.ngr")));
        Assert.Empty(service.Diagnostics(grammarUri));
        string uri = new Uri(Example("sample.xml")).AbsoluteUri;
        service.Open(uri, 1, File.ReadAllText(Example("sample.xml")));
        var warning = Assert.Single(service.Diagnostics(uri));
        Assert.Equal("XML1001", warning.Code);
        Assert.Equal(ServiceSeverity.Warning, warning.Severity);
        Assert.Equal(5, warning.Range.Start.Line);
        service.Change(uri, 2, "<root></wrong>");
        Assert.Contains(service.Diagnostics(uri), diagnostic => diagnostic.Code == "XML0001");
    }

    [Fact]
    public async Task Standard_cli_reports_the_grammar_warning_without_failing_the_check()
    {
        var output = new StringWriter();
        int code = await NitrogenCli.RunAsync(["check", Example("sample.xml")], output, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("(6,5): warning XML1001:", output.ToString());
    }
}
