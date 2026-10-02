namespace Nitrogen.Binding;

/// <summary>A file-scope name emitted by checked lowering, linked to its declaring source node.</summary>
public sealed record DerivedDeclaration(string Name, int Node, TextSpan NameSpan, bool Export = false);
