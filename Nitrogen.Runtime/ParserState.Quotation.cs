namespace Nitrogen;

public unsafe ref partial struct ParserState
{
    /// <summary>Matches a list placeholder only where a generated list expects an item.</summary>
    public bool TryListHole(string category)
    {
        if (Holes is null) return false;
        int at = PeekPastTrivia();
        if (!_text[at..].StartsWith("..$")) return false;
        int end = at + 3;
        while (end < _text.Length && IsIdentifierPart(_text[end])) end++;
        if (end == at + 3 || !Holes.TryGetValue(_text[(at + 3)..end].ToString(), out var hole) ||
            !hole.IsList || hole.Category != category) return false;
        Position = at;
        PushLeaf(SyntaxKinds.ListHole, end);
        return true;
    }

    /// <summary>Closes a template list and records its structural item contract.</summary>
    public void CloseTemplateList(string category, int minimumCount, string? separator)
    {
        int node = Close();
        if (InTemplate && node != SyntaxTree.NoNode)
            (Arena.TemplateLists ??= new())[node] = new(category, minimumCount, separator);
    }
}
