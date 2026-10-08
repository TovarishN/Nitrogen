using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public class CallSignaturesTests
{
    [Fact]
    public void Overloads_are_found_by_name()
    {
        var round = new CallSignature([new CallParameter("x", SemanticTypes.Scalar)], SemanticTypes.Scalar);
        var calls = new CallSignatures(new Dictionary<string, IReadOnlyList<CallSignature>> { ["round"] = [round] });

        Assert.Same(round, Assert.Single(calls.For("round")));
        Assert.Empty(calls.For("floor"));
    }
}
