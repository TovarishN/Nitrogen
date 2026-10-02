using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Templates;
using Xunit;

namespace Nitrogen.Tests.Semantic;

/// <summary>Declarative templates: <c>lowers template Body(Params)</c> and <c>lowers expand Name(Args)</c>.</summary>
public sealed class TemplateLoweringTests
{
    static readonly OperationSignature Add = new("Templates.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly OperationSignature Turn = new("Templates.Turn", SemanticTypes.Angle, SemanticTypes.Angle);
    static readonly Language Language = new LanguageBuilder().Add(TemplatesModule.Instance)
        .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
        .AddSemantic(new SemanticModule("Templates", ["Units"], [], [Add, Turn])).Build();

    sealed class Files : IDisposable
    {
        readonly List<ParseResult> _parsed = [];
        readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
        public Project Project { get; } = new(Language);
        public ProjectSemantics Semantics { get; }

        public Files(params (string Path, string Text)[] files)
        {
            Semantics = new ProjectSemantics(Project);
            foreach (var (path, text) in files)
            {
                var parsed = Language.Parse(text, TemplatesModule.File);
                Assert.True(parsed.Success, text);
                _parsed.Add(parsed);
                _texts[path] = text;
                Project.Set(path, parsed.Tree);
            }
        }

        public LoweringResult Lower(string path) => HirLowering.Lower(Semantics[path], Language.SemanticCatalog, Guid.NewGuid());

        public string Text(SourceOrigin origin) => _texts[origin.Path].Substring(origin.Span.Start, origin.Span.Length);

        public string Text(string path, SemanticDiagnostic diagnostic) => _texts[path].Substring(diagnostic.Span.Start, diagnostic.Span.Length);

        public void Dispose()
        {
            foreach (var parsed in _parsed) parsed.Dispose();
        }
    }

    static float[] Values(HirNode root) =>
        Assert.IsType<HirOperation>(root).Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value).ToArray();

    [Fact]
    public void An_expansion_in_another_file_lowers_the_body_with_its_arguments_and_both_origins()
    {
        using var files = new Files(("defs.t", "def sum(a: Scalar, b: Scalar) = add(a, b);"), ("use.t", "call sum(1, 2); call sum(3, 4);"));
        Assert.Empty(files.Semantics["defs.t"].Diagnostics());
        Assert.Empty(files.Semantics["use.t"].Diagnostics());
        Assert.Empty(files.Lower("defs.t").Roots); // a template body is no root

        var lowered = files.Lower("use.t");
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal([[1f, 2f], [3f, 4f]], lowered.Roots.Select(Values));
        var root = Assert.IsType<HirOperation>(lowered.Roots[0]);
        Assert.Equal(Add, root.Signature);
        Assert.Equal("call sum(1, 2)", files.Text(root.Origins[0]));
        Assert.Contains(root.Origins, origin => files.Text(origin) == "add(a, b)");
        Assert.Equal(["a", "1"], root.Arguments[0].Origins.Select(files.Text));
    }

    [Fact]
    public void Nested_expansions_forward_parameters_by_position_not_name()
    {
        using var files = new Files(
            ("outer.t", "def outer(x: Scalar, y: Scalar) = call inner(y, x);"),
            ("inner.t", "def inner(x: Scalar, y: Scalar) = add(x, y);"),
            ("use.t", "call outer(1, 2);"));
        var lowered = files.Lower("use.t");
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.Single(lowered.Roots);
        Assert.Equal([2f, 1f], Values(root));
        Assert.Equal(["inner.t", "outer.t", "use.t"], root.Origins.Select(origin => origin.Path).Distinct().Order(StringComparer.Ordinal));
        Assert.All(Assert.IsType<HirOperation>(root).Arguments, argument =>
            Assert.Equal(3, argument.Origins.Select(origin => origin.Path).Distinct().Count()));
    }

    [Fact]
    public void An_expansion_projects_through_its_operations()
    {
        using var files = new Files(("use.t", "def sum(a: Scalar, b: Scalar) = add(a, b); call sum(40, 2);"));
        var root = Assert.Single(files.Lower("use.t").Roots);
        var registry = new ProjectionRegistry(Language.SemanticCatalog,
            [new ProjectionHandler(Add, values => new ProjectedValue(SemanticTypes.Scalar, (float)values[0].Value + (float)values[1].Value))]);
        Assert.Equal(42f, HirProjector.Project(root, registry).Value!.Value);
    }

