using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl.PolicySyntax;
using Ast = Gravity.MotionDSL.Ast;

namespace Nitrogen.MotionDsl;

/// <summary>
/// MotionDSL's <c>.policy</c> and <c>.compose</c> documents parsed by Nitrogen, the counterpart of
/// <c>new PolicyParser(new MotionLexer(text).Tokenize())</c>. A language of its own (Policy.ngr),
/// with MotionLexer's trivia.
/// </summary>
public static unsafe partial class NitrogenPolicyParser
{
    static readonly Language s_language =
        new LanguageBuilder().Add(PolicyModule.Instance).AddSemantic(UnitsSemanticModule.Instance)
            .AddSemantic(UnitsSemanticModule.Policy).WithTrivia(&MotionTrivia.Skip, MotionTrivia.StartChars).Build();

    public static Language Language => s_language;

    /// <summary>Same AST as <c>PolicyParser.ParsePolicy()</c>; failures are <see cref="ParseException"/>.</summary>
    public static Ast.PolicyNode ParsePolicy(string text) => Parse(text, PolicyModule.PolicyDocument, static m => m.Policy());

    /// <summary>Same AST as <c>PolicyParser.ParseCompose()</c>; failures are <see cref="ParseException"/>.</summary>
    public static Ast.ComposeNode ParseCompose(string text) => Parse(text, PolicyModule.ComposeDocument, static m => m.Compose());

    static T Parse<T>(string text, Rule start, Func<PolicyAstMapper, T> map)
    {
        using var result = s_language.Parse(text, start);
        var source = new SourceText(text);
        if (!result.Success || result.HasErrors)
        {
            var diagnostic = result.Diagnostics[0];
            var (line, col) = source.GetLineColumn(diagnostic.Span.Start);
            throw new ParseException(result.FormatMessage(diagnostic), line, col);
        }
        return map(new PolicyAstMapper(result.Tree, source));
    }
}
