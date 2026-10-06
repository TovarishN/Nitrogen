using Nitrogen.Grammar;
using Nitrogen.Ngr.Syntax;

namespace Nitrogen.Ngr;

/// <summary>
/// The self-hosted <c>.ngr</c> parser: generated from <c>Nitrogen.ngr</c> and mapped to the same
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
            return new GrammarParseResult(new NgrMapper(result.Tree).File(), default);
        }
        catch (NgrMappingException error)
        {
            return Failure(error.Message, error.Span);
        }
    }

    static GrammarParseResult Failure(string message, GrammarSpan span) =>
        new(null, new[] { new GrammarDiagnostic(GrammarCodes.Syntax, GrammarSeverity.Error, message, span) });
}
