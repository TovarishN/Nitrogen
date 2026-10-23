namespace Nitrogen.Semantics;

/// <summary>What one node kind computes (issue 239), emitted from its semantics block.</summary>
public sealed class SemanticsRule(SemanticsOut[] outs, SemanticsIn[] ins, SemanticsSymbol[] symbols, SemanticsCheck[] checks)
{
    public IReadOnlyList<SemanticsOut> Outs { get; } = outs;

    public IReadOnlyList<SemanticsIn> Ins { get; } = ins;

    public IReadOnlyList<SemanticsSymbol> Symbols { get; } = symbols;

    public IReadOnlyList<SemanticsCheck> Checks { get; } = checks;
}

/// <summary>An <c>out</c> property the node defines.</summary>
public abstract class SemanticsOut(Property property)
{
    public Property Property { get; } = property;

    public static SemanticsOut<T> Of<T>(Property<T> property, Func<FileSemantics, int, T> compute) => new(property, compute);
}

public sealed class SemanticsOut<T>(Property<T> property, Func<FileSemantics, int, T> compute) : SemanticsOut(property)
{
    public T Compute(FileSemantics semantics, int node) => compute(semantics, node);
}

/// <summary>
/// An <c>in</c> property the node assigns to a child. <see cref="Child"/> is the element's index among
/// the node's children, and <see cref="Inner"/> the item's index inside a group element, or -1. A list
/// element assigns every item. The value is computed on the parent.
/// </summary>
public abstract class SemanticsIn(Property property, int child, int inner)
{
    public Property Property { get; } = property;

    public int Child { get; } = child;

    public int Inner { get; } = inner;

    public static SemanticsIn<T> Of<T>(Property<T> property, int child, int inner, Func<FileSemantics, int, T> compute) =>
        new(property, child, inner, compute);
}

public sealed class SemanticsIn<T>(Property<T> property, int child, int inner, Func<FileSemantics, int, T> compute)
    : SemanticsIn(property, child, inner)
{
    public T Compute(FileSemantics semantics, int parent) => compute(semantics, parent);
}

/// <summary>A property of the symbol the node declares.</summary>
public abstract class SemanticsSymbol(SymbolProperty property)
{
    public SymbolProperty Property { get; } = property;

    public static SemanticsSymbol<T> Of<T>(SymbolProperty<T> property, Func<FileSemantics, int, T> compute) => new(property, compute);
}

public sealed class SemanticsSymbol<T>(SymbolProperty<T> property, Func<FileSemantics, int, T> compute) : SemanticsSymbol(property)
{
    public T Compute(FileSemantics semantics, int node) => compute(semantics, node);
}

/// <summary>A <c>check</c>: when the condition is false, the message is reported at the node under the code.</summary>
public sealed class SemanticsCheck(string code, Func<FileSemantics, int, bool> condition, Func<FileSemantics, int, string> message,
    Func<FileSemantics, int, TextSpan>? at = null, DiagnosticSeverity severity = DiagnosticSeverity.Error,
    SyntaxDiagnosticPattern? pattern = null)
{
    public string Code { get; } = code;
    public DiagnosticSeverity Severity { get; } = severity;
    public SyntaxDiagnosticPattern? Pattern { get; } = pattern;

    readonly Func<FileSemantics, int, SemanticPatternCaptures, string>? _patternMessage;

    /// <summary>A diagnostic whose message receives the successful pattern's typed captures.</summary>
    public SemanticsCheck(string code, SyntaxDiagnosticPattern pattern,
        Func<FileSemantics, int, SemanticPatternCaptures, string> message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
        : this(code, static (_, _) => true, static (_, _) => "", severity: severity, pattern: pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(message);
        _patternMessage = message;
    }

    QuoteMatchResult Match(FileSemantics semantics, int node)
    {
        var compiled = semantics.Tree.Language!.DiagnosticPattern(Pattern!);
        if (!compiled.Success) throw new InvalidOperationException(string.Join("; ", compiled.Diagnostics));
        var match = compiled.Pattern!.Match(new(semantics.Tree, node));
        if (match.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", match.Diagnostics));
        return match;
    }

    public bool Holds(FileSemantics semantics, int node)
        => Pattern is null ? condition(semantics, node) : !Match(semantics, node).Matched;

    public string Message(FileSemantics semantics, int node)
    {
        if (_patternMessage is null) return message(semantics, node);
        var match = Match(semantics, node);
        if (!match.Matched) throw new InvalidOperationException("A pattern message requires a successful match.");
        return _patternMessage(semantics, node, new(semantics, match.Captures));
    }

    internal SemanticDiagnostic? Evaluate(FileSemantics semantics, int node)
    {
        string text;
        TextSpan span;
        if (Pattern is null)
        {
            if (condition(semantics, node)) return null;
            span = Span(semantics, node);
            text = message(semantics, node);
        }
        else
        {
            var match = Match(semantics, node);
            if (!match.Matched) return null;
            span = Span(semantics, node);
            text = _patternMessage is null ? message(semantics, node)
                : _patternMessage(semantics, node, new(semantics, match.Captures));
        }
        return new SemanticDiagnostic(Code, span, text) { Severity = Severity };
    }

    /// <summary>Where the diagnostic sits: the child <c>at</c> names, else the node (issue 241).</summary>
    public TextSpan Span(FileSemantics semantics, int node) => at is null ? semantics.Tree.Span(node) : at(semantics, node);
}
