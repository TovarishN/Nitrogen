using Nitrogen.Ngr;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarSemanticsTests
{
    [Fact]
    public void The_grammar_module_composes_with_core()
    {
        var catalog = SemanticCatalog.Compose([GrammarSemantics.Module], out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.NotNull(catalog);
        Assert.Equal("Grammar", GrammarSemantics.Module.Name);
        Assert.Equal(["Core"], GrammarSemantics.Module.Imports);
        Assert.Equal(28, GrammarSemantics.Module.Types.Count);
        Assert.Contains(GrammarSemantics.Types.QuoteParameter, GrammarSemantics.Module.Types);
        Assert.Contains(GrammarSemantics.Types.QuoteMatchBody, GrammarSemantics.Module.Types);
        Assert.Contains(GrammarSemantics.Types.QuoteMatchArm, GrammarSemantics.Module.Types);
        Assert.Contains(GrammarSemantics.Types.QuoteCaptures, GrammarSemantics.Module.Types);
        Assert.All(GrammarSemantics.Module.Operations, operation => Assert.StartsWith("Grammar.", operation.Id));
        Assert.Equal(GrammarSemantics.Types.File, catalog!.Operations["Grammar.File"].Result);
        Assert.Equal(SemanticTypes.SequenceOf(GrammarSemantics.Types.Module), catalog.Operations["Grammar.File"].Inputs[0]);
        Assert.Equal(SemanticTypes.OptionalOf(GrammarSemantics.Types.LanguageDeclaration), catalog.Operations["Grammar.File"].Inputs[1]);
    }
}
