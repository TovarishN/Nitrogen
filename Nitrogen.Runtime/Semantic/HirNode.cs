namespace Nitrogen.Semantic;

/// <summary>Stable coordinates in one parsed document snapshot.</summary>
public readonly record struct SourceOrigin(string Path, Guid SnapshotId, int Node, TextSpan Span);

public abstract class HirNode
{
    protected HirNode(SemanticType type, IEnumerable<SourceOrigin> origins)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        ArgumentNullException.ThrowIfNull(origins);
        var copied = origins.ToArray();
        if (copied.Length == 0) throw new ArgumentException("A HIR node requires an origin.", nameof(origins));
        Origins = Array.AsReadOnly(copied);
    }

    public SemanticType Type { get; }
    public IReadOnlyList<SourceOrigin> Origins { get; }
}

public sealed class HirConstant : HirNode
{
    public HirConstant(float value, SemanticType type, SourceOrigin origin) : this(value, type, [origin]) { }
    internal HirConstant(float value, SemanticType type, IReadOnlyList<SourceOrigin> origins) : base(type, origins) => Value = value;
    public float Value { get; }
}

public sealed class HirText : HirNode
{
    public HirText(string value, SourceOrigin origin) : this(value, [origin]) { }
    internal HirText(string value, IReadOnlyList<SourceOrigin> origins)
        : base(SemanticTypes.Text, origins) => Value = value ?? throw new ArgumentNullException(nameof(value));

    public string Value { get; }
}

public sealed class HirSequence : HirNode
{
    public HirSequence(SemanticType elementType, IReadOnlyList<HirNode> items, SourceOrigin origin)
        : this(elementType, items, [origin]) { }

    internal HirSequence(SemanticType elementType, IReadOnlyList<HirNode> items,
        IReadOnlyList<SourceOrigin> origins) : base(SemanticTypes.SequenceOf(elementType), origins)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copied = items.ToArray();
        if (copied.Any(item => item is null || !item.Type.Equals(elementType)))
            throw new ArgumentException("Every sequence item must have the element type.", nameof(items));
        Items = Array.AsReadOnly(copied);
        ElementType = elementType;
    }

    public SemanticType ElementType { get; }
    public IReadOnlyList<HirNode> Items { get; }
}

public sealed class HirOptional : HirNode
{
    public HirOptional(SemanticType elementType, HirNode? value, SourceOrigin origin)
        : this(elementType, value, [origin]) { }

    internal HirOptional(SemanticType elementType, HirNode? value, IReadOnlyList<SourceOrigin> origins)
        : base(SemanticTypes.OptionalOf(elementType), origins)
    {
        if (value is not null && !value.Type.Equals(elementType))
            throw new ArgumentException("The optional value must have the element type.", nameof(value));
        ElementType = elementType;
        Value = value;
    }

    public SemanticType ElementType { get; }
    public HirNode? Value { get; }
}

/// <summary>Evaluates a typed sequence template with scalar indices, retaining one sequence per iteration.</summary>
public sealed class HirRepeat : HirNode
{
    public HirRepeat(HirNode count, SemanticSymbol iterator, HirSequence template, SourceOrigin origin)
        : this(count, iterator, template, [origin]) { }

    internal HirRepeat(HirNode count, SemanticSymbol iterator, HirSequence template, IReadOnlyList<SourceOrigin> origins)
        : base(SemanticTypes.SequenceOf(template.Type), origins)
    {
        if (!count.Type.Equals(SemanticTypes.Scalar) || iterator.Type?.Equals(SemanticTypes.Scalar) != true)
            throw new ArgumentException("Repeat count and iterator must be Core.Scalar.");
        Count = count;
        Iterator = iterator;
        Template = template;
    }

    public HirNode Count { get; }
    public SemanticSymbol Iterator { get; }
    public HirSequence Template { get; }
}

public sealed class HirSymbolRef : HirNode
{
    public HirSymbolRef(SemanticSymbol symbol, SourceOrigin origin) : this(symbol, [origin]) { }

    internal HirSymbolRef(SemanticSymbol symbol, IReadOnlyList<SourceOrigin> origins)
        : base(symbol?.Type ?? throw new ArgumentException("A HIR symbol requires a type.", nameof(symbol)), origins)
    {
        Symbol = symbol;
    }

    public SemanticSymbol Symbol { get; }
}

