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

    public static void Write(StringBuilder b, EmitModel model, ModuleInfo info)
    {
        var entries = info.Kinds.Select(kind => (Kind: kind, Text: Entry(model, info, kind))).Where(entry => entry.Text is not null).ToList();
        if (entries.Count == 0) return;
        b.Append("    static readonly ").Append(RuleType).Append("[] s_declarative =\n    {\n");
        foreach (var (kind, text) in entries) b.Append("        ").Append(text).Append(", // ").Append(kind.Name).Append('\n');
        b.Append("    };\n\n")
            .Append("    public override global::System.Collections.Generic.IReadOnlyList<").Append(RuleType)
            .Append("> DeclarativeRules => s_declarative;\n\n");
    }

    static string? Entry(EmitModel model, ModuleInfo info, KindInfo kind)
    {
        var clauses = BindingWriter.Clauses(kind);
        var lowers = clauses.FirstOrDefault(c => c.Kind is BindingClauseKind.Lowers or BindingClauseKind.LowersLiteral
            or BindingClauseKind.LowersText or BindingClauseKind.LowersSequence or BindingClauseKind.LowersValue
            or BindingClauseKind.LowersReference or BindingClauseKind.LowersRepeat
            or BindingClauseKind.LowersTemplate or BindingClauseKind.LowersExpand);
        var declares = clauses.FirstOrDefault(c => c.Kind == BindingClauseKind.Declares && c.Target is not null);
        if (lowers is null && declares is null) return null;

        var elements = SyntaxCodeWriter.Elements(kind.Alternative?.Body ?? ((SyntaxRule)kind.Rule!).Body);
        string form = "None", target = "null", arguments = "new int[0]";
        int sequenceStride = 1;
        string extraArguments = "";
        string property = "null";
        string typeProperty = "null";
        string operationProperty = "null";
        string initializerProperty = "null";
        if (lowers is { Kind: BindingClauseKind.Lowers })
        {
            form = "Operation";
            target = lowers.Target is { } operationTarget ? CSharpText.Literal(operationTarget.Name) : "null";
            if (lowers.OperationProperty is { } operationName)
            {
                var owner = kind.Point is { } point ? model.Info(point.Module) : info;
                var ruleName = kind.Point?.Rule.Name ?? kind.Rule!.Name;
                operationProperty = owner.ClassPrefix(info) + "P_" + ruleName + "_" + operationName.Name;
            }
            arguments = Ints(lowers.Arguments.Select(a => BindingWriter.ChildIndex(elements, a.Name)));
            if (lowers.Arguments.Any(a => a.SequenceElementType is not null || a.InferSequence || a.OptionalElementType is not null || a.AsText || a.OptionalText))
            {
                var types = lowers.Arguments.Select(a => a.SequenceElementType is { } t ? CSharpText.Literal(t.Name) : "null");
                var strides = lowers.Arguments.Select(a =>
                {
                    if (a.SequenceElementType is null && !a.InferSequence) return 1;
                    var source = elements.OfType<LabeledExpr>().First(e => e.Label == a.Name).Inner;
                    return source is SeparatedListExpr ? 2 : 1;
                });
                var textArguments = lowers.Arguments.Select(a => a.AsText ? "true" : "false");
                var optionalTypes = lowers.Arguments.Select(a => a.OptionalElementType is { } t ? CSharpText.Literal(t.Name) : "null");
                extraArguments = $", 1, new string?[] {{ {string.Join(", ", types)} }}, {Ints(strides)}, new[] {{ {string.Join(", ", textArguments)} }}, "
                    + $"new string?[] {{ {string.Join(", ", optionalTypes)} }}";
                if (lowers.Arguments.Any(a => a.InferSequence))
                    extraArguments += $", argumentInferredSequences: new[] {{ {string.Join(", ", lowers.Arguments.Select(a => a.InferSequence ? "true" : "false"))} }}";
                if (lowers.Arguments.Any(a => a.OptionalText))
                    extraArguments += $", argumentOptionalTexts: new[] {{ {string.Join(", ", lowers.Arguments.Select(a => a.OptionalText ? "true" : "false"))} }}";
            }
        }
        else if (lowers is { Kind: BindingClauseKind.LowersTemplate or BindingClauseKind.LowersExpand })
        {
            // Template: the body and parameter list children; Expand: the argument list child.
            form = lowers.Kind == BindingClauseKind.LowersTemplate ? "Template" : "Expand";
            var list = lowers.Arguments[0].Name;
            arguments = Ints(lowers.Kind == BindingClauseKind.LowersTemplate
                ? new[] { BindingWriter.ChildIndex(elements, lowers.Field), BindingWriter.ChildIndex(elements, list) }
                : new[] { BindingWriter.ChildIndex(elements, list) });
            if (elements.OfType<LabeledExpr>().First(element => element.Label == list).Inner is SeparatedListExpr) sequenceStride = 2;
        }
        else if (lowers is not null)
        {
            form = lowers.Kind switch
            {
                BindingClauseKind.LowersText => "Text",
                BindingClauseKind.LowersSequence => "Sequence",
                BindingClauseKind.LowersValue => "Value",
                BindingClauseKind.LowersReference => "Reference",
                BindingClauseKind.LowersRepeat => "Repeat",
                _ => "Literal",
            };
            target = lowers.Target is { } loweringTarget ? CSharpText.Literal(loweringTarget.Name) : "null";
            arguments = lowers.Kind is BindingClauseKind.LowersValue or BindingClauseKind.LowersReference or BindingClauseKind.LowersRepeat
                ? "new int[0]" : Ints(new[] { BindingWriter.ChildIndex(elements, lowers.Field) });
            if (lowers.Kind == BindingClauseKind.LowersRepeat)
            {
                arguments = Ints(lowers.Arguments.Select(argument => BindingWriter.ChildIndex(elements, argument.Name)));
                var source = elements.OfType<LabeledExpr>().First(element => element.Label == lowers.Arguments[2].Name).Inner;
                if (source is SeparatedListExpr) sequenceStride = 2;
            }
            if (lowers.Kind == BindingClauseKind.LowersSequence)
            {
                var source = lowers.Field == "this"
                    ? kind.Alternative?.Body ?? ((SyntaxRule)kind.Rule!).Body
                    : elements.OfType<LabeledExpr>().First(element => element.Label == lowers.Field).Inner;
                if (source is SeparatedListExpr) sequenceStride = 2;
            }
            if (lowers.Kind is BindingClauseKind.LowersValue or BindingClauseKind.LowersReference)
            {
                var owner = kind.Point is { } point ? model.Info(point.Module) : info;
                var ruleName = kind.Point?.Rule.Name ?? kind.Rule!.Name;
                if (lowers.Kind == BindingClauseKind.LowersValue)
                    property = owner.ClassPrefix(info) + "P_" + ruleName + "_" + lowers.Field;
                if (lowers.TypeProperty is { } typeName)
                    typeProperty = owner.ClassPrefix(info) + "P_" + ruleName + "_" + typeName.Name;
                if (lowers.InitializerProperty is { } initializerName)
                    initializerProperty = owner.ClassPrefix(info) + "P_" + ruleName + "_" + initializerName.Name;
            }
        }

        string declaredType = "null";
        int declaredChild = -1;
        if (declares?.Target is { } type)
        {
            if (type.Name.IndexOf('.') >= 0) declaredType = CSharpText.Literal(type.Name);
            else declaredChild = BindingWriter.ChildIndex(elements, type.Name);
        }
        var stride = sequenceStride == 1 ? "" : $", {sequenceStride}";
        var propertyArgument = property == "null" ? "" : $", property: {property}";
        if (typeProperty != "null") propertyArgument += $", typeProperty: {typeProperty}";
        if (operationProperty != "null") propertyArgument += $", operationProperty: {operationProperty}";
        if (initializerProperty != "null") propertyArgument += $", initializerProperty: {initializerProperty}";
        if (lowers is { Kind: BindingClauseKind.Lowers, Optional: true })
            propertyArgument += ", optionalOperation: true";
        return $"new({kind.Local}, {FormType}.{form}, {target}, {arguments}, {declaredType}, {declaredChild}{stride}{extraArguments}{propertyArgument})";
    }

    static string Ints(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "new int[0]" : "new[] { " + string.Join(", ", list) + " }";
    }
}
