namespace Nitrogen.Grammar;

public sealed record GrammarFile(EquatableArray<ModuleDecl> Modules);

public sealed record ModuleDecl(
    string Name,
    EquatableArray<UsingDecl> Usings,
    EquatableArray<RuleDecl> Rules,
    EquatableArray<ExtendDecl> Extends,
    GrammarSpan Span,
    EquatableArray<SymbolsDecl> Symbols = default,
    EquatableArray<BuiltinDecl> Builtins = default,
    EquatableArray<SymbolPropertyDecl> SymbolProperties = default);

public sealed record UsingDecl(string Module, GrammarSpan Span);

public abstract record RuleDecl(string Name, GrammarSpan Span);

/// <summary>
/// A lexical rule: matches characters with no implicit trivia. A match whose text equals one of
/// <see cref="Except"/> fails (reserved words).
/// </summary>
public sealed record TokenRule(string Name, Expr Body, GrammarSpan Span, EquatableArray<LiteralExpr> Except = default)
    : RuleDecl(Name, Span);

/// <summary>A syntax rule: trivia is skipped between its elements. <see cref="Clauses"/>: binding (issue 237); <see cref="Semantics"/>: issue 239.</summary>
public sealed record SyntaxRule(string Name, Expr Body, GrammarSpan Span, EquatableArray<BindingClause> Clauses = default,
    SemanticsBlock? Semantics = null)
    : RuleDecl(Name, Span);

/// <summary>An extension point; other modules add alternatives with <see cref="ExtendDecl"/>.</summary>
/// <param name="Properties">Properties shared by all its alternatives (issue 239).</param>
public sealed record ExtensibleRule(string Name, EquatableArray<Alternative> Alternatives, GrammarSpan Span,
    EquatableArray<PropertyDecl> Properties = default)
    : RuleDecl(Name, Span);

/// <summary><c>extend syntax Target { ... }</c>; <see cref="Target"/> is the name as written.</summary>
public sealed record ExtendDecl(string Target, EquatableArray<Alternative> Alternatives, GrammarSpan Span);

public enum GrammarAssociativity
{
    Left,
    Right,
}

/// <param name="Name">The alternative's name; <c>""</c> when unnamed and not a single reference.</param>
/// <param name="NameIsImplicit">True when the name was taken from a single-reference body.</param>
/// <param name="Associativity">Null when not written; the generator treats that as left.</param>
/// <param name="Clauses">Binding clauses (issue 237).</param>
/// <param name="Semantics">The alternative's semantics block (issue 239).</param>
public sealed record Alternative(
    string Name,
    bool NameIsImplicit,
    Expr Body,
    int? Precedence,
    GrammarAssociativity? Associativity,
    GrammarSpan Span,
    EquatableArray<BindingClause> Clauses = default,
    SemanticsBlock? Semantics = null);

public abstract record Expr(GrammarSpan Span);

public sealed record SequenceExpr(EquatableArray<Expr> Items, GrammarSpan Span) : Expr(Span);

/// <summary>PEG ordered choice <c>a / b</c>.</summary>
public sealed record ChoiceExpr(EquatableArray<Expr> Alternatives, GrammarSpan Span) : Expr(Span);

public sealed record LabeledExpr(string Label, Expr Inner, GrammarSpan Span) : Expr(Span);

public enum RepeatKind
{
    Optional,
    ZeroOrMore,
    OneOrMore,
}

public sealed record RepeatExpr(RepeatKind Kind, Expr Inner, GrammarSpan Span) : Expr(Span);

/// <summary><c>(Item; Separator)*</c> or, with <see cref="AtLeastOne"/>, <c>(Item; Separator)+</c>.</summary>
public sealed record SeparatedListExpr(Expr Item, Expr Separator, bool AtLeastOne, GrammarSpan Span) : Expr(Span);

public sealed record LiteralExpr(string Value, GrammarSpan Span) : Expr(Span);

public readonly record struct CharRange(char First, char Last);

public sealed record CharClassExpr(EquatableArray<CharRange> Ranges, bool Negated, GrammarSpan Span) : Expr(Span);

public sealed record AnyCharExpr(GrammarSpan Span) : Expr(Span);

/// <summary>A rule reference, possibly qualified (<c>Module.Rule</c>).</summary>
public sealed record ReferenceExpr(string Name, GrammarSpan Span) : Expr(Span);

public enum PredicateKind
{
    Not,
    And,
}

