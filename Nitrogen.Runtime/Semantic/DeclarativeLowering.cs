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
        SemanticType? LiteralType, SemanticType? DeclaredType);

    internal static readonly DeclarativeLowering Empty = new(new Dictionary<int, ResolvedRule>());

    readonly IReadOnlyDictionary<int, ResolvedRule> _rules;

    DeclarativeLowering(Dictionary<int, ResolvedRule> rules) => _rules = rules;

    public bool IsEmpty => _rules.Count == 0;

    internal ResolvedRule? RuleFor(int kind) => _rules.TryGetValue(kind, out var rule) ? rule : null;

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
                if (rule.Form == DeclarativeForm.Operation)
                {
                    if (!catalog.Operations.TryGetValue(rule.Target!, out operation))
                        errors.Add(new CompositionDiagnostic("NM0008", [module.Name],
                            $"{where} lowers to operation '{rule.Target}', which the semantic catalog does not export."));
                    else if (operation.Inputs.Count != rule.Arguments.Count)
                    {
                        errors.Add(new CompositionDiagnostic("NM0010", [module.Name],
                            $"{where} passes {rule.Arguments.Count} arguments to '{rule.Target}', which takes {operation.Inputs.Count}."));
                        operation = null;
                    }
                }
                else if (rule.Form == DeclarativeForm.Literal &&
                         TryResolveType(catalog, rule.Target!, out literal) != TypeLookup.Found)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} has literal type '{rule.Target}', which the semantic catalog does not export."));
                if (rule.DeclaredType is { } fixedType && TryResolveType(catalog, fixedType, out declared) != TypeLookup.Found)
                    errors.Add(new CompositionDiagnostic("NM0009", [module.Name],
                        $"{where} declares type '{fixedType}', which the semantic catalog does not export."));

                rules[kind] = new ResolvedRule(rule, operation, literal, declared);
                if (operation is not null)
                    lowerers.Add(new LoweringRegistration(kind, operation.Id,
                        (context, node) => context.File.DeclarativeTypes.LowerRoot(context, node)));
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
