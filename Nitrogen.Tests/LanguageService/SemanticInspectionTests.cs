using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.MotionDsl;
using Nitrogen.Semantic;
using Nitrogen.Tests.Scopes;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

public sealed class SemanticInspectionTests(ITestOutputHelper output)
{
    const string MotionUri = "file:///w/a.skill";
    const string PolicyUri = "file:///w/a.policy";

    static DocumentPosition At(string text, string needle, int delta = 0) =>
        new LineMap(text).PositionOf(text.IndexOf(needle, StringComparison.Ordinal) + delta);

    [Fact]
    public void Inspect_selects_the_deepest_motion_node_and_resolved_declaration()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 7, text);

        var document = Assert.IsType<DocumentInspection>(service.InspectDocument(MotionUri));
        var name = Assert.IsType<SemanticInspection>(service.Inspect(MotionUri, At(text, "gain + 1deg")));
        var add = Assert.IsType<SemanticInspection>(service.Inspect(MotionUri, At(text, "+ 1deg")));

        Assert.Equal(7, document.Version);
        Assert.Equal(document.SnapshotId, name.SnapshotId);
        Assert.Equal(document.SnapshotId, add.SnapshotId);
        Assert.IsType<HirSymbolRef>(name.Node);
        Assert.Equal(SemanticTypes.Scalar, name.Type);
        Assert.Equal("Motion.AddScalarAngle", Assert.IsType<HirOperation>(add.Node).Signature.Id);
        Assert.Equal(At(text, "gain: float"), name.Declaration!.Range.Start);
        Assert.Same(document.Roots[0], name.Root);
        Assert.Null(service.Inspect(MotionUri, At(text, "lifecycle")));
    }

    [Fact]
    public void Inspect_uses_the_same_api_for_policy_clamp()
    {
        var text = PolicyValueChecksTests.Reference.Replace("clamp knee 0.2", "clamp knee 1", StringComparison.Ordinal);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(PolicyUri, 1, text);

        var inspection = Assert.IsType<SemanticInspection>(service.Inspect(PolicyUri, At(text, "clamp knee 1", 11)));

        Assert.Equal(SemanticTypes.Angle, inspection.Type);
        Assert.IsType<HirConstant>(inspection.Node);
        Assert.Equal("Policy.Clamp", Assert.IsType<HirOperation>(inspection.Root).Signature.Id);
        Assert.Null(service.InspectDocument("file:///w/closed.skill"));
    }

    [Fact]
    public void Edited_document_gets_a_new_snapshot_and_old_result_remains_readable()
    {
        var before = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        var after = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 2deg"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 1, before);
        var firstDocument = service.InspectDocument(MotionUri)!;
        var old = service.Inspect(MotionUri, At(before, "+ 1deg"))!;
        Assert.Same(firstDocument, service.InspectDocument(MotionUri));
        Assert.Equal(firstDocument.SnapshotId, service.Inspect(MotionUri, At(before, "gain + 1deg"))!.SnapshotId);

        service.Change(MotionUri, 2, after);
        var secondDocument = service.InspectDocument(MotionUri)!;
        var current = service.Inspect(MotionUri, At(after, "+ 2deg"))!;

        Assert.NotEqual(firstDocument.SnapshotId, secondDocument.SnapshotId);
        Assert.Equal(2, current.Version);
        Assert.Equal("Motion.AddScalarAngle", Assert.IsType<HirOperation>(current.Node).Signature.Id);
        Assert.Equal(SemanticTypes.Angle, old.Type);
        Assert.Equal(firstDocument.SnapshotId, old.Node.Origins[0].SnapshotId);
        Assert.Equal(At(before, "gain + 1deg"), old.Range.Start);
    }

    [Fact]
    public void Closing_and_reopening_the_same_version_rebuilds_the_snapshot()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 1, text);
        var old = service.InspectDocument(MotionUri)!;
        service.Close(MotionUri);
        Assert.Null(service.InspectDocument(MotionUri));
        service.Open(MotionUri, 1, text);
        Assert.NotEqual(old.SnapshotId, service.InspectDocument(MotionUri)!.SnapshotId);
    }

    [Fact]
    public void Parentheses_select_the_inner_reference_and_same_span_ties_select_the_deeper_add()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("(gain) + 1deg"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 1, text);
        Assert.IsType<HirSymbolRef>(service.Inspect(MotionUri, At(text, "gain)"))!.Node);
        for (var i = 0; i < 3; i++)
            Assert.Equal("Motion.AddScalarAngle", Assert.IsType<HirOperation>(
                service.Inspect(MotionUri, At(text, "+ 1deg"))!.Node).Signature.Id);
    }

    [Fact]
    public void Changed_sibling_document_invalidates_a_cross_file_binding_snapshot()
    {
        const string declarationUri = "file:///w/a.scopes";
        const string useUri = "file:///w/b.scopes";
        const string useText = "unit b { use a; }";
        var signature = new OperationSignature("Test.Use", SemanticTypes.Scalar);
        var registration = new LoweringRegistration(ScopesKinds.Use, signature.Id,
            (context, node) => context.File.SymbolOf(node) is { } bound
                ? new HirSymbolRef(SemanticSymbol.From(bound, "Test", SemanticTypes.Scalar), context.Origin(node))
                : null);
        var language = new LanguageBuilder().Add(ScopesModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [], [signature], [registration])).Build();
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("scopes", language, new Dictionary<string, Rule> { [".scopes"] = ScopesModule.File }));
        using var service = new NitrogenLanguageService(registry);
        service.Open(declarationUri, 1, "unit a { }");
        service.Open(useUri, 1, useText);
        var first = service.Inspect(useUri, At(useText, "a;"))!;
        var firstBinding = Assert.IsType<HirSymbolRef>(first.Node).Symbol.Binding;

        service.Change(declarationUri, 2, "unit a { let x = 1; }");
        var second = service.Inspect(useUri, At(useText, "a;"))!;

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Equal(1, second.Version);
        Assert.NotSame(firstBinding, Assert.IsType<HirSymbolRef>(second.Node).Symbol.Binding);
        Assert.Equal(declarationUri, second.Declaration!.Uri);
    }

    [Fact]
    public void Builtin_reference_has_no_source_declaration_location()
    {
        const string uri = "file:///w/b.scopes";
        const string text = "unit b { let x = pi; }";
        var signature = new OperationSignature("Test.Ref", SemanticTypes.Scalar);
        var lowerer = new LoweringRegistration(ScopesKinds.Ref, signature.Id,
            (context, node) => context.File.SymbolOf(node) is { } bound
                ? new HirSymbolRef(SemanticSymbol.From(bound, "Test", SemanticTypes.Scalar), context.Origin(node))
                : null);
        var language = new LanguageBuilder().Add(ScopesModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [], [signature], [lowerer])).Build();
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("scopes", language, new Dictionary<string, Rule> { [".scopes"] = ScopesModule.File }));
        using var service = new NitrogenLanguageService(registry);
        service.Open(uri, 1, text);

        var inspection = service.Inspect(uri, At(text, "pi;"));

        Assert.True(Assert.IsType<HirSymbolRef>(inspection!.Node).Symbol.Binding.IsBuiltin);
        Assert.Null(inspection.Declaration);
    }

    [Fact]
    public void Hover_appends_shared_type_and_operation_without_losing_existing_name_text()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 1, text);

        var name = service.Hover(MotionUri, At(text, "gain + 1deg"))!.Markdown;
        var add = service.Hover(MotionUri, At(text, "+ 1deg"))!.Markdown;
        var literal = service.Hover(MotionUri, At(text, "1deg"))!.Markdown;

        Assert.Contains("declared in a.skill", name);
        Assert.Contains("Core.Scalar", name);
        Assert.Contains("Motion.AddScalarAngle", add);
        Assert.Contains("Units.Angle", add);
        Assert.Contains("Units.Angle", literal);
        Assert.Contains((MathF.PI / 180f).ToString(System.Globalization.CultureInfo.InvariantCulture), literal);
    }

    [Fact]
    public void Policy_clamp_receives_a_standard_hover_from_shared_inspection()
    {
        var text = PolicyValueChecksTests.Reference.Replace("clamp knee 0.2", "clamp knee 1", StringComparison.Ordinal);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(PolicyUri, 1, text);

        var hover = service.Hover(PolicyUri, At(text, "clamp knee 1", 11));

        Assert.Contains("Units.Angle", hover!.Markdown);
        Assert.Contains("Policy.Clamp", hover.Markdown);
        Assert.Contains("1", hover.Markdown);
    }

    [Fact]
    public void Unsupported_and_invalid_motion_expressions_keep_their_existing_hover()
    {
        var unsupported = MotionTypingTests.Skill(MotionTypingTests.Track("gain * 2 + 0.5deg"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 1, unsupported);
        Assert.Equal("`float angle`", service.Hover(MotionUri, At(unsupported, "+ 0.5deg"))!.Markdown);

        var invalid = MotionTypingTests.Skill(MotionTypingTests.Track("true + 1deg"));
        service.Change(MotionUri, 2, invalid);
        Assert.Null(service.Inspect(MotionUri, At(invalid, "+ 1deg")));
        Assert.DoesNotContain("Motion.Add", service.Hover(MotionUri, At(invalid, "+ 1deg"))?.Markdown ?? "");
    }

    [Fact]
    public void Repeated_inspection_reuses_the_document_result_on_representative_inputs()
    {
        const string track = "track biped.a.target_angle { from rest to gain + 1deg during 0s..1s easing linear }";
        var motion = MotionTypingTests.Skill("source main: tracks { " + string.Join(" ", Enumerable.Repeat(track, 40)) + " }");
        var policy = PolicyValueChecksTests.Reference;
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(MotionUri, 1, motion);
        service.Open(PolicyUri, 1, policy);

        foreach (var (uri, text) in new[] { (MotionUri, motion), (PolicyUri, policy) })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var first = service.InspectDocument(uri)!;
            watch.Stop();
            var firstMs = watch.Elapsed.TotalMilliseconds;
            var reads = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                watch.Restart();
                Assert.Same(first, service.InspectDocument(uri));
                watch.Stop();
                reads.Add(watch.Elapsed.TotalMilliseconds);
            }
            reads.Sort();
            output.WriteLine($"{uri}: {text.Length} UTF-16 units, {first.Roots.Count} roots, first {firstMs:F3} ms, repeated median {reads[10]:F3} ms");
            Assert.NotEqual(Guid.Empty, first.SnapshotId);
        }
    }
}
