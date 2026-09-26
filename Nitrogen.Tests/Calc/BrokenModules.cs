namespace Nitrogen.Tests.Calc;

/// <summary>Declares an extension point it does not own.</summary>
public sealed class RogueDeclareModule : SyntaxModule
{
    public static readonly RogueDeclareModule Instance = new();

    RogueDeclareModule() : base("Rogue") { }

    public override string GetKindName(int localKind) => "Rogue#" + localKind;

    public override void Register(ExtensionRegistry registry) => registry.Declare(CalcModule.Instance.Expr);
}

/// <summary>Adds a second alternative named Pow.</summary>
public sealed unsafe class PowerTwinModule : SyntaxModule
{
    public static readonly PowerTwinModule Instance = new();

    PowerTwinModule() : base("Calc.PowerTwin") { }

    public override string GetKindName(int localKind) => "Pow";

    public override void Register(ExtensionRegistry registry) =>
        registry.AddPostfix(CalcModule.Instance.Expr,
            new PostfixExtension("Pow", KindBase | 1, 25, Associativity.Right, &Never, AsciiSet.Of("^")));

    static bool Never(ref ParserState s) => false;
}

/// <summary>An independently registered copy of Clash's Invoke alternative.</summary>
public sealed unsafe class ClashCopyModule : SyntaxModule
{
    public static readonly ClashCopyModule Instance = new();

    ClashCopyModule() : base("Calc.ClashCopy") { }

    public override string GetKindName(int localKind) => "Invoke";

    public override void Register(ExtensionRegistry registry) =>
        registry.AddPrefix(CalcModule.Instance.Expr,
            new PrefixExtension("Invoke", KindBase | 1, 0, &Never,
                AsciiSet.Of("ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz")));

    static bool Never(ref ParserState s) => false;
}

/// <summary>A postfix alternative with precedence 0, which would never bind.</summary>
public sealed unsafe class ZeroPrecedenceModule : SyntaxModule
{
    public static readonly ZeroPrecedenceModule Instance = new();

    ZeroPrecedenceModule() : base("Calc.Zero") { }

    public override string GetKindName(int localKind) => "Zero";

    public override void Register(ExtensionRegistry registry) =>
        registry.AddPostfix(CalcModule.Instance.Expr,
            new PostfixExtension("Zero", KindBase | 1, 0, Associativity.Left, &Never));

    static bool Never(ref ParserState s) => false;
}
