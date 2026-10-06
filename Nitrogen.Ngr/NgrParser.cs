using Nitrogen.Binding;
using Nitrogen.Grammar;
using Nitrogen.Ngr.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace Nitrogen.Ngr;

/// <summary>
/// The self-hosted <c>.ngr</c> parser: generated from <c>Nitrogen.ngr</c>, lowered to typed
/// <see cref="GrammarSemantics"/> HIR and projected by <see cref="NgrProjector"/> to the same
/// <see cref="GrammarFile"/> model as the bootstrap <see cref="GrammarParser"/>. Error messages
/// and positions may differ from the bootstrap parser's; successful results are identical.
/// </summary>
public static class NgrParser
{
    static readonly Language s_language = new LanguageBuilder().Add(NitrogenModule.Instance).AddSemantic(GrammarSemantics.Module).Build();

    /// <summary>The generated <c>.ngr</c> language with the <see cref="GrammarSemantics"/> catalog, for tests and tools.</summary>
    public static Language Language => s_language;

    public static GrammarParseResult Parse(string text)
    {
        using var result = s_language.Parse(text, NitrogenModule.File);
        if (!result.Success)
        {
            var diagnostic = result.Diagnostics[0];
            return Failure(result.FormatMessage(diagnostic), new GrammarSpan(diagnostic.Span.Start, diagnostic.Span.Length));
        }
        try
        {
            return new GrammarParseResult(NgrProjector.File(LowerFile(result.Tree)), default);
        }
        catch (NgrMappingException error)
        {
            return Failure(error.Message, error.Span);
        }
    }

    const string DocumentPath = "grammar.ngr";

    /// <summary>
    /// The parsed file's one <c>Grammar.File</c> root. Binding errors (duplicates) do not block it:
    /// <see cref="GrammarValidator"/> reports those. Any other outcome on a successful parse is a bug.
    /// </summary>
    static HirOperation LowerFile(SyntaxTree tree)
    {
        var project = new Project(s_language);
        project.Set(DocumentPath, tree);
        var file = new ProjectSemantics(project)[DocumentPath];
        var lowered = HirLowering.Lower(file, s_language.SemanticCatalog, Guid.NewGuid(), LoweringAdmission.SyntaxOnly);
        if (lowered.Roots is [HirOperation { Signature.Id: "Grammar.File" } root]) return root;
        var first = lowered.Diagnostics.FirstOrDefault();
        throw new InvalidOperationException("a parsed grammar did not lower to one Grammar.File: " +
            (first is null ? $"{lowered.Roots.Count} roots" : $"{first.Code} {first.Message}"));
    }

    static GrammarParseResult Failure(string message, GrammarSpan span) =>
        new(null, new[] { new GrammarDiagnostic(GrammarCodes.Syntax, GrammarSeverity.Error, message, span) });
}
