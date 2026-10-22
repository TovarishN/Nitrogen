using Nitrogen.Tests.PaletteGrammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Literals are classified by their place in the rule that matched them (the TextMate palette).</summary>
public sealed class LiteralRolesTests
{
    static LiteralRole? Role(int localKind, string literal) => PaletteModule.Instance.GetLiteralRole(localKind, literal);

    [Theory]
    [InlineData(nameof(PaletteModule.LGoal), "goal", LiteralRole.DeclarationKeyword)]
    [InlineData(nameof(PaletteModule.LGoal), "{", LiteralRole.BlockBracket)]
    [InlineData(nameof(PaletteModule.LTrain), "train", LiteralRole.SectionKeyword)]
    [InlineData(nameof(PaletteModule.LTerm), "sharpness", LiteralRole.ModifierWord)]
    [InlineData(nameof(PaletteModule.LTerm), ";", LiteralRole.Terminator)]
    [InlineData(nameof(PaletteModule.LStep), "hold", LiteralRole.ModifierWord)]
    [InlineData(nameof(PaletteModule.LStep), "..", LiteralRole.Operator)]
    [InlineData(nameof(PaletteModule.LStep), ",", LiteralRole.Separator)]
    [InlineData(nameof(PaletteModule.LStep), "kg", LiteralRole.ConstantWord)]
    [InlineData(nameof(PaletteModule.LDoc), "policy", LiteralRole.Operation)]
    [InlineData(nameof(PaletteModule.LAnchor), "xz", LiteralRole.ConstantWord)]
    [InlineData(nameof(PaletteModule.LHold), "hold", LiteralRole.SectionKeyword)]
    [InlineData(nameof(PaletteModule.LHold), "(", LiteralRole.Parenthesis)]
    [InlineData(nameof(PaletteModule.LHold), "[", LiteralRole.SquareBracket)]
    public void Roles_follow_grammar_shape(string kind, string literal, LiteralRole expected)
    {
        int local = (int)typeof(PaletteModule).GetField(kind)!.GetValue(null)!;
        Assert.Equal(expected, Role(local, literal));
    }

    [Fact]
    public void Argument_words_of_a_lowered_rule_are_not_operations()
    {
        Assert.Equal(LiteralRole.Operation, Role(PaletteModule.LAnchor, "anchored"));
        Assert.Equal(LiteralRole.ConstantWord, Role(PaletteModule.LAnchor, "xyz"));
    }

    [Fact]
    public void Grammar_roles_mirror_runtime_roles_by_name() =>
        Assert.Equal(Enum.GetNames<LiteralRole>(), Enum.GetNames<Nitrogen.Grammar.GrammarLiteralRole>());

    [Fact]
    public void Unknown_literal_has_no_role() => Assert.Null(Role(PaletteModule.LGoal, "nope"));

    [Fact]
    public void Language_routes_a_global_kind_to_its_module()
    {
        var language = new LanguageBuilder().Add(PaletteModule.Instance).Build();
        Assert.Equal(LiteralRole.DeclarationKeyword, language.GetLiteralRole(PaletteKinds.Goal, "goal"));
        Assert.Null(language.GetLiteralRole(SyntaxKinds.List, "goal"));
    }
}
