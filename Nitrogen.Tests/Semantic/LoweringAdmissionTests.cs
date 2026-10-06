using Nitrogen.Binding;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Semantic;

/// <summary>Lowering admission: Full blocks binding errors; SyntaxOnly blocks only recovered syntax.</summary>
public sealed class LoweringAdmissionTests
{
    const string Duplicate = "syntax module M { syntax R = \"a\"; syntax R = \"b\"; }";

    static LoweringResult Lower(string source, LoweringAdmission admission, Action<FileSemantics>? inspect = null)
    {
        using var parsed = NgrParser.Language.Parse(source, NitrogenModule.File);
        var project = new Project(NgrParser.Language);
        project.Set("m.ngr", parsed.Tree);
        var file = new ProjectSemantics(project)["m.ngr"];
        inspect?.Invoke(file);
        return HirLowering.Lower(file, NgrParser.Language.SemanticCatalog, Guid.NewGuid(), admission);
    }

    [Fact]
    public void Full_admission_blocks_a_binding_error()
    {
        var lowered = Lower(Duplicate, LoweringAdmission.Full,
            file => Assert.Contains(file.Binding.Diagnostics, diagnostic => diagnostic.Code == BindingCodes.Duplicate));

        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
    }

    [Fact]
    public void Syntax_only_admission_lowers_despite_a_binding_error()
    {
        var lowered = Lower(Duplicate, LoweringAdmission.SyntaxOnly);

        Assert.Empty(lowered.Diagnostics);
        Assert.Equal("Grammar.File", Assert.IsType<HirOperation>(Assert.Single(lowered.Roots)).Signature.Id);
    }

    [Theory]
    [InlineData(LoweringAdmission.Full)]
    [InlineData(LoweringAdmission.SyntaxOnly)]
    public void Recovered_syntax_is_blocked_under_every_admission(LoweringAdmission admission)
    {
        var lowered = Lower("syntax module M { syntax R = ; }", admission);

        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0001");
    }

    [Fact]
    public void A_context_admits_fully_by_default()
    {
        using var parsed = NgrParser.Language.Parse(Duplicate, NitrogenModule.File);
        var project = new Project(NgrParser.Language);
        project.Set("m.ngr", parsed.Tree);
        var file = new ProjectSemantics(project)["m.ngr"];

        Assert.Equal(LoweringAdmission.Full, new LoweringContext(file, Guid.NewGuid()).Admission);
    }
}
