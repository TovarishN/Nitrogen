using System.Text;

namespace Nitrogen.Grammar;

internal static class NamedPatternWriter
{
    public static void Write(StringBuilder output, EmitModel model, ModuleInfo info)
    {
        if (info.Module.Patterns.Count == 0) return;
        output.Append("    public override global::System.Collections.Generic.IReadOnlyList<global::Nitrogen.NamedPatternSpec> NamedPatterns => new global::Nitrogen.NamedPatternSpec[]\n    {\n");
        foreach (var pattern in info.Module.Patterns)
        {
            var category = model.Analysis.Helpers.Category(pattern.Category, info.Module)!;
            output.Append("        new(").Append(CSharpText.Literal(info.Module.Name + "." + pattern.Name.Name)).Append(", ")
                .Append(CSharpText.Literal(category.Key)).Append(", new global::Nitrogen.NamedPatternAlternative[] {\n");
            foreach (var alternative in pattern.Alternatives)
                output.Append("            new(").Append(CSharpText.Literal(alternative.Pattern.Text)).Append(", ")
                    .Append(QuoteHelperWriter.HoleMap(alternative.Captures, model, info)).Append(", ")
                    .Append(QuoteHelperWriter.QuoteSource(alternative.Pattern, model, info)).Append("),\n");
            output.Append("        }, ").Append(QuoteHelperWriter.QuoteSource(new(pattern.Name.Name, pattern.Name.Span), model, info)).Append("),\n");
        }
        output.Append("    };\n\n");
    }
}