/// <summary>PEG lookahead <c>!x</c> / <c>&amp;x</c>; consumes nothing.</summary>
public sealed record PredicateExpr(PredicateKind Kind, Expr Inner, GrammarSpan Span) : Expr(Span);

/// <summary>A name as written, with its span: a symbol kind or a built-in name (issue 237).</summary>
public sealed record NameDecl(string Name, GrammarSpan Span);

/// <summary><c>symbols { kind … }</c>: the symbol kinds a module declares (issue 237).</summary>
public sealed record SymbolsDecl(EquatableArray<NameDecl> Kinds, GrammarSpan Span);

/// <summary>
/// <c>builtin kind [in Rule] { name … }</c>: symbols every project of the language sees (issue 237);
/// with <see cref="Scope"/>, only inside the scopes that rule opens.
/// </summary>
public sealed record BuiltinDecl(NameDecl Kind, EquatableArray<NameDecl> Names, GrammarSpan Span, NameDecl? Scope = null);

public enum BindingClauseKind
{
    Declares,
    References,
    Scope,
    Dynamic,
    Lowers,
    LowersLiteral,
}

/// <summary>A binding clause after a syntax rule or an alternative (issue 237), or a declarative lowering clause (issue 251).</summary>
/// <param name="Kinds">Declares: one kind. References: one or more, in lookup order. Otherwise empty.</param>
/// <param name="Field">A top-level label of the rule's elements, or <c>this</c>; empty for Scope, Dynamic and Lowers.</param>
/// <param name="Optional"><c>references?</c>: counts only when it resolves, and then hides the references inside it.</param>
/// <param name="Export"><c>declares … export</c>: visible project-wide.</param>
/// <param name="Qualifier"><c>references … in K</c> (issue 239): resolve in the scope of the symbol the nearest enclosing reference of kind K names.</param>
/// <param name="Target">Declares: the <c>type</c> field label or qualified type name; Lowers: the operation ID; LowersLiteral: the literal's type (issue 251).</param>
/// <param name="Arguments">Lowers: the argument field labels, in parameter order (issue 251).</param>
public sealed record BindingClause(
    BindingClauseKind Kind,
    EquatableArray<NameDecl> Kinds,
    string Field,
    GrammarSpan FieldSpan,
    bool Optional,
    bool Export,
    GrammarSpan Span,
    NameDecl? Qualifier = null,
    NameDecl? Target = null,
    EquatableArray<NameDecl> Arguments = default);

/// <summary>C# text from a semantics block (issue 239), trimmed at the end; the grammar compiler never parses it.</summary>
public sealed record CodeText(string Text, GrammarSpan Span);

public enum PropertyDirection
{
    Out,
    In,
}

/// <summary><c>out|in [hover] [expected] Name : Type = Default;</c> (issue 239).</summary>
public sealed record PropertyDecl(
    PropertyDirection Direction, NameDecl Name, CodeText Type, CodeText Default, bool Hover, bool Expected, GrammarSpan Span);

/// <summary>
/// <c>symbol property Name for kind|(a | b) : Type = Default;</c>: a property of symbols (issue 239).
/// The default, used for built-ins and when the declaring rule assigns nothing, may read <c>kind</c> and <c>name</c>.
/// </summary>
public sealed record SymbolPropertyDecl(NameDecl Name, EquatableArray<NameDecl> Kinds, CodeText Type, CodeText Default, GrammarSpan Span);

public enum AssignTarget
{
    Self,
    Child,
    Symbol,
}

public abstract record SemanticStatement(GrammarSpan Span);

/// <summary><c>Prop = C#;</c>, <c>Child.Prop = C#;</c> or <c>symbol.Prop = C#;</c> (issue 239).</summary>
/// <param name="Child">The child's name for <see cref="AssignTarget.Child"/>; null otherwise.</param>
public sealed record AssignStatement(AssignTarget Target, NameDecl? Child, NameDecl Property, CodeText Value, GrammarSpan Span)
    : SemanticStatement(Span);

/// <summary><c>check [CODE] condition : message;</c> (issue 239). A message that is a string literal is interpolated.</summary>
public sealed record CheckStatement(NameDecl? Code, CodeText Condition, CodeText Message, GrammarSpan Span, NameDecl? At = null) : SemanticStatement(Span);

/// <summary>The <c>{ … }</c> after a syntax rule or an alternative (issue 239).</summary>
public sealed record SemanticsBlock(EquatableArray<PropertyDecl> Properties, EquatableArray<SemanticStatement> Statements, GrammarSpan Span);
