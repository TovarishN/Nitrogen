namespace Nitrogen.Semantic;

internal enum TypeLookup
{
    Found,
    Missing,
    Ambiguous,
}

/// <summary>A language's declarative rules (issue 251), resolved against its semantic catalog.</summary>
public sealed class DeclarativeLowering
{
    internal sealed record ResolvedRule(DeclarativeRule Rule, OperationSignature? Operation,
        SemanticType? LiteralType, SemanticType? DeclaredType, SemanticType?[] ArgumentSequenceTypes,
        SemanticType?[] ArgumentOptionalTypes);

    internal static readonly DeclarativeLowering Empty = new(new Dictionary<int, ResolvedRule>());

    readonly IReadOnlyDictionary<int, ResolvedRule> _rules;

    DeclarativeLowering(Dictionary<int, ResolvedRule> rules) => _rules = rules;

    public bool IsEmpty => _rules.Count == 0;

    internal ResolvedRule? RuleFor(int kind) => _rules.TryGetValue(kind, out var rule) ? rule : null;

    internal static SemanticType? SequenceElement(SemanticType type) =>
        type.Id == "Core.Sequence" && type.Arguments.Count == 1 ? type.Arguments[0] : null;

    /// <summary>An exact qualified ID, or an unqualified name that exactly one catalog type has.</summary>
    internal static TypeLookup TryResolveType(SemanticCatalog catalog, string name, out SemanticType? type)
    {
        if (catalog.Types.TryGetValue(name, out type)) return TypeLookup.Found;
        type = null;
        if (name.Contains('.')) return TypeLookup.Missing;
        var matches = catalog.Types.Values.Where(candidate => candidate.Name == name).ToArray();
        if (matches.Length == 1)
        {
            type = matches[0];
            return TypeLookup.Found;
        }
        return matches.Length == 0 ? TypeLookup.Missing : TypeLookup.Ambiguous;
    }

    internal static bool TryResolve(IReadOnlyList<SyntaxModule> modules, SemanticCatalog catalog,
        out DeclarativeLowering? lowering, out SemanticModule? registrations,
        out IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        var errors = new List<CompositionDiagnostic>();
        var rules = new Dictionary<int, ResolvedRule>();
        var lowerers = new List<LoweringRegistration>();
        foreach (var module in modules)
            foreach (var rule in module.DeclarativeRules)
            {
                int kind = module.KindBase | rule.LocalKind;
                string where = $"'{module.Name}.{module.GetKindName(rule.LocalKind)}'";
                OperationSignature? operation = null;
                SemanticType? literal = null, declared = null;
                var argumentSequenceTypes = new SemanticType?[rule.Arguments.Count];
                var argumentOptionalTypes = new SemanticType?[rule.Arguments.Count];
                if (rule.Form == DeclarativeForm.Operation)
                {
                    if (rule.Target is not null && !catalog.Operations.TryGetValue(rule.Target, out operation))
                        errors.Add(new CompositionDiagnostic("NM0008", [module.Name],
                            $"{where} lowers to operation '{rule.Target}', which the semantic catalog does not export."));
                    else if (operation is not null && operation.Inputs.Count != rule.Arguments.Count)
                    {
                        errors.Add(new CompositionDiagnostic("NM0010", [module.Name],
                            $"{where} passes {rule.Arguments.Count} arguments to '{rule.Target}', which takes {operation.Inputs.Count}."));
                        operation = null;
                    }
                    for (int i = 0; i < rule.Arguments.Count; i++)
                        {
                            if (rule.ArgumentTexts[i] && operation is not null &&
                                !operation.Inputs[i].Equals(SemanticTypes.Text))
                            {
                                errors.Add(new CompositionDiagnostic("NM0011", [module.Name],
                                    $"{where} passes text to '{operation.Id}', which needs {operation.Inputs[i]}."));
                                continue;
                            }
                            var typeName = rule.ArgumentSequenceTypes[i];
                            var optionalName = rule.ArgumentOptionalTypes[i];
                            if (rule.ArgumentInferredSequences[i])
                            {
                                if (operation is not null && SequenceElement(operation.Inputs[i]) is null)
                                    errors.Add(new CompositionDiagnostic("NM0011", [module.Name],
                                        $"{where} infers a sequence from '{operation.Id}', which needs {operation.Inputs[i]}."));
                                continue;
                            }
                            if (typeName is null && optionalName is null) continue;
                            var elementName = typeName ?? optionalName!;
                            if (TryResolveType(catalog, elementName, out var itemType) != TypeLookup.Found)
                                errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                                    $"{where} has argument element type '{elementName}', which the semantic catalog does not export."));
                            else if (operation is not null && !operation.Inputs[i].Equals(typeName is null
                                ? SemanticTypes.OptionalOf(itemType!) : SemanticTypes.SequenceOf(itemType!)))
                                errors.Add(new CompositionDiagnostic("NM0011", [module.Name],
                                    $"{where} passes {(typeName is null ? "an optional" : "a sequence")} of {itemType} to '{operation.Id}', which needs {operation.Inputs[i]}."));
                            else if (typeName is null) argumentOptionalTypes[i] = itemType;
                            else argumentSequenceTypes[i] = itemType;
                        }
                }
                else if (rule.Form is DeclarativeForm.Literal or DeclarativeForm.Sequence or DeclarativeForm.Value or DeclarativeForm.Repeat &&
                         rule.Target is not null &&
                         TryResolveType(catalog, rule.Target!, out literal) != TypeLookup.Found)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} has lowering type '{rule.Target}', which the semantic catalog does not export."));
                else if (rule.Form == DeclarativeForm.Text && rule.Target != SemanticTypes.Text.Id)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} text lowering requires '{SemanticTypes.Text.Id}', not '{rule.Target}'."));
                if (rule.DeclaredType is { } fixedType && TryResolveType(catalog, fixedType, out declared) != TypeLookup.Found)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} declares type '{fixedType}', which the semantic catalog does not export."));

                rules[kind] = new ResolvedRule(rule, operation, literal, declared,
                    argumentSequenceTypes, argumentOptionalTypes);
                if (rule.Form == DeclarativeForm.Expand)
                    lowerers.Add(new LoweringRegistration(kind, "<expand>",
                        (context, node) => context.File.DeclarativeTypes.LowerRoot(context, node), DynamicOperation: true));
                // An optional computed operation is a root too; where it is missing, the node lowers to nothing.
                else if (operation is not null || rule.OperationProperty is not null)
                    lowerers.Add(new LoweringRegistration(kind, operation?.Id ?? "<computed>",
                        (context, node) => context.File.DeclarativeTypes.LowerRoot(context, node),
                        DynamicOperation: rule.OperationProperty is not null));
            }

        diagnostics = errors;
        if (errors.Count > 0)
        {
            lowering = null;
            registrations = null;
            return false;
        }
        lowering = rules.Count == 0 ? Empty : new DeclarativeLowering(rules);
        registrations = lowerers.Count == 0 ? null : new SemanticModule("Nitrogen.Declarative", [], [], [], lowerers);
        return true;
    }
}
