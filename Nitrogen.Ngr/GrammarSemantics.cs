using Nitrogen.Semantic;

namespace Nitrogen.Ngr;

/// <summary>
/// What Nitrogen.ngr's <c>lowers</c> clauses lower a grammar to: one type per kind of construct, one
/// operation per concrete rule. Text is the raw source spelling; quoted literals keep their quotes and
/// C# keeps its spacing. Nothing executes these operations; they type and describe a grammar.
/// </summary>
public static class GrammarSemantics
{
    public static class Types
    {
        public static readonly SemanticType File = Named("File");
        public static readonly SemanticType Module = Named("Module");
        public static readonly SemanticType Member = Named("Member");
        public static readonly SemanticType Except = Named("Except");
        public static readonly SemanticType End = Named("End");
        public static readonly SemanticType Alternative = Named("Alternative");
        public static readonly SemanticType Precedence = Named("Precedence");
        public static readonly SemanticType Clause = Named("Clause");
        public static readonly SemanticType Kinds = Named("Kinds");
        public static readonly SemanticType LowersForm = Named("LowersForm");
        public static readonly SemanticType TypeSpec = Named("TypeSpec");
        public static readonly SemanticType OperationSpec = Named("OperationSpec");
        public static readonly SemanticType LoweringArgument = Named("LoweringArgument");
        public static readonly SemanticType Semantics = Named("Semantics");
        public static readonly SemanticType SemanticItem = Named("SemanticItem");
        public static readonly SemanticType Expression = Named("Expression");
        public static readonly SemanticType Tail = Named("Tail");
        public static readonly SemanticType ClassItem = Named("ClassItem");

        internal static readonly SemanticType[] All =
        [
            File, Module, Member, Except, End, Alternative, Precedence, Clause, Kinds, LowersForm, TypeSpec,
            OperationSpec, LoweringArgument, Semantics, SemanticItem, Expression, Tail, ClassItem,
        ];

        static SemanticType Named(string name) => SemanticType.Named("Grammar", name);
    }

    static readonly SemanticType Text = SemanticTypes.Text;

    static SemanticType Seq(SemanticType element) => SemanticTypes.SequenceOf(element);

    static SemanticType Opt(SemanticType element) => SemanticTypes.OptionalOf(element);

    static OperationSignature Op(string name, SemanticType result, params SemanticType[] inputs) =>
        new("Grammar." + name, result, inputs);

    static readonly OperationSignature[] s_operations =
    [
        Op("File", Types.File, Seq(Types.Module)),
        Op("Module", Types.Module, Text, Seq(Types.Member)),
        Op("Using", Types.Member, Text),
        Op("Symbols", Types.Member, Seq(Text)),
        Op("SymbolProperty", Types.Member, Text, Types.Kinds, Text, Text),
        Op("Builtin", Types.Member, Text, Opt(Text), Seq(Text)),
        Op("TokenRule", Types.Member, Text, Types.Expression, Opt(Types.Except)),
        Op("Except", Types.Except, Seq(Text)),
        Op("SyntaxRule", Types.Member, Text, Types.Expression, Seq(Types.Clause), Types.End),
        Op("Terminator", Types.End),
        Op("RuleSemantics", Types.End, Types.Semantics),
        Op("ExtensibleRule", Types.Member, Text, Seq(Types.SemanticItem), Seq(Types.Alternative)),
        Op("Extend", Types.Member, Text, Seq(Types.Alternative)),
        Op("Alternative", Types.Alternative, Opt(Text), Types.Expression, Opt(Types.Precedence), Seq(Types.Clause), Opt(Types.Semantics)),
        Op("Precedence", Types.Precedence, Text, Opt(Text)),
        Op("Declares", Types.Clause, Text, Text, Opt(Text), Opt(Text), Opt(Text), Opt(Text)),
        Op("References", Types.Clause, Opt(Text), Types.Kinds, Text, Opt(Text)),
        Op("KindGroup", Types.Kinds, Seq(Text)),
        Op("SingleKind", Types.Kinds, Text),
        Op("Scope", Types.Clause),
        Op("Dynamic", Types.Clause),
        Op("Lowers", Types.Clause, Types.LowersForm),
        Op("LowersTemplate", Types.LowersForm, Text, Text),
        Op("LowersExpand", Types.LowersForm, Text, Text),
        Op("LowersRepeat", Types.LowersForm, Text, Text, Text, Text),
        Op("LowersLiteral", Types.LowersForm, Text, Text),
        Op("LowersText", Types.LowersForm, Text, Text),
        Op("LowersSequence", Types.LowersForm, Text, Text),
        Op("LowersValue", Types.LowersForm, Types.TypeSpec, Text),
        Op("ComputedType", Types.TypeSpec, Text),
        Op("FixedType", Types.TypeSpec, Text),
        Op("LowersReference", Types.LowersForm, Text, Opt(Text)),
        Op("LowersCall", Types.LowersForm, Types.OperationSpec, Seq(Types.LoweringArgument)),
        Op("ComputedOperation", Types.OperationSpec, Opt(Text), Text),
        Op("FixedOperation", Types.OperationSpec, Text),
        Op("SequenceArgument", Types.LoweringArgument, Text, Text),
        Op("OptionalArgument", Types.LoweringArgument, Text, Text),
        Op("OptionalTextArgument", Types.LoweringArgument, Text),
        Op("TextArgument", Types.LoweringArgument, Text),
        Op("FieldArgument", Types.LoweringArgument, Text),
        Op("Semantics", Types.Semantics, Seq(Types.SemanticItem)),
        Op("PropertyDecl", Types.SemanticItem, Text, Seq(Text), Text, Text, Text),
        Op("Check", Types.SemanticItem, Opt(Text), Text, Text, Opt(Text)),
        Op("Assignment", Types.SemanticItem, Text, Text),
        Op("Choice", Types.Expression, Seq(Types.Expression)),
        Op("Sequence", Types.Expression, Seq(Types.Expression)),
        Op("Element", Types.Expression, Opt(Text), Types.Expression),
        Op("Unary", Types.Expression, Seq(Text), Types.Expression),
        Op("Postfix", Types.Expression, Types.Expression, Seq(Text)),
        Op("Literal", Types.Expression, Text),
        Op("Any", Types.Expression),
        Op("CharClass", Types.Expression, Opt(Text), Seq(Types.ClassItem)),
        Op("ClassItem", Types.ClassItem, Text, Opt(Text)),
        Op("Parenthesized", Types.Expression, Types.Expression, Types.Tail),
        Op("SeparatorTail", Types.Tail, Types.Expression, Text),
        Op("GroupClose", Types.Tail),
        Op("Reference", Types.Expression, Text),
    ];

    /// <summary>The <c>Grammar</c> semantic module: imports Core, exports the types and operations above.</summary>
    public static SemanticModule Module { get; } = new("Grammar", ["Core"], Types.All, s_operations);
}
