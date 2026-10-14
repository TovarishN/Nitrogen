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
            var category = model.Analysis.Helpers.Category(helper.ReturnCategory, info.Module)!;
            output.Append("        new(").Append(CSharpText.Literal(info.Module.Name + "." + helper.Name.Name)).Append(", ")
                .Append(CSharpText.Literal(category.Key)).Append(", ").Append(CSharpText.Literal(helper.Body.Text))
                .Append(", ").Append(Map(helper.Parameters)).Append(", ").Append(Source(helper.Body));
            output.Append(')');
            if (helper.Match is { } match)
            {
                var selector = helper.Parameters.First(parameter => parameter.Name.Name == match.Selector.Name);
                output.Append(" { Match = new global::Nitrogen.QuoteHelperMatchSpec(")
                    .Append(CSharpText.Literal(match.Selector.Name)).Append(", ")
                    .Append(CSharpText.Literal(model.Analysis.Helpers.Category(selector.Category, info.Module)!.Key))
                    .Append(", new global::Nitrogen.QuoteMatchArmSpec[] {\n");
                foreach (var arm in match.Arms)
                    output.Append("            new(").Append(CSharpText.Literal(arm.Pattern.Text)).Append(", ").Append(Map(arm.Captures))
                        .Append(", ").Append(CSharpText.Literal(arm.Result.Text)).Append(", ").Append(Source(arm.Pattern))
                        .Append(", ").Append(Source(arm.Result)).Append("),\n");
                output.Append("        }) }");
            }
            output.Append(",\n");
        }
        output.Append("    };\n\n");

        string Map(IEnumerable<QuoteParameter> parameters) =>
            "new global::System.Collections.Generic.Dictionary<string, global::Nitrogen.QuoteHole> { " +
            string.Join(", ", parameters.Select(parameter => "[" + CSharpText.Literal(parameter.Name.Name) + "] = new(" +
                CSharpText.Literal(model.Analysis.Helpers.Category(parameter.Category, info.Module)!.Key) +
                (parameter.IsList ? ", true)" : ", false)"))) + " }";
        string Source(QuoteText quote) => "new global::Nitrogen.QuoteHelperSource(" +
            CSharpText.Literal(model.InputOf(info.Module)?.Path ?? "") + $", {quote.Span.Start}, {quote.Span.Length})";
    }
}
