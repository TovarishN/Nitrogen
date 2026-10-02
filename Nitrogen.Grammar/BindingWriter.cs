using System.Text;

namespace Nitrogen.Grammar;

/// <summary>
/// Emits a module's binding table (issue 237): <c>GetBinding(localKind)</c> over one
/// <c>BindingRule</c> per kind, and <c>Builtins</c>. A module without clauses or built-ins emits
/// nothing, so its generated code is unchanged.
/// </summary>
internal static class BindingWriter
{
    public static void Write(StringBuilder b, ModuleInfo info)
    {
        bool hasClauses = info.Kinds.Any(k => BindingClauses(k).Length > 0);
        if (hasClauses)
        {
            b.Append("    static readonly global::Nitrogen.Binding.BindingRule?[] s_bindings =\n    {\n        null,\n");
            foreach (var kind in info.Kinds) b.Append("        ").Append(Rule(kind)).Append(", // ").Append(kind.Name).Append('\n');
            b.Append("    };\n\n")
                .Append("    public override global::Nitrogen.Binding.BindingRule? GetBinding(int localKind) =>\n")
                .Append("        (uint)localKind < (uint)s_bindings.Length ? s_bindings[localKind] : null;\n\n");
        }
        if (info.Module.Builtins.Count > 0)
        {
            b.Append("    static readonly global::Nitrogen.Binding.BuiltinSymbols[] s_builtins =\n    {\n");
            foreach (var builtin in info.Module.Builtins)
                b.Append("        new(").Append(CSharpText.Literal(builtin.Kind.Name)).Append(", new[] { ")
                    .Append(string.Join(", ", builtin.Names.Select(n => CSharpText.Literal(n.Name)))).Append(" }")
                    .Append(builtin.Scope is { } scope ? ", L" + scope.Name : "").Append("),\n");
            b.Append("    };\n\n")
                .Append("    public override global::System.Collections.Generic.IReadOnlyList<global::Nitrogen.Binding.BuiltinSymbols> Builtins => s_builtins;\n\n");
        }
    }

    internal static EquatableArray<BindingClause> Clauses(KindInfo kind) =>
        kind.Alternative is { } alternative ? alternative.Clauses
        : kind.Rule is SyntaxRule rule ? rule.Clauses
        : default;

    static BindingClause[] BindingClauses(KindInfo kind) => Clauses(kind)
        .Where(c => c.Kind is BindingClauseKind.Declares or BindingClauseKind.References or BindingClauseKind.Scope or BindingClauseKind.Dynamic)
        .ToArray();

    static string Rule(KindInfo kind)
    {
        var clauses = BindingClauses(kind);
        if (clauses.Length == 0) return "null";
        var elements = SyntaxCodeWriter.Elements(kind.Alternative?.Body ?? ((SyntaxRule)kind.Rule!).Body);
        string declares = "null", references = "null";
        bool scope = false, dynamic = false;
        foreach (var clause in clauses)
        {
            switch (clause.Kind)
            {
                case BindingClauseKind.Declares:
                    declares = $"new({CSharpText.Literal(clause.Kinds[0].Name)}, {ChildIndex(elements, clause.Field)}, {Bool(clause.Export)}"
                        + (clause.Sequential ? $", {Bool(clause.FileScope)}, true" : clause.FileScope ? ", true" : "") + ")";
                    break;
                case BindingClauseKind.References:
                    references = $"new(new[] {{ {string.Join(", ", clause.Kinds.Select(k => CSharpText.Literal(k.Name)))} }}, "
                        + $"{ChildIndex(elements, clause.Field)}, {Bool(clause.Optional)}"
                        + (clause.Qualifier is { } qualifier ? ", " + CSharpText.Literal(qualifier.Name) : "") + ")";
                    break;
                case BindingClauseKind.Scope:
                    scope = true;
                    break;
                default:
                    dynamic = true;
                    break;
            }
        }
        return $"new({declares}, {references}, {Bool(scope)}, {Bool(dynamic)})";
    }

    /// <summary>The child a clause field names, counted as the views count (predicates push no node); -1 for <c>this</c>.</summary>
    public static int ChildIndex(IReadOnlyList<Expr> elements, string field)
    {
        if (field == "this") return -1;
        int child = 0;
        foreach (var element in elements)
        {
            var bare = element is LabeledExpr l ? l.Inner : element;
            if (bare is PredicateExpr) continue;
            if (element is LabeledExpr labeled && labeled.Label == field) return child;
            child++;
        }
        throw new InvalidOperationException($"'{field}' is not a label; validate before emitting");
    }

    static string Bool(bool value) => value ? "true" : "false";
}
