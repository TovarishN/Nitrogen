using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

public sealed class DeclarativeTypes
{
    internal DeclarativeTypes(FileSemantics file, DeclarativeLowering lowering, SemanticCatalog catalog) { }

    internal HirNode? LowerRoot(LoweringContext context, int node) => null;
}
