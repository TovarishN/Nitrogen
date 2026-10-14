using System.Text;

namespace Nitrogen.Grammar;

internal static class QuoteHelperWriter
{
    public static void Write(StringBuilder output, EmitModel model, ModuleInfo info)
    {
        if (info.Module.Helpers.Count == 0) return;
        output.Append("    public override global::System.Collections.Generic.IReadOnlyList<global::Nitrogen.QuoteHelper> QuoteHelpers => new global::Nitrogen.QuoteHelper[]\n    {\n");
        foreach (var helper in info.Module.Helpers)
        {
            if (helper.Match is not null) throw new InvalidOperationException("Match helpers require pattern-aware emission.");
            var category = model.Analysis.Helpers.Category(helper.ReturnCategory, info.Module)!;
            output.Append("        new(").Append(CSharpText.Literal(info.Module.Name + "." + helper.Name.Name)).Append(", ")
                .Append(CSharpText.Literal(category.Key)).Append(", ").Append(CSharpText.Literal(helper.Body.Text))
                .Append(", new global::System.Collections.Generic.Dictionary<string, global::Nitrogen.QuoteHole>\n        {\n");
            foreach (var parameter in helper.Parameters)
                output.Append("            [").Append(CSharpText.Literal(parameter.Name.Name)).Append("] = new(")
                    .Append(CSharpText.Literal(model.Analysis.Helpers.Category(parameter.Category, info.Module)!.Key))
                    .Append(parameter.IsList ? ", true),\n" : ", false),\n");
            output.Append("        }, new(").Append(CSharpText.Literal(model.InputOf(info.Module)?.Path ?? ""))
                .Append(", ").Append(helper.Body.Span.Start).Append(", ").Append(helper.Body.Span.Length).Append(")),\n");
        }
        output.Append("    };\n\n");
    }
}
