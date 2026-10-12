using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteOriginTests
{
    [Fact]
    public void Explicit_source_identity_maps_inserted_nodes_and_lowered_origins()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var source = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
        var original = new SourceOrigin("input.quote", Guid.NewGuid(), source.Tree.Root, new TextSpan(0, 5));
        using var compiled = language.CompileQuote("QuoteLists.Expr", "$Value * 2",
            new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("QuoteLists.Expr") });
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, source.Tree.Root, original)),
        });
        var lowered = TemplateListInstantiationTests.Lower(language, result.Tree!);
        var mapped = result.Remap(lowered);
        Assert.Contains(mapped.Roots.SelectMany(HirTraversal.PreOrder).SelectMany(n => n.Origins), o => o == original);
        Assert.Contains(mapped.Roots.SelectMany(n => n.Origins), o => o.Path == "quote.test");
        var entry = Assert.Single(result.Origins, p => result.Tree!.GetText(p.Key).ToString() == "1 + 2");
        Assert.Contains(original, result.MapRange("quote.test", result.Tree!.Span(entry.Key)));
        // Same node number and span from another snapshot must never acquire this input identity.
        var unrelated = new SourceOrigin("other.quote", Guid.NewGuid(), entry.Key, result.Tree.Span(entry.Key));
        Assert.Equal(unrelated, result.MapOrigin(unrelated));
    }

    [Fact]
    public void Missing_input_identity_retains_generated_locations()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var compiled = language.CompileQuote("QuoteLists.Expr", "1 + 2", new Dictionary<string, QuoteHole>());
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>());
        var lowered = TemplateListInstantiationTests.Lower(language, result.Tree!);
        Assert.All(result.Remap(lowered).Roots.SelectMany(HirTraversal.PreOrder).SelectMany(n => n.Origins),
            o => Assert.Equal("quote.test", o.Path));
        Assert.Empty(result.Origins);
    }
    [Fact]
    public void Inserted_names_bind_freshly_and_unresolved_diagnostics_map_to_the_original_subtree()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Scopes.ScopesModule.Instance).Build();
        using var source = language.Parse("unit a { let x = 1; let y = x; }", Nitrogen.Tests.Scopes.ScopesModule.File);
        var originalProject = new Nitrogen.Binding.Project(language);
        originalProject.Set("original.scope", source.Tree);
        Assert.Empty(originalProject.Diagnostics("original.scope"));
        int reference = Enumerable.Range(0, source.Tree.NodeCount).Single(n => source.Tree.Kind(n) == Nitrogen.Tests.Scopes.ScopesKinds.Ref);
        var original = new SourceOrigin("original.scope", Guid.NewGuid(), reference, source.Tree.Span(reference));
        using var compiled = language.CompileQuote("Scopes.Expr", "$Value",
            new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("Scopes.Expr") });
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, reference, original)),
        });
        source.Dispose();
        var destination = new Nitrogen.Binding.Project(language);
        destination.Set("generated.scope", result.Tree!);
        Assert.NotEmpty(destination.Diagnostics("generated.scope"));
        using var semantics = new Nitrogen.Semantics.ProjectSemantics(destination);
        var snapshotId = Guid.NewGuid();
        var lowered = HirLowering.LowerSelected(semantics["generated.scope"],
            new HashSet<int> { Nitrogen.Tests.Scopes.ScopesKinds.Ref }, snapshotId);
        Assert.Contains(lowered.Diagnostics, d => d.Code == "NH0002");
        var mapped = result.Remap(lowered, "generated.scope", snapshotId);
        Assert.Contains(mapped.Diagnostics, d => d.Code == "NH0002" && d.Origin == original);
    }

    [Fact]
    public void Explicit_template_and_repeated_input_origins_are_kept_separate()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var source = language.ParseTemplate("3", "QuoteLists.Expr", new Dictionary<string, string>());
        var input = new SourceOrigin("input.quote", Guid.NewGuid(), 0, new TextSpan(0, 1));
        var template = new SourceOrigin("helper.cs", Guid.NewGuid(), 4, new TextSpan(100, 24));
        using var compiled = language.CompileQuote("QuoteLists.Expr", "f($Value, $Value)",
            new Dictionary<string, QuoteHole> { ["Value"] = QuoteHole.Node("QuoteLists.Expr") }, new QuoteOptions(template));
        using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Value"] = QuoteArgument.Node(new QuoteNode(source.Tree, source.Tree.Root, input)),
        });
        Assert.Equal(template, result.Origins[0]);
        Assert.Equal(2, result.Origins.Count(p => p.Value == input && result.Tree!.Kind(p.Key) == Nitrogen.Tests.QuoteLists.QuoteListsKinds.Num));
    }
}
