using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeLoweringTests
{
    static void Lower(string source, Action<FileSemantics, LoweringResult> assert) =>
        DeclarativeTypesTests.Run(source, file => assert(file, HirLowering.Lower(file, file.Tree.Language!.SemanticCatalog)));

    static string Text(string source, HirNode node) =>
        source.Substring(node.Origins[0].Span.Start, node.Origins[0].Span.Length);

    [Fact]
    public void Operation_lowers_to_typed_constants_with_origins()
    {
        const string source = "add(1, 2);";
        Lower(source, (_, lowered) =>
        {
            Assert.Empty(lowered.Diagnostics);
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            Assert.Equal(DeclarativeCompositionTests.Add, root.Signature);
            Assert.Equal("add(1, 2)", Text(source, root));
            Assert.Equal([1f, 2f], root.Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value));
            Assert.Equal(["1", "2"], root.Arguments.Select(argument => Text(source, argument)));
            Assert.All(root.Arguments, argument => Assert.Equal(SemanticTypes.Scalar, argument.Type));
        });
    }

    [Fact]
    public void Nested_operation_is_one_root()
    {
        Lower("add(add(1, 2), 3);", (_, lowered) =>
        {
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            Assert.IsType<HirOperation>(root.Arguments[0]);
        });
    }

    [Fact]
    public void Reference_lowers_to_a_typed_symbol_ref()
    {
        Lower("input x : Scalar; add(x, 1);", (_, lowered) =>
        {
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            var reference = Assert.IsType<HirSymbolRef>(root.Arguments[0]);
            Assert.Equal(SemanticTypes.Scalar, reference.Type);
            Assert.Equal("x", reference.Symbol.Binding.Name);
            Assert.Equal("Lowered", reference.Symbol.Module);
        });
    }

    [Fact]
    public void Pass_through_lowers_its_typed_child()
    {
        const string source = "add(wrap(1), 2);";
        Lower(source, (_, lowered) =>
        {
            var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
            Assert.Equal("1", Text(source, Assert.IsType<HirConstant>(root.Arguments[0])));
        });
    }

    [Fact]
    public void Type_errors_prevent_lowering()
    {
        Lower("turn(1);", (_, lowered) =>
        {
            Assert.Empty(lowered.Roots);
            Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
        });
    }

    [Fact]
    public void Lowered_program_evaluates_through_a_bound_host()
    {
        Lower("input x : Scalar; add(x, 1);", (file, lowered) =>
        {
            var root = Assert.Single(lowered.Roots);
            var catalog = file.Tree.Language!.SemanticCatalog;
            var registry = HostOperationRegistry.Bind(catalog, [new HostOperationBinding(DeclarativeCompositionTests.Add,
                (Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)(arguments =>
                    new ExecutionValue(SemanticTypes.Scalar, arguments[0].Number + arguments[1].Number)))]);
            var x = ((HirSymbolRef)((HirOperation)root).Arguments[0]).Symbol.Binding;
            var result = HirEvaluator.Evaluate(root, registry,
                new Dictionary<Symbol, ExecutionValue> { [x] = new(SemanticTypes.Scalar, 41f) });
            Assert.Empty(result.Diagnostics);
            Assert.Equal(42f, result.Value!.Number);
        });
    }
}
