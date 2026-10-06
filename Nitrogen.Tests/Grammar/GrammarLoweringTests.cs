using Nitrogen.Binding;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Nitrogen.ngr's lowers clauses: every .ngr file lowers to one typed Grammar.File root.</summary>
public sealed class GrammarLoweringTests
{
    public static TheoryData<string> RepositoryGrammars()
    {
        string root = AppContext.BaseDirectory;
        var data = new TheoryData<string>();
        foreach (string path in Directory.GetFiles(Path.Combine(root, "Grammars"), "*.ngr").Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(root, path));
        data.Add(Path.Combine("GeometryLanguage", "Geometry.ngr"));
        data.Add(Path.Combine("DateCalcLanguage", "DateCalc.ngr"));
        return data;
    }

    [Theory]
    [MemberData(nameof(RepositoryGrammars))]
    public void Every_repository_grammar_lowers_to_one_grammar_file(string relative)
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relative));
        using var parsed = NgrParser.Language.Parse(text, NitrogenModule.File);
        Assert.True(parsed.Success, parsed.Success ? "" : parsed.FormatMessage(parsed.Diagnostics[0]));
        var project = new Project(NgrParser.Language);
        project.Set(relative, parsed.Tree);
        var file = new ProjectSemantics(project)[relative];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, NgrParser.Language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal("Grammar.File", root.Signature.Id);
        Assert.Equal(GrammarSemantics.Types.File, root.Type);
    }

    [Fact]
    public void A_grammar_lowers_its_structure_as_raw_text()
    {
        const string source = "syntax module M { token T = ['a'..'z' '_']+; syntax R = \"r\" Items:(T; \",\")* lowers M.Build(sequence M.Part Items); }";
        WithRoot(source, root =>
        {
            var module = Op(Assert.Single(Seq(root.Arguments[0])), "Module");
            Assert.Equal("M", Text(module.Arguments[0]));
            var members = Seq(module.Arguments[1]);
            Assert.Equal(2, members.Count);

            var token = Op(members[0], "TokenRule");
            Assert.Equal("T", Text(token.Arguments[0]));
            Assert.Null(Opt(token.Arguments[2]));
            var postfix = Postfix(token.Arguments[1]);
            Assert.Equal(["+"], Seq(postfix.Arguments[1]).Select(Text));
            var charClass = Op(postfix.Arguments[0], "CharClass");
            Assert.Null(Opt(charClass.Arguments[0]));
            var items = Seq(charClass.Arguments[1]).Select(item => Op(item, "ClassItem")).ToList();
            Assert.Equal(("'a'", "'z'"), (Text(items[0].Arguments[0]), Text(Opt(items[0].Arguments[1])!)));
            Assert.Equal("'_'", Text(items[1].Arguments[0]));
            Assert.Null(Opt(items[1].Arguments[1]));

            var rule = Op(members[1], "SyntaxRule");
            Assert.Equal("R", Text(rule.Arguments[0]));
            Op(rule.Arguments[3], "Terminator");
            var lowers = Op(Assert.Single(Seq(rule.Arguments[2])), "Lowers");
            var call = Op(lowers.Arguments[0], "LowersCall");
            Assert.Equal("M.Build", Text(Op(call.Arguments[0], "FixedOperation").Arguments[0]));
            var argument = Op(Assert.Single(Seq(call.Arguments[1])), "SequenceArgument");
            Assert.Equal(("M.Part", "Items"), (Text(argument.Arguments[0]), Text(argument.Arguments[1])));

            var elements = Seq(Op(Assert.Single(Seq(Op(rule.Arguments[1], "Choice").Arguments[0])), "Sequence").Arguments[0]);
            Assert.Equal(2, elements.Count);
            Assert.Equal("\"r\"", Text(Op(PostfixOf(elements[0]).Arguments[0], "Literal").Arguments[0]));
            var labeled = Op(elements[1], "Element");
            Assert.Equal("Items", Text(Opt(labeled.Arguments[0])!));
            var list = PostfixOf(elements[1]);
            Assert.Empty(Seq(list.Arguments[1]));
            var group = Op(list.Arguments[0], "Parenthesized");
            var tail = Op(group.Arguments[1], "SeparatorTail");
            Assert.Equal("*", Text(tail.Arguments[1]));
            Assert.Equal("\",\"", Text(Op(Postfix(tail.Arguments[0]).Arguments[0], "Literal").Arguments[0]));
        });
    }

    [Fact]
    public void Optional_keywords_and_semantics_lower_with_presence()
    {
        const string source = "syntax module M { symbols { v } token T = ['a'..'z']+; syntax R = Name:T declares v Name export { out O : int = 0; check O >= 0 : \"bad\" at Name; } }";
        WithRoot(source, root =>
        {
            var module = Op(Assert.Single(Seq(root.Arguments[0])), "Module");
            var rule = Op(Seq(module.Arguments[1])[2], "SyntaxRule");
            var declares = Op(Assert.Single(Seq(rule.Arguments[2])), "Declares");
            Assert.Equal(("v", "Name"), (Text(declares.Arguments[0]), Text(declares.Arguments[1])));
            Assert.Null(Opt(declares.Arguments[2]));
            Assert.Null(Opt(declares.Arguments[3]));
            Assert.Equal("export", Text(Opt(declares.Arguments[4])!));
            Assert.Null(Opt(declares.Arguments[5]));

            var semantics = Op(Op(rule.Arguments[3], "RuleSemantics").Arguments[0], "Semantics");
            var items = Seq(semantics.Arguments[0]);
            var property = Op(items[0], "PropertyDecl");
            Assert.Equal(("out", "O"), (Text(property.Arguments[0]), Text(property.Arguments[2])));
            Assert.Empty(Seq(property.Arguments[1]));
            var check = Op(items[1], "Check");
            Assert.Null(Opt(check.Arguments[0]));
            Assert.Equal("Name", Text(Opt(check.Arguments[3])!));
        });
    }

    static void WithRoot(string source, Action<HirOperation> inspect)
    {
        using var parsed = NgrParser.Language.Parse(source, NitrogenModule.File);
        Assert.True(parsed.Success);
        var project = new Project(NgrParser.Language);
        project.Set("m.ngr", parsed.Tree);
        var file = new ProjectSemantics(project)["m.ngr"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, NgrParser.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        inspect(Op(Assert.Single(lowered.Roots), "File"));
    }

    static HirOperation Op(HirNode node, string name)
    {
        var operation = Assert.IsType<HirOperation>(node);
        Assert.Equal("Grammar." + name, operation.Signature.Id);
        return operation;
    }

    static string Text(HirNode node) => Assert.IsType<HirText>(node).Value;

    static HirNode? Opt(HirNode node) => Assert.IsType<HirOptional>(node).Value;

    static IReadOnlyList<HirNode> Seq(HirNode node) => Assert.IsType<HirSequence>(node).Items;

    /// <summary>The postfix of an expression that is one choice of one element.</summary>
    static HirOperation Postfix(HirNode expression) =>
        PostfixOf(Assert.Single(Seq(Op(Assert.Single(Seq(Op(expression, "Choice").Arguments[0])), "Sequence").Arguments[0])));

    /// <summary>The postfix of an element: Element → Unary → Postfix.</summary>
    static HirOperation PostfixOf(HirNode element) =>
        Op(Op(Op(element, "Element").Arguments[1], "Unary").Arguments[1], "Postfix");
}