    [Fact]
    public void Argument_count_and_types_are_checked_at_the_call()
    {
        const string source = "def sum(a: Scalar, b: Scalar) = add(a, b); def spin(a: Units.Angle) = turn(a); call sum(1); call spin(1);";
        using var files = new Files(("use.t", source));
        var diagnostics = files.Semantics["use.t"].Diagnostics();
        var arity = Assert.Single(diagnostics, d => d.Code == "NT0008");
        Assert.Equal("sum", files.Text("use.t", arity));
        Assert.Contains("2", arity.Message);
        var type = Assert.Single(diagnostics, d => d.Code == "NT0001");
        Assert.Equal("1", files.Text("use.t", type));
        Assert.Contains("Units.Angle", type.Message);
        Assert.Empty(files.Lower("use.t").Roots);
    }

    [Fact]
    public void Expanding_a_symbol_that_is_no_template_is_NT0007()
    {
        using var files = new Files(("use.t", "alias f; call f(1);"));
        var diagnostic = Assert.Single(files.Semantics["use.t"].Diagnostics());
        Assert.Equal(("NT0007", "f"), (diagnostic.Code, files.Text("use.t", diagnostic)));
        Assert.Empty(files.Lower("use.t").Roots);
    }

    [Fact]
    public void An_unresolved_template_blocks_at_its_name_in_the_file_that_names_it()
    {
        using var missing = new Files(("use.t", "call absent(1);"));
        var error = Assert.Single(missing.Lower("use.t").Diagnostics);
        Assert.Equal(("NH0002", "absent"), (error.Code, missing.Text(error.Origin)));

        using var nested = new Files(("defs.t", "def outer(a: Scalar) = call absent(a);"), ("use.t", "call outer(1);"));
        var lowered = nested.Lower("use.t");
        Assert.Empty(lowered.Roots);
        var inner = Assert.Single(lowered.Diagnostics);
        Assert.Equal(("NH0002", "defs.t", "absent"), (inner.Code, inner.Origin.Path, nested.Text(inner.Origin)));
    }

    [Fact]
    public void An_invalid_template_blocks_its_expansions_but_not_a_valid_sibling()
    {
        using var files = new Files(
            ("defs.t", "def broken(a: Scalar) = turn(a); def sum(a: Scalar, b: Scalar) = add(a, b);"),
            ("use.t", "call broken(1); call sum(1, 2);"));
        Assert.Contains(files.Semantics["defs.t"].Diagnostics(), d => d.Code == "NT0001");
        var lowered = files.Lower("use.t");
        Assert.Equal([1f, 2f], Values(Assert.Single(lowered.Roots)));
        var blocked = Assert.Single(lowered.Diagnostics);
        Assert.Equal(("NH0003", "defs.t"), (blocked.Code, blocked.Origin.Path));
    }

    [Theory]
    [InlineData("def loop(a: Scalar) = call loop(a);", "call loop(1);", 0)]
    [InlineData("def p(a: Scalar) = call q(a);\ndef q(a: Scalar) = call p(a);", "call p(1);", 1)]
    public void A_cyclic_expansion_reports_the_call_that_closes_it(string definitions, string use, int closingLine)
    {
        using var files = new Files(("defs.t", definitions), ("use.t", use));
        Assert.DoesNotContain(files.Semantics["defs.t"].Diagnostics(), d => d.Code.StartsWith("NT", StringComparison.Ordinal));
        var lowered = files.Lower("use.t");
        Assert.Empty(lowered.Roots);
        var cycle = Assert.Single(lowered.Diagnostics);
        Assert.Equal(("NH0007", "defs.t"), (cycle.Code, cycle.Origin.Path));
        var closing = definitions.Split('\n')[closingLine];
        Assert.Equal(definitions.IndexOf(closing, StringComparison.Ordinal) + closing.IndexOf("call ", StringComparison.Ordinal) + 5,
            cycle.Origin.Span.Start);
    }
}
