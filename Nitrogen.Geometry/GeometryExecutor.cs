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

/// <summary>
/// Projects a <c>Geometry.Mesh</c> root through <see cref="HirProjector"/> with the composition's box mesh
/// host. The whole tree is preflighted before the host runs; projection codes map onto stable GX codes.
/// </summary>
public static class GeometryExecutor
{
    public static GeometryExecutionResult Execute(HirNode root, ModuleComposition composition)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(composition);
        var origin = root.Origins[0];
        var catalog = composition.Language.SemanticCatalog;
        if (!root.Type.Equals(BoxMeshModule.MeshType) ||
            !catalog.Operations.TryGetValue(BoxMeshModule.BoxSignature.Id, out var exported) ||
            !exported.Equals(BoxMeshModule.BoxSignature))
            return Failure("GX0001", origin, "The root does not match the exported box mesh operation.", root.Origins);
        if (!composition.HostBindings.TryGetValue(BoxMeshModule.BoxSignature.Id, out var binding) ||
            !binding.Signature.Equals(BoxMeshModule.BoxSignature) ||
            binding.Handler.GetType() != typeof(Func<IReadOnlyList<ExecutionValue>, GeometryMesh>))
            return Failure("GX0002", origin, "A matching typed box mesh host binding is required.", root.Origins);
        var host = (Func<IReadOnlyList<ExecutionValue>, GeometryMesh>)binding.Handler;

        var registry = new ProjectionRegistry(catalog, [new ProjectionHandler(BoxMeshModule.BoxSignature, values =>
            new ProjectedValue(BoxMeshModule.MeshType,
                host(Array.AsReadOnly(values.Select(value => new ExecutionValue(SemanticTypes.Scalar, (float)value.Value)).ToArray())) ??
                throw new InvalidOperationException("The box mesh host returned no mesh.")))]);
        var projected = HirProjector.Project(root, registry);
        if (projected.Value?.Value is GeometryMesh mesh)
            return new GeometryExecutionResult(mesh, root.Origins, []);
        return new GeometryExecutionResult(null, root.Origins, projected.Diagnostics.Select(diagnostic =>
            diagnostic with { Code = Code(diagnostic.Code) }));
    }

    /// <summary>The GX code of a projection diagnostic.</summary>
    static string Code(string projection) => projection switch
    {
        "NP0001" or "NP0003" => "GX0003", // a nonfinite or missing dimension
        "NP0002" => "GX0002",             // an operation without a host
        "NP0005" => "GX0004",             // the host threw, returned nothing, or built an invalid mesh
        _ => "GX0001",                    // a tree that does not match the catalog
    };

    static GeometryExecutionResult Failure(string code, SourceOrigin origin, string message,
        IReadOnlyList<SourceOrigin> origins) => new(null, origins, [new ExecutionDiagnostic(code, origin, message)]);
}