public sealed class HirOperation : HirNode
{
    public HirOperation(OperationSignature signature, IReadOnlyList<HirNode> arguments,
        IReadOnlyList<SourceOrigin> origins)
        : base(signature?.Result ?? throw new ArgumentNullException(nameof(signature)), origins)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != signature.Inputs.Count)
            throw new ArgumentException("Operation argument count does not match its signature.", nameof(arguments));
        var copied = arguments.ToArray();
        for (var i = 0; i < copied.Length; i++)
            if (copied[i] is null || !copied[i].Type.Equals(signature.Inputs[i]))
                throw new ArgumentException($"Operation argument {i} does not match its signature.", nameof(arguments));
        Signature = signature;
        Arguments = Array.AsReadOnly(copied);
    }

    public OperationSignature Signature { get; }
    public IReadOnlyList<HirNode> Arguments { get; }
}

public static class HirTraversal
{
    public static IEnumerable<HirNode> PreOrder(HirNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        yield return root;
        if (root is HirOperation operation)
            foreach (var child in operation.Arguments)
                foreach (var descendant in PreOrder(child))
                    yield return descendant;
        else if (root is HirSequence sequence)
            foreach (var child in sequence.Items)
                foreach (var descendant in PreOrder(child))
                    yield return descendant;
        else if (root is HirOptional { Value: { } value })
            foreach (var descendant in PreOrder(value))
                yield return descendant;
        else if (root is HirRepeat repeat)
            foreach (var child in new HirNode[] { repeat.Count, repeat.Template })
                foreach (var descendant in PreOrder(child)) yield return descendant;
    }

    public static HirNode Rewrite(HirNode root, Func<HirNode, HirNode> transform)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(transform);
        var current = root;
        if (root is HirRepeat repeat)
        {
            var count = Rewrite(repeat.Count, transform);
            var template = (HirSequence)Rewrite(repeat.Template, transform);
            if (!ReferenceEquals(count, repeat.Count) || !ReferenceEquals(template, repeat.Template))
                current = new HirRepeat(count, repeat.Iterator, template,
                    repeat.Origins.Concat(count.Origins).Concat(template.Origins).Distinct().ToArray());
        }
        if (root is HirOperation operation)
        {
            var rewritten = operation.Arguments.Select(child => Rewrite(child, transform)).ToArray();
            if (rewritten.Where((child, i) => !ReferenceEquals(child, operation.Arguments[i])).Any())
            {
                var origins = operation.Origins.Concat(rewritten.SelectMany(child => child.Origins))
                    .Distinct().ToArray();
                current = new HirOperation(operation.Signature, rewritten, origins);
            }
        }
        else if (root is HirSequence sequence)
        {
            var rewritten = sequence.Items.Select(child => Rewrite(child, transform)).ToArray();
            if (rewritten.Where((child, i) => !ReferenceEquals(child, sequence.Items[i])).Any())
            {
                var origins = sequence.Origins.Concat(rewritten.SelectMany(child => child.Origins))
                    .Distinct().ToArray();
                current = new HirSequence(sequence.ElementType, rewritten, origins);
            }
        }
        else if (root is HirOptional { Value: { } value } optional)
        {
            var rewritten = Rewrite(value, transform);
            if (!ReferenceEquals(rewritten, value))
                current = new HirOptional(optional.ElementType, rewritten,
                    optional.Origins.Concat(rewritten.Origins).Distinct().ToArray());
        }
        var replacement = transform(current) ?? throw new ArgumentException("A HIR rewrite cannot return null.", nameof(transform));
        if (ReferenceEquals(replacement, current)) return current;
        if (!replacement.Type.Equals(root.Type))
            throw new ArgumentException("A HIR rewrite cannot change the node type.", nameof(transform));
        var combined = root.Origins.Concat(replacement.Origins).Distinct().ToArray();
        return replacement switch
        {
            HirConstant constant => new HirConstant(constant.Value, constant.Type, combined),
            HirText text => new HirText(text.Value, combined),
            HirSequence sequence => new HirSequence(sequence.ElementType, sequence.Items, combined),
            HirOptional optional => new HirOptional(optional.ElementType, optional.Value, combined),
            HirRepeat repeated => new HirRepeat(repeated.Count, repeated.Iterator, repeated.Template, combined),
            HirSymbolRef symbol => new HirSymbolRef(symbol.Symbol, combined),
            HirOperation rewritten => new HirOperation(rewritten.Signature, rewritten.Arguments, combined),
            _ => throw new ArgumentException("Unknown HIR replacement node.", nameof(transform)),
        };
    }
}
