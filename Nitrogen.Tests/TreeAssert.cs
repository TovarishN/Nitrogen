using Xunit;

namespace Nitrogen.Tests;

/// <summary>Node-for-node tree equality: what an incremental reparse must give compared with a full parse.</summary>
internal static class TreeAssert
{
    public static void Same(ParseResult expected, ParseResult actual)
    {
        Assert.Equal(expected.Success, actual.Success);
        Same(expected.Tree, actual.Tree);
        var e = expected.Diagnostics.ToArray();
        var a = actual.Diagnostics.ToArray();
        Assert.Equal(e.Select(d => (d.Code, d.Severity, d.Span, expected.FormatMessage(d))),
            a.Select(d => (d.Code, d.Severity, d.Span, actual.FormatMessage(d))));
    }

    public static void Same(SyntaxTree expected, SyntaxTree actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.NodeCount, actual.NodeCount);
        for (int n = 0; n < expected.NodeCount; n++)
        {
            Assert.True(expected.Kind(n) == actual.Kind(n), $"kind of node {n}");
            Assert.True(expected.Span(n) == actual.Span(n), $"span of node {n}: {expected.Span(n)} vs {actual.Span(n)}");
            Assert.True(expected.Flags(n) == actual.Flags(n), $"flags of node {n}");
            Assert.True(expected.ChildCount(n) == actual.ChildCount(n), $"child count of node {n}");
            for (int k = 0; k < expected.ChildCount(n); k++)
                Assert.True(expected.Child(n, k) == actual.Child(n, k), $"child {k} of node {n}");
        }
        Assert.Equal(expected.Trivia.ToArray(), actual.Trivia.ToArray());
        Assert.Equal(expected.SkippedSpans.ToArray(), actual.SkippedSpans.ToArray());
    }
}
