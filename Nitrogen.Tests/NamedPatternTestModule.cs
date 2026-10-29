using Nitrogen.Semantic;
using Nitrogen.Tests.QuoteLists;

namespace Nitrogen.Tests;

/// <summary>A syntax-free module that only contributes named patterns over QuoteLists syntax.</summary>
sealed class NamedPatternTestModule(params NamedPatternSpec[] specs) : SyntaxModule("P")
{
    public override string GetKindName(int localKind) => $"P#{localKind}";
    public override void Register(ExtensionRegistry registry) { }
    public override IReadOnlyList<NamedPatternSpec> NamedPatterns => specs;

    internal const string Expr = "QuoteLists.Expr";
    static readonly QuoteHelperSource Source = new("patterns.test", 0, 0);

    internal static NamedPatternSpec Spec(string name, params (string Text, Dictionary<string, QuoteHole>? Captures)[] alternatives) =>
        new("P." + name, Expr, alternatives.Select(alternative => new NamedPatternAlternative(alternative.Text,
            alternative.Captures ?? new Dictionary<string, QuoteHole>(), Source)).ToArray(), Source);

    internal static (string, Dictionary<string, QuoteHole>?) Alt(string text, params (string Name, QuoteHole Hole)[] captures) =>
        (text, captures.ToDictionary(capture => capture.Name, capture => capture.Hole));

    internal static bool TryCompose(out Language? language, out IReadOnlyList<CompositionDiagnostic> diagnostics,
        params NamedPatternSpec[] specs) =>
        new LanguageBuilder().Add(QuoteListsModule.Instance).Add(new NamedPatternTestModule(specs))
            .TryBuild(out language, out diagnostics);

    internal static Language Compose(params NamedPatternSpec[] specs)
    {
        Xunit.Assert.True(TryCompose(out var language, out var diagnostics, specs), string.Join("\n", diagnostics.Select(d => d.Code + " " + d.Message)));
        return language!;
    }
}
