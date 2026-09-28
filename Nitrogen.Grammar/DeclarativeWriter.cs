using System.Text;

namespace Nitrogen.Grammar;

/// <summary>
/// Emits a module's declarative typing and lowering table (issue 251): one <c>DeclarativeRule</c>
/// per kind with a <c>lowers</c> clause or a typed declaration. A module without them emits nothing.
/// </summary>
internal static class DeclarativeWriter
{
    const string RuleType = "global::Nitrogen.Semantic.DeclarativeRule";
    const string FormType = "global::Nitrogen.Semantic.DeclarativeForm";

    public static void Write(StringBuilder b, ModuleInfo info)
    {
        var entries = info.Kinds.Select(kind => (Kind: kind, Text: Entry(kind))).Where(entry => entry.Text is not null).ToList();
        if (entries.Count == 0) return;
        b.Append("    static readonly ").Append(RuleType).Append("[] s_declarative =\n    {\n");
        foreach (var (kind, text) in entries) b.Append("        ").Append(text).Append(", // ").Append(kind.Name).Append('\n');
        b.Append("    };\n\n")
            .Append("    public override global::System.Collections.Generic.IReadOnlyList<").Append(RuleType)
            .Append("> DeclarativeRules => s_declarative;\n\n");
    }

    static string? Entry(KindInfo kind)
    {
        var clauses = BindingWriter.Clauses(kind);
        var lowers = clauses.FirstOrDefault(c => c.Kind is BindingClauseKind.Lowers or BindingClauseKind.LowersLiteral);
        var declares = clauses.FirstOrDefault(c => c.Kind == BindingClauseKind.Declares && c.Target is not null);
        if (lowers is null && declares is null) return null;

        var elements = SyntaxCodeWriter.Elements(kind.Alternative?.Body ?? ((SyntaxRule)kind.Rule!).Body);
        string form = "None", target = "null", arguments = "new int[0]";
        if (lowers is { Kind: BindingClauseKind.Lowers })
        {
            form = "Operation";
            target = CSharpText.Literal(lowers.Target!.Name);
            arguments = Ints(lowers.Arguments.Select(a => BindingWriter.ChildIndex(elements, a.Name)));
        }
        else if (lowers is not null)
        {
            form = "Literal";
            target = CSharpText.Literal(lowers.Target!.Name);
            arguments = Ints(new[] { BindingWriter.ChildIndex(elements, lowers.Field) });
        }

        string declaredType = "null";
        int declaredChild = -1;
        if (declares?.Target is { } type)
        {
            if (type.Name.IndexOf('.') >= 0) declaredType = CSharpText.Literal(type.Name);
            else declaredChild = BindingWriter.ChildIndex(elements, type.Name);
        }
        return $"new({kind.Local}, {FormType}.{form}, {target}, {arguments}, {declaredType}, {declaredChild})";
    }

    static string Ints(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "new int[0]" : "new[] { " + string.Join(", ", list) + " }";
    }
}
