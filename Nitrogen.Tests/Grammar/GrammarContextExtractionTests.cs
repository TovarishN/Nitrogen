using Nitrogen.Grammar;
using Xunit;
namespace Nitrogen.Tests;
public class GrammarContextExtractionTests
{
    [Fact]public void Extracted_scope_and_test_keep_the_original_flag_identity()
    {
        const string rule="syntax R = with(F = true) context(F) \"x\";";
        const string source="syntax module A { context F; "+rule+" }";
        var input=new ExtractionSource("file:///source.ngr",source);
        var selected=GrammarExtraction.Select(input,new(source.IndexOf(rule,StringComparison.Ordinal),rule.Length));Assert.Empty(selected.Problems);
        var result=GrammarExtraction.Transform([input],selected.Selection!,"file:///destination.ngr","B");Assert.Empty(result.Problems);
        Assert.Contains("with(A.F = true)",result.DestinationText);Assert.Contains("context(A.F)",result.DestinationText);
    }
}
