using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarExtractionParityTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-extract-parity-").FullName;
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service(string grammar, string start, string? helper = null)
    {
        File.WriteAllText(Path.Combine(_root, "a.ngr"), grammar);
        string sources = helper is null ? "" : ", \"sources\": [\"Helper.cs\"]";
        if (helper is not null) File.WriteAllText(Path.Combine(_root, "Helper.cs"), helper);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), $$"""{"languages":[{"name":"Test","extensions":[".test"],"start":"{{start}}","grammars":["a.ngr"]{{sources}}}]}""");
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("a.ngr"), 1, grammar);
        return service;
    }
    ExtractionProposal Extract(NitrogenLanguageService service, string grammar, string rule)
    {
        var map = new LineMap(grammar);
        int at = grammar.IndexOf(rule, StringComparison.Ordinal);
        var preparation = service.PrepareExtraction(Uri("a.ngr"), new(map.PositionOf(at), map.PositionOf(at + rule.Length)));
        Assert.Null(preparation.UnavailableReason);
        var proposal = service.ResolveExtraction(new(preparation.Id!, Uri("z.ngr"), "Shared"));
        Assert.Null(proposal.UnavailableReason);
        return proposal;
    }
    void ApplyUnsaved(NitrogenLanguageService service, ExtractionProposal proposal)
    {
        // Mirrors CreateFile followed by text-model edits; config/source buffers stay unsaved.
        File.WriteAllText(new System.Uri(proposal.DestinationUri).LocalPath, "");
        service.Open(proposal.DestinationUri, 1, proposal.DestinationText);
        foreach (var change in proposal.Changes)
        {
            string before = change.OriginalText;
            string text = RenameTests.Apply(before, change.Edits);
            if (change.Version is null) service.Open(change.Uri, 1, before);
            service.Change(change.Uri, (change.Version ?? 1) + 1, text);
        }
    }
    static string Shape(HirNode node) => node switch
    {
        HirOperation op => op.Signature.Id + "(" + string.Join(",", op.Arguments.Select(Shape)) + ")",
        HirConstant constant => constant.Value + ":" + constant.Type,
        _ => node.GetType().Name + ":" + node.Type,
    };
    [Fact]
    public void Extraction_refreshes_unsaved_configuration_and_the_same_service_parser()
    {
        const string rule = "syntax Word = \"x\";";
        const string grammar = "syntax module A { " + rule + " syntax Program = Word; }";
        using var service = Service(grammar, "A.Program");
        service.Open(Uri("sample.test"), 1, "x");
        Assert.Empty(service.Diagnostics(Uri("sample.test")));
        var result = Extract(service, grammar, rule);
        ApplyUnsaved(service, result);
        Assert.Empty(service.Diagnostics(Uri("sample.test")));
        service.Change(result.DestinationUri, 2, result.DestinationText.Replace("\"x\"", "\"y\""));
        Assert.NotEmpty(service.Diagnostics(Uri("sample.test")));
    }
    [Fact]
    public void Binding_targets_and_grammar_navigation_survive_a_rule_move()
    {
        const string rule = "syntax Ref = Name:Identifier references value Name;";
        const string grammar = "syntax module A { symbols { value } token Identifier = ['a'..'z']+ except \"let\"; syntax Program = Let Ref \";\"; syntax Let = \"let\" Name:Identifier \";\" declares value Name; " + rule + " }";
        using var service = Service(grammar, "A.Program");
        service.Open(Uri("sample.test"), 1, "let x; x;");
        var before = Assert.Single(service.Definition(Uri("sample.test"), new(0, 7)));
        var result = Extract(service, grammar, rule);
        ApplyUnsaved(service, result);
        Assert.Empty(service.Diagnostics(Uri("sample.test")));
        Assert.Equal(before, Assert.Single(service.Definition(Uri("sample.test"), new(0, 7))));
        string updated = service.TextOf(Uri("a.ngr"))!;
        var at = new LineMap(updated).PositionOf(updated.IndexOf("Shared.Ref", StringComparison.Ordinal) + 8);
        Assert.Contains(service.Definition(Uri("a.ngr"), at), d => d.Uri == result.DestinationUri);
    }
    [Fact]
    public void Extracted_qualified_uses_remain_references_and_unsafe_rename_is_refused()
    {
        const string rule = "syntax Word = \"x\";";
        const string grammar = "syntax module A { " + rule + " syntax Program = Word; }";
        using var service = Service(grammar, "A.Program");
        var result = Extract(service, grammar, rule);
        ApplyUnsaved(service, result);
        var declaration = new LineMap(result.DestinationText).PositionOf(result.DestinationText.IndexOf("Word =", StringComparison.Ordinal));
        Assert.Contains(service.References(result.DestinationUri, declaration, false), r => r.Uri == Uri("a.ngr"));
        Assert.Contains("qualified", Assert.Throws<RenameRefusedException>(() => service.Rename(result.DestinationUri, declaration, "Changed")).Message);
        Assert.Throws<RenameRefusedException>(() => service.PrepareRename(result.DestinationUri, declaration));
    }
    [Fact]
    public void Typed_lowering_and_values_remain_equivalent_after_a_rule_move()
    {
        const string rule = "syntax Num = Text:Number lowers literal Core.Scalar Text;";
        const string grammar = "syntax module Sum { token Number = ['0'..'9']+; syntax Doc = Add; syntax Add = \"add\" Left:Num Right:Num \";\" lowers Sum.Add(Left, Right); " + rule + " }";
        const string helper = """
            using Nitrogen.Semantic;
            using Nitrogen.Workspace.Grammar;
            public static class Helpers
            {
                public static readonly OperationSignature Add = new("Sum.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
                public static readonly SemanticModule Module = new("Sum", [], [], [Add]);
                public static readonly EvaluationProfile Profile = new(new HashSet<int> { SumKinds.Add },
                    catalog => [new ProjectionHandler(catalog.Operations["Sum.Add"], a => new ProjectedValue(SemanticTypes.Scalar, (float)a[0].Value + (float)a[1].Value))],
                    _ => null, value => value.Value.ToString()!);
            }
            """;
        using var service = Service(grammar, "Sum.Doc", helper);
        service.Open(Uri("sample.test"), 1, "add 2 3;");
        var before = Shape(Assert.Single(service.InspectDocument(Uri("sample.test"))!.Roots));
        Assert.Equal("= 5", Assert.Single(service.ValueHints(Uri("sample.test"), new(new(0, 0), new(1, 0)))).Label);
        ApplyUnsaved(service, Extract(service, grammar, rule));
        Assert.Empty(service.Diagnostics(Uri("sample.test")));
        Assert.Equal(before, Shape(Assert.Single(service.InspectDocument(Uri("sample.test"))!.Roots)));
        Assert.Equal("= 5", Assert.Single(service.ValueHints(Uri("sample.test"), new(new(0, 0), new(1, 0)))).Label);
    }
    [Fact]
    public void An_extracted_quasiquote_alternative_keeps_its_lowering_and_hover()
    {
        const string alternative = "| Same = Value:Expr \"same\" precedence 30 rewrites <[ $Value ]>";
        const string grammar = "syntax module Calc { token Number = ['0'..'9']+; syntax Program = Value:Expr \";\" lowers Test.Identity(Value); extensible syntax Expr { out hover Type : Nitrogen.Semantic.SemanticType? = null; | Num = Value:Number lowers literal Core.Scalar Value { Type = Nitrogen.Semantic.SemanticTypes.Scalar; } " + alternative + " } }";
        using var service = Service(grammar, "Calc.Program",
            "public static class Helpers { public static readonly Nitrogen.Semantic.SemanticModule Module = new(\"Test\", [], [], [new(\"Test.Identity\", Nitrogen.Semantic.SemanticTypes.Scalar, Nitrogen.Semantic.SemanticTypes.Scalar)]); }");
        service.Open(Uri("sample.test"), 1, "2 same;");
        Assert.Empty(service.Diagnostics(Uri("sample.test")));
        string before = Shape(Assert.Single(service.InspectDocument(Uri("sample.test"))!.Roots));
        ApplyUnsaved(service, Extract(service, grammar, alternative));
        Assert.Empty(service.Diagnostics(Uri("sample.test")));
        Assert.Equal(before, Shape(Assert.Single(service.InspectDocument(Uri("sample.test"))!.Roots)));
        Assert.Contains("Core.Scalar", service.Hover(Uri("sample.test"), new(0, 3))!.Markdown);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
