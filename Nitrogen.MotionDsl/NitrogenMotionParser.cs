using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl.Syntax;
using Ast = Gravity.MotionDSL.Ast;

namespace Nitrogen.MotionDsl;

/// <summary>
/// MotionDSL's body / behavior / motion / skill language parsed by Nitrogen: the same AST as
/// <c>new MotionParser(new MotionLexer(text).Tokenize()).ParseFile()</c>, with failures reported
/// as <see cref="ParseException"/>. Policies and compositions: <see cref="NitrogenPolicyParser"/>.
/// </summary>
public static unsafe class NitrogenMotionParser
{
    static readonly Language s_language =
        new LanguageBuilder().Add(MotionModule.Instance).AddSemantic(UnitsSemanticModule.Instance)
            .AddSemantic(UnitsSemanticModule.Motion).WithTrivia(&MotionTrivia.Skip, MotionTrivia.StartChars).Build();

    public static Language Language => s_language;

    public static Ast.FileNode ParseFile(string text)
    {
        using var result = s_language.Parse(text, MotionModule.File);
        var source = new SourceText(text);
        if (!result.Success || result.HasErrors)
        {
            var diagnostic = result.Diagnostics[0];
            var (line, col) = source.GetLineColumn(diagnostic.Span.Start);
            throw new ParseException(result.FormatMessage(diagnostic), line, col);
        }
        return new MotionAstMapper(result.Tree, source).File();
    }
}
