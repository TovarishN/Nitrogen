using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>What the fast pass expected where it failed (issue 238: keyword completion).</summary>
public class ExpectedTests
{
    [Fact]
    public void The_items_the_parser_expected_at_the_failure()
    {
        var (position, items) = FileBindingTests.Scopes.Expected("unit a { \u0001", ScopesModule.File);
        Assert.Equal(9, position);
        Assert.Contains(("let", true), items);
        Assert.Contains(("use", true), items);
        Assert.Contains(("}", true), items);
    }

    [Fact]
    public void A_text_that_parses_expected_nothing()
    {
        var (position, items) = FileBindingTests.Scopes.Expected("unit a { }", ScopesModule.File);
        Assert.Equal(-1, position);
        Assert.Empty(items);
    }
}
