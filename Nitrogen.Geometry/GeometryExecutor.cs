using Nitrogen.Semantic;

namespace Nitrogen.Geometry;

public sealed class GeometryExecutionResult
{
    public GeometryExecutionResult(GeometryMesh? mesh, IEnumerable<SourceOrigin> origins,
        IEnumerable<ExecutionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Mesh = mesh;
        Origins = Array.AsReadOnly(origins.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public GeometryMesh? Mesh { get; }
    public IReadOnlyList<SourceOrigin> Origins { get; }
    public IReadOnlyList<ExecutionDiagnostic> Diagnostics { get; }
}

public static class GeometryExecutor
{
    public static GeometryExecutionResult Execute(HirNode root, ModuleComposition composition)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(composition);
        var origin = root.Origins[0];
        if (root is not HirOperation operation || !operation.Signature.Equals(BoxMeshModule.BoxSignature) ||
            !composition.Language.SemanticCatalog.Operations.TryGetValue(BoxMeshModule.BoxSignature.Id, out var exported) ||
            !exported.Equals(operation.Signature))
            return Failure("GX0001", origin, root.Origins);
        if (!composition.HostBindings.TryGetValue(BoxMeshModule.BoxSignature.Id, out var binding) ||
            !binding.Signature.Equals(operation.Signature) ||
            binding.Handler.GetType() != typeof(Func<IReadOnlyList<ExecutionValue>, GeometryMesh>))
            return Failure("GX0002", origin, root.Origins);
        var handler = (Func<IReadOnlyList<ExecutionValue>, GeometryMesh>)binding.Handler;

        var arguments = new ExecutionValue[3];
        for (var i = 0; i < arguments.Length; i++)
        {
            if (operation.Arguments[i] is not HirConstant constant ||
                !constant.Type.Equals(SemanticTypes.Scalar) || !float.IsFinite(constant.Value))
                return Failure("GX0003", operation.Arguments[i].Origins[0], root.Origins);
            arguments[i] = new ExecutionValue(SemanticTypes.Scalar, constant.Value);
        }

        try
        {
            var mesh = handler(Array.AsReadOnly(arguments));
            return mesh is null ? Failure("GX0004", origin, root.Origins) :
                new GeometryExecutionResult(mesh, root.Origins, []);
        }
        catch (Exception)
        {
            return Failure("GX0004", origin, root.Origins);
        }
    }

    static GeometryExecutionResult Failure(string code, SourceOrigin origin,
        IReadOnlyList<SourceOrigin> origins) => new(null, origins,
        [new ExecutionDiagnostic(code, origin, code switch
        {
            "GX0001" => "The root does not match the exported box mesh operation.",
            "GX0002" => "A matching typed box mesh host binding is required.",
            "GX0003" => "Box mesh dimensions must be finite scalar constants.",
            _ => "The box mesh host failed to produce a valid mesh."
        })]);
}
