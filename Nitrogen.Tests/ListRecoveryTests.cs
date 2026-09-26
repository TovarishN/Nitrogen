using Nitrogen.Tests.Lists;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Separated lists and stray closers (issue 235, Plan 3b).</summary>
public class ListRecoveryTests
{
    static readonly Language Lists = new LanguageBuilder().Add(ListsModule.Instance).Build();

    static string Dump(string text)
    {
        using var result = Lists.Parse(text, ListsModule.File);
        Assert.Single(result.Diagnostics.ToArray());
        return SyntaxDumper.Dump(result.Tree);
    }

    static string Set(string values) => $"(File (List (Set \"set\" Name:\"a\" \"(\" {values} \")\" \";\")))";

    [Fact]
    public void A_bad_item_after_a_separator_is_skipped_and_the_list_goes_on() =>
        Assert.Equal(Set("(List~ Number:\"1\" \",\" Number:\"2\")"), Dump("set a (1, ?? 2) ;"));

    [Fact]
    public void A_bad_first_item_is_skipped_too() =>
        Assert.Equal(Set("(List~ Number:\"1\")"), Dump("set a (?? 1) ;"));

    [Fact]
    public void A_missing_separator_is_inserted_before_the_next_item() =>
        Assert.Equal(Set("(List Number:\"1\" !Literal Number:\"2\")"), Dump("set a (1 2) ;"));

    [Fact]
    public void A_stray_closer_inside_the_parentheses_is_skipped() =>
        Assert.Equal(Set("(List~ Number:\"1\" \",\" Number:\"2\")"), Dump("set a (1, } 2) ;"));

    [Fact]
    public void Valid_lists_are_untouched_by_the_recovery_pass()
    {
        using var result = Lists.ParseRecovering("set a (1, 2) ; set b () ;", ListsModule.File);
        Assert.True(result.Success);
    }
}
