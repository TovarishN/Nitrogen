using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ExecutionValueTests
{
    static readonly SourceOrigin Origin = new("a.skill", Guid.NewGuid(), 7, new TextSpan(12, 5));

    [Fact]
    public void Finite_scalar_and_angle_values_preserve_structural_type_and_number()
    {
        var scalar = new ExecutionValue(SemanticType.Named("Core", "Scalar"), -2.5f);
        var angle = new ExecutionValue(SemanticType.Named("Units", "Angle"), 0.5f);

        Assert.Equal(SemanticTypes.Scalar, scalar.Type);
        Assert.Equal(-2.5f, scalar.Number);
        Assert.Equal(SemanticTypes.Angle, angle.Type);
        Assert.Equal(0.5f, angle.Number);
    }

    [Fact]
    public void Unsupported_or_nonfinite_values_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ExecutionValue(null!, 1));
        Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Bool, 1));
        Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Error, 1));
        Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Angle, float.NaN));
        Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Scalar, float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Scalar, float.NegativeInfinity));
    }

    [Fact]
    public void Result_copies_origins_and_diagnostics()
    {
        var origins = new List<SourceOrigin> { Origin };
        var diagnostic = new ExecutionDiagnostic("NE0001", Origin, "bad value");
        var diagnostics = new List<ExecutionDiagnostic> { diagnostic };
        var result = new ExecutionResult(null, origins, diagnostics);

        origins.Clear();
        diagnostics.Clear();

        Assert.Equal([Origin], result.Origins);
        Assert.Equal([diagnostic], result.Diagnostics);
        Assert.Throws<NotSupportedException>(() => ((IList<SourceOrigin>)result.Origins).Clear());
    }
}
