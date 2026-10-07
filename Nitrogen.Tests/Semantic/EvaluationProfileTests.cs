using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>An evaluation profile builds its handlers from the catalog it is bound to, and the registry checks them.</summary>
public class EvaluationProfileTests
{
    static readonly OperationSignature Double = new("Twice.Double", SemanticTypes.Scalar, SemanticTypes.Scalar);

    static readonly SemanticCatalog Catalog =
        SemanticCatalog.Compose([new SemanticModule("Twice", ["Core"], [], [Double])], out _)!;

    static EvaluationProfile Profile(Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers) =>
        new(new HashSet<int> { 7 }, handlers, _ => null, value => $"<{value.Value}>");

    static ProjectionHandler Doubling(OperationSignature signature) =>
        new(signature, arguments => new ProjectedValue(SemanticTypes.Scalar, 2 * (float)arguments[0].Value));

    [Fact]
    public void Bind_builds_the_handlers_from_the_catalog()
    {
        var bound = Profile(catalog => [Doubling(catalog.Operations["Twice.Double"])]).Bind(Catalog);

        Assert.Same(Catalog, bound.Registry.Catalog);
        Assert.Equal(new[] { 7 }, bound.Profile.StatementKinds);
    }

    [Fact]
    public void Bind_rejects_an_operation_the_catalog_does_not_export()
    {
        var profile = Profile(_ => [Doubling(new OperationSignature("Twice.Triple", SemanticTypes.Scalar, SemanticTypes.Scalar))]);

        Assert.Throws<ArgumentException>(() => profile.Bind(Catalog));
    }

    [Fact]
    public void Bind_rejects_two_handlers_for_one_operation()
    {
        var profile = Profile(catalog => [Doubling(catalog.Operations["Twice.Double"]), Doubling(catalog.Operations["Twice.Double"])]);

        Assert.Throws<ArgumentException>(() => profile.Bind(Catalog));
    }

    [Fact]
    public void The_original_constructor_does_not_read_the_clock()
    {
        Assert.False(Profile(_ => []).ReadsClock);
    }

    [Fact]
    public void A_profile_can_declare_that_it_reads_the_clock()
    {
        var profile = new EvaluationProfile(new HashSet<int> { 7 }, _ => [],
            (_, context) => new ProjectedValue(SemanticTypes.Text, context.Today.ToString("yyyy-MM-dd")), value => (string)value.Value,
            readsClock: true);

        Assert.True(profile.ReadsClock);
        Assert.Equal(new DateOnly(2026, 10, 7),
            new EvaluationContext(new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.FromHours(3))).Today);
    }

    [Fact]
    public void Format_delegates_to_the_profile()
    {
        Assert.Equal("<3>", Profile(_ => []).Format(new ProjectedValue(SemanticTypes.Scalar, 3f)));
    }
}
