using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteApiTests
{
    [Fact]
    public void Scalar_input_preserves_its_atomic_operator_structure()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompileQuote("QuoteLists.Expr", "$Value * 2",
            new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("QuoteLists.Expr") });
        Assert.True(compiled.Success);
        using var source = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, source.Tree.Root)),
        });
        Assert.True(result.Success);
        var lowered = TemplateListInstantiationTests.Lower(language, result.Tree!);
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal("Test.Mul(Test.Add(1:Core.Scalar,2:Core.Scalar),2:Core.Scalar)",
            TemplateListInstantiationTests.Shape(Assert.Single(lowered.Roots)));
        for (int n = 0; n < result.Tree!.NodeCount; n++)
        {
            Assert.DoesNotContain(result.Tree.Kind(n), new[] { SyntaxKinds.Hole, SyntaxKinds.ListHole });
            Assert.InRange(result.Tree.Span(n).End, 0, result.Tree.Text.Length);
        }
    }

    [Theory]
    [InlineData("f(..$Args)", "f()", true)]
    [InlineData("f(..$Args, 1, ..$Args)", "f(1)", true)]
    [InlineData("required(..$Args)", "", false)]
    [InlineData("required(..$Args, 1)", "required(1)", true)]
    public void Empty_list_arguments_normalize_separators(string text, string expected, bool success)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompileQuote("QuoteLists.Expr", text,
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        Assert.True(compiled.Success);
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Args"] = QuoteArgument.List(Array.Empty<QuoteNode>()),
        });
        Assert.Equal(success, result.Success);
        if (success) Assert.Equal(expected, result.Tree!.Text);
        else { Assert.Null(result.Tree); Assert.Equal("NQ0005", Assert.Single(result.Diagnostics).Code); }
    }

    [Theory]
    [InlineData("missing", "NQ0002")]
    [InlineData("extra", "NQ0002")]
    [InlineData("list", "NQ0002")]
    [InlineData("disposed", "NQ0003")]
    [InlineData("index", "NQ0003")]
    [InlineData("snapshot", "NQ0004")]
    [InlineData("category", "NQ0004")]
    public void Invalid_arguments_return_diagnostics_without_a_tree(string scenario, string code)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompileQuote("QuoteLists.Expr", "$Value",
            new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("QuoteLists.Expr") });
        var sourceLanguage = scenario == "snapshot" ? TemplateListInstantiationTests.CreateLanguage() : language;
        using var source = sourceLanguage.ParseTemplate(scenario == "category" ? "1;" : "1",
            scenario == "category" ? "QuoteLists.Program" : "QuoteLists.Expr", new Dictionary<string, string>());
        var args = new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, scenario == "index" ? -1 : source.Tree.Root)),
        };
        if (scenario == "missing") args.Clear();
        if (scenario == "extra") args["Other"] = args["Value"];
        if (scenario == "list") args["Value"] = QuoteArgument.List([]);
        if (scenario == "disposed") source.Dispose();
        using var result = compiled.Template!.Instantiate(args);
        Assert.False(result.Success);
        Assert.Null(result.Tree);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("$Unknown", "QuoteLists.Expr")]
    [InlineData("..$Args", "QuoteLists.Expr")]
    [InlineData("tokenlist(..$Args)", "QuoteLists.Expr")]
    [InlineData("1", "Missing.Expr")]
    public void Invalid_templates_fail_compilation(string text, string category)
    {
        using var compiled = TemplateListInstantiationTests.CreateLanguage().CompileQuote(category, text,
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        Assert.False(compiled.Success);
        Assert.Null(compiled.Template);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "NQ0001");
    }
    [Fact]
    public void Aliases_and_cross_module_extensions_use_real_kind_membership()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Base.BaseModule.Instance)
            .Add(Nitrogen.Tests.Uses.UsesModule.Instance).Build();
        using var source = language.ParseTemplate("-1", "Base.Value", new Dictionary<string, string>());
        using var compiled = language.CompileQuote("Base.Value", "$Value",
            new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("Base.Value") });
        using var instance = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, source.Tree.Root)),
        });
        Assert.True(instance.Success);
        Assert.Equal("Neg", language.GetKindName(instance.Tree!.Kind(instance.Tree.Root)));

        var calc = new LanguageBuilder().Add(Nitrogen.Tests.Calc.CalcModule.Instance).Build();
        using var statement = calc.ParseTemplate("1;", "Calc.Statement", new Dictionary<string, string>());
        using var quote = calc.CompileQuote("Calc.Program", "$Statement",
            new Dictionary<string, QuoteHole> { ["Statement"] = QuoteHole.Node("Calc.Statement") });
        using var result = quote.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Statement"] = QuoteArgument.Node(new QuoteNode(statement.Tree, statement.Tree.Root)),
        });
        Assert.True(result.Success);
    }

    [Fact]
    public void Template_declarations_and_recovered_source_are_rejected()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Scopes.ScopesModule.Instance).Build();
        using var declaring = language.CompileQuote("Scopes.Item", "let x = 1;", new Dictionary<string, QuoteHole>());
        Assert.False(declaring.Success);
        Assert.Contains(declaring.Diagnostics, d => d.Code == "NQ0006");
        using var malformed = language.ParseRecovering("unit a { let x = ; }", Nitrogen.Tests.Scopes.ScopesModule.File);
        using var compiled = language.CompileQuote("Scopes.File", "$File",
            new Dictionary<string, QuoteHole> { ["File"] = QuoteHole.Node("Scopes.File") });
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["File"] = QuoteArgument.Node(new QuoteNode(malformed.Tree, malformed.Tree.Root)),
        });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "NQ0003");
    }

    [Fact]
    public void Strings_and_comments_containing_markers_remain_ordinary_text()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Lexical.LexicalModule.Instance).Build();
        using var compiled = language.CompileQuote("Lexical.Item", "\"..$Ghost\" /* ..$Unknown */", new Dictionary<string, QuoteHole>());
        Assert.True(compiled.Success);
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>());
        Assert.True(result.Success);
        Assert.Equal("\"..$Ghost\"", result.Tree!.GetText(result.Tree.Root).ToString());
    }
    [Fact]
    public void Lists_admit_imported_alias_members_and_preserve_ordinary_list_layout()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Base.BaseModule.Instance)
            .Add(Nitrogen.Tests.Uses.UsesModule.Instance).Build();
        using var source = language.ParseTemplate("(abc)", "Uses.Entry", new Dictionary<string, string>());
        using var compiled = language.CompileQuote("Uses.Doc", "doc ..$Entries end",
            new Dictionary<string, QuoteHole> { ["Entries"] = QuoteHole.List("Uses.Entry") });
        Assert.True(compiled.Success);
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Entries"] = QuoteArgument.List([new(source.Tree, source.Tree.Root), new(source.Tree, source.Tree.Root)]),
        });
        Assert.True(result.Success);
        Assert.Equal("doc (abc) (abc) end", result.Tree!.Text);
        int list = result.Tree.Child(result.Tree.Root, 1);
        Assert.Equal(SyntaxKinds.List, result.Tree.Kind(list));
        Assert.Equal(2, result.Tree.ChildCount(list));
        Assert.All(Enumerable.Range(0, 2), i => Assert.Equal(Nitrogen.Tests.Base.BaseKinds.Pair, result.Tree.Kind(result.Tree.Child(list, i))));
    }
    [Fact]
    public void Compilation_diagnostic_spans_include_original_leading_trivia()
    {
        using var quote = TemplateListInstantiationTests.CreateLanguage().CompileQuote("QuoteLists.Expr", "  $Unknown",
            new Dictionary<string, QuoteHole>());
        Assert.False(quote.Success);
        Assert.Equal(2, Assert.Single(quote.Diagnostics).Span.Start);
    }
}
