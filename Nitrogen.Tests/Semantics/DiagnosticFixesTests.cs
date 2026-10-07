using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A language's quick fixes dispatch by diagnostic code.</summary>
public class DiagnosticFixesTests
{
    static readonly DiagnosticFixes Fixes = new(new Dictionary<string, Func<FixRequest, IEnumerable<QuickFix>>>
    {
        ["XX0001"] = request => [new QuickFix("Upper", [new TextEdit(request.Span, request.Text.ToUpperInvariant())])],
    });

    [Fact]
    public void A_fixer_proposes_for_its_code()
    {
        var fix = Assert.Single(Fixes.Propose(new FixRequest("XX0001", new TextSpan(0, 2), "ab", null!)));
        Assert.Equal("Upper", fix.Title);
        Assert.Equal(new TextEdit(new TextSpan(0, 2), "AB"), Assert.Single(fix.Edits));
    }

    [Fact]
    public void A_code_without_a_fixer_proposes_nothing()
    {
        Assert.Empty(Fixes.Propose(new FixRequest("XX0002", new TextSpan(0, 2), "ab", null!)));
    }
}
