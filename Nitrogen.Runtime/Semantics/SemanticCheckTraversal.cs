namespace Nitrogen.Semantics;

internal static class SemanticCheckTraversal
{
    internal static IEnumerable<int> Nodes(SyntaxTree tree, int root, bool ancestors)
    {
        // Match whole-file checking's first reading of an ambiguity.
        for (int child = root, parent = tree.Parent(child); parent >= 0; child = parent, parent = tree.Parent(child))
            if (tree.Kind(parent) == SyntaxKinds.Ambiguous && tree.Child(parent, 0) != child) yield break;
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            if (tree.Kind(node) == SyntaxKinds.Ambiguous)
            {
                if (tree.ChildCount(node) > 0) stack.Push(tree.Child(node, 0));
                continue;
            }
            yield return node;
            for (int index = tree.ChildCount(node) - 1; index >= 0; index--) stack.Push(tree.Child(node, index));
        }
        if (ancestors)
            for (int parent = tree.Parent(root); parent >= 0; parent = tree.Parent(parent)) yield return parent;
    }
}
