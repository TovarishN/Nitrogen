using System.Text;

namespace Nitrogen;

/// <summary>
/// S-expression dump for tests and debugging. Interior node: <c>(Kind child ...)</c>; literal:
/// <c>"text"</c>; other leaf: <c>Kind:"text"</c>; empty: <c>_</c>; ambiguous: <c>(? a b)</c>.
/// Recovery (issue 235): a Missing node is <c>!Kind</c>; a node whose recovery skipped input is
/// <c>(Kind~ ...)</c>.
/// </summary>
public static class SyntaxDumper
{
    public static string Dump(SyntaxTree tree) => Dump(tree, tree.Root);

    public static string Dump(SyntaxTree tree, int node)
    {
        var builder = new StringBuilder();
        Write(tree, node, builder);
        return builder.ToString();
    }

    internal static string KindName(int kind, Language? language) =>
        language is not null ? language.GetKindName(kind)
        : SyntaxKinds.ModuleOf(kind) == 0 ? SyntaxKinds.GetBuiltinName(kind)
        : "#" + kind;

    static void Write(SyntaxTree tree, int node, StringBuilder builder)
    {
        int kind = tree.Kind(node);
        var flags = tree.Flags(node);
        if ((flags & NodeFlags.Missing) != 0)
        {
            builder.Append('!').Append(KindName(kind, tree.Language));
            return;
        }
        if (kind == SyntaxKinds.Empty)
        {
            builder.Append('_');
            return;
        }
        if (kind == SyntaxKinds.Literal)
        {
            builder.Append('"').Append(tree.GetText(node)).Append('"');
            return;
        }
        string name = kind == SyntaxKinds.Ambiguous ? "?" : KindName(kind, tree.Language);
        if ((flags & NodeFlags.Skipped) != 0) name += "~";
        int count = tree.ChildCount(node);
        if (count == 0 && kind != SyntaxKinds.List)
        {
            builder.Append(name).Append(":\"").Append(tree.GetText(node)).Append('"');
            return;
        }
        builder.Append('(').Append(name);
        for (int k = 0; k < count; k++)
        {
            builder.Append(' ');
            Write(tree, tree.Child(node, k), builder);
        }
        builder.Append(')');
    }
}
