namespace Nitrogen.Tests.Typed;

/// <summary>The Typed test language's helpers (issue 239): its types are the strings "num", "bool" and "error".</summary>
public static class TypedTypes
{
    public static string Builtin(string kind, string name) => name switch
    {
        "pi" => "num",
        "yes" => "bool",
        _ => "error",
    };

    public static string Throw() => throw new InvalidOperationException("boom");
}
