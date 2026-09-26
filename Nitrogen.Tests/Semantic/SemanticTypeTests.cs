using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SemanticTypeTests
{
    [Fact]
    public void Type_identity_is_structural_across_separate_instances()
    {
        var a = SemanticType.Named("Units", "Vector", SemanticTypes.Angle);
        var b = SemanticType.Named("Units", "Vector", SemanticType.Named("Units", "Angle"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, SemanticType.Named("Other", "Vector", SemanticTypes.Angle));
    }

    [Fact]
    public void Type_arguments_are_copied_and_order_matters()
    {
        var arguments = new[] { SemanticTypes.Angle, SemanticTypes.Bool };
        var type = SemanticType.Named("Units", "Pair", arguments);
        arguments[0] = SemanticTypes.Scalar;

        Assert.Equal(SemanticType.Named("Units", "Pair", SemanticTypes.Angle, SemanticTypes.Bool), type);
        Assert.NotEqual(type, SemanticType.Named("Units", "Pair", SemanticTypes.Bool, SemanticTypes.Angle));
    }

    [Fact]
    public void Type_identity_rejects_empty_names()
    {
        Assert.Throws<ArgumentException>(() => SemanticType.Named("", "Angle"));
        Assert.Throws<ArgumentException>(() => SemanticType.Named("Units", ""));
    }

    [Fact]
    public void Semantic_symbol_reuses_the_bound_declaration()
    {
        string text = MotionTypingTests.Skill(MotionTypingTests.Track("1deg"));
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        var binding = project.Set("a.skill", parsed.Tree);
        var declared = Assert.Single(binding.Declarations, symbol => symbol.Name == "probe");

        var semantic = SemanticSymbol.From(declared, "Motion", SemanticTypes.Angle);

        Assert.Same(declared, semantic.Binding);
        Assert.Equal("a.skill", semantic.Binding.Path);
        Assert.Equal("Motion:skill:probe", semantic.Id);
        Assert.Equal(SemanticTypes.Angle, semantic.Type);
    }
}
