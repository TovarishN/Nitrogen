namespace Nitrogen.LanguageService;

/// <summary>The editor's indentation settings (LSP's FormattingOptions): a level is <paramref name="TabSize"/> columns.</summary>
public sealed record FormattingOptions(int TabSize, bool InsertSpaces);

/// <summary>
/// Formatting (spec: formatting): whitespace at the start and end of lines only, from a syntax tree's
/// leaf tokens. Lines starting with a token are indented by their block (<c>{ }</c>) or keep their
/// offset from the list item they continue; runs of comment lines move with the next code line; trailing
/// whitespace, blank-line runs and the end of the file are tidied. Spaces within a line, line breaks and
/// tokens never change.
/// </summary>
internal static class Formatter
{
    internal readonly record struct Token(int Node, int Start, int End, string Text);

    /// <summary>A replacement of [Start, End) of the original text.</summary>
    internal readonly record struct Edit(int Start, int End, string Text);

    /// <summary>The tree's leaf tokens in text order: non-Missing nodes with no children and a non-empty span, one per start (an ambiguity's candidates share theirs).</summary>
    internal static List<Token> Tokens(SyntaxTree tree, string text)
    {
        var tokens = new List<Token>();
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.ChildCount(node) != 0 || (tree.Flags(node) & NodeFlags.Missing) != 0) continue;
            var span = tree.Span(node);
            if (span.Length > 0) tokens.Add(new Token(node, span.Start, span.End, text.Substring(span.Start, span.Length)));
        }
        tokens.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Node.CompareTo(b.Node));
        var distinct = new List<Token>(tokens.Count);
        foreach (var token in tokens)
            if (distinct.Count == 0 || distinct[^1].Start != token.Start) distinct.Add(token);
        return distinct;
    }

    /// <summary>The edits formatting lines <paramref name="first"/> to <paramref name="last"/>; the end-of-file rule applies when the last line is among them.</summary>
    internal static List<Edit> Edits(SyntaxTree tree, string text, FormattingOptions options, int first, int last)
    {
        var lines = new LineMap(text);
        int count = lines.LineCount;
        last = Math.Min(last, count - 1);
        int level = Math.Max(1, options.TabSize);
        var tokens = Tokens(tree, text);

        // Each line: its span, its first non-whitespace offset (-1 when none), and its indentation in columns.
        var start = new int[count];
        var end = new int[count];
        var firstChar = new int[count];
        var oldCols = new int[count];
        for (int line = 0; line < count; line++)
        {
            start[line] = lines.OffsetOf(new DocumentPosition(line, 0));
            end[line] = start[line] + lines.LineLength(line);
            int i = start[line], cols = 0;
            for (; i < end[line] && (text[i] == ' ' || text[i] == '\t'); i++) cols += text[i] == '\t' ? level - cols % level : 1;
            firstChar[line] = i < end[line] ? i : -1;
            oldCols[line] = cols;
        }

        int TokenStartingAt(int offset)
        {
            int low = 0, high = tokens.Count;
            while (low < high) { int middle = (low + high) >>> 1; if (tokens[middle].Start < offset) low = middle + 1; else high = middle; }
            return low < tokens.Count && tokens[low].Start == offset ? low : -1;
        }

        bool InsideToken(int offset)
        {
            int low = 0, high = tokens.Count;
            while (low < high) { int middle = (low + high) >>> 1; if (tokens[middle].Start < offset) low = middle + 1; else high = middle; }
            return low > 0 && tokens[low - 1].End > offset; // the last token starting before the offset still covers it
        }

        // The innermost open "{" before each token (its token index, or -1).
        var blockBefore = new int[tokens.Count];
        var open = new Stack<int>();
        for (int k = 0; k < tokens.Count; k++)
        {
            blockBefore[k] = open.Count > 0 ? open.Peek() : -1;
            if (tokens[k].Text == "{") open.Push(k);
            else if (tokens[k].Text == "}" && open.Count > 0) open.Pop();
        }

        int LineOf(int offset) => lines.PositionOf(offset).Line;

        // Line kinds: kept (starts inside a token), blank, code (starts with a token), comment (starts in trivia).
        var kept = new bool[count];
        var blank = new bool[count];
        var codeToken = new int[count];
        for (int line = 0; line < count; line++)
        {
            kept[line] = InsideToken(start[line]);
            blank[line] = !kept[line] && firstChar[line] < 0;
            codeToken[line] = kept[line] || blank[line] ? -1 : TokenStartingAt(firstChar[line]);
        }

        var newCols = (int[])oldCols.Clone();
        for (int line = 0; line < count; line++)
        {
            int k = codeToken[line];
            if (k < 0) continue;
            int block = blockBefore[k];
            int? continued = ContinuedItem(tree, tokens[k].Node, line, block);
            if (continued is int itemLine)
                newCols[line] = Math.Max(0, newCols[itemLine] + Math.Max(0, oldCols[line] - oldCols[itemLine]));
            else if (tokens[k].Text == "}")
                newCols[line] = block < 0 ? 0 : newCols[LineOf(tokens[block].Start)];
            else
                newCols[line] = block < 0 ? 0 : newCols[LineOf(tokens[block].Start)] + level;
        }

        // The innermost list item containing the node that started on an earlier line, when it started in the same block: its first line.
        int? ContinuedItem(SyntaxTree syntax, int node, int line, int block)
        {
            for (int child = node, parent = syntax.Parent(node); parent >= 0; child = parent, parent = syntax.Parent(parent))
            {
                if (syntax.Kind(parent) != SyntaxKinds.List) continue;
                int itemStart = syntax.Span(child).Start;
                int itemLine = LineOf(itemStart);
                if (itemLine >= line) continue;
                int itemToken = TokenStartingAt(itemStart);
                return itemToken >= 0 && blockBefore[itemToken] == block ? itemLine : null;
            }
            return null;
        }

        // Comment runs move with the next code line, one level in when it starts with "}".
        for (int line = 0; line < count; line++)
        {
            if (kept[line] || blank[line] || codeToken[line] >= 0) continue;
            int runEnd = line;
            while (runEnd + 1 < count && !kept[runEnd + 1] && !blank[runEnd + 1] && codeToken[runEnd + 1] < 0) runEnd++;
            int next = runEnd + 1;
            while (next < count && codeToken[next] < 0) next++;
            int target = next >= count ? 0 : newCols[next] + (tokens[codeToken[next]].Text == "}" ? level : 0);
            int delta = target - oldCols[line];
            for (int comment = line; comment <= runEnd; comment++) newCols[comment] = Math.Max(0, oldCols[comment] + delta);
            line = runEnd;
        }

        string Indent(int cols) => options.InsertSpaces ? new string(' ', cols) : new string('\t', cols / level) + new string(' ', cols % level);

        var edits = new List<Edit>();
        bool endOfFile = last == count - 1;
        int lastContent = count - 1;
        while (lastContent >= 0 && blank[lastContent]) lastContent--;
        int firstContent = 0;
        while (firstContent < count && blank[firstContent]) firstContent++;

        for (int line = first; line <= last; line++)
        {
            if (endOfFile && line > lastContent) break; // the end-of-file edit covers the rest
            if (blank[line])
            {
                // Leading blank lines go, as does every blank line after the first of a run; a kept one loses its whitespace.
                if (line < firstContent || (line > 0 && blank[line - 1]))
                {
                    int to = line + 1 < count ? start[line + 1] : end[line];
                    edits.Add(new Edit(start[line], to, ""));
                }
                else if (end[line] > start[line]) edits.Add(new Edit(start[line], end[line], ""));
                continue;
            }
            if (!kept[line])
            {
                string indent = Indent(newCols[line]);
                if (text.AsSpan(start[line], firstChar[line] - start[line]).SequenceEqual(indent) is false)
                    edits.Add(new Edit(start[line], firstChar[line], indent));
            }
            if (endOfFile && line == lastContent) continue; // its trailing whitespace goes with the end-of-file edit
            int trailing = TrailingStart(line);
            if (trailing < end[line]) edits.Add(new Edit(trailing, end[line], ""));
        }

        if (endOfFile && lastContent >= 0)
        {
            int contentEnd = TrailingStart(lastContent);
            int lineBreak = text.IndexOf('\n');
            string newline = lineBreak > 0 && text[lineBreak - 1] == '\r' ? "\r\n" : "\n";
            if (text.AsSpan(contentEnd).SequenceEqual(newline) is false) edits.Add(new Edit(contentEnd, text.Length, newline));
        }
        return edits;

        // Where a line's trailing whitespace starts; its end when the line ends inside a token.
        int TrailingStart(int line)
        {
            if (InsideToken(end[line])) return end[line];
            int i = end[line];
            while (i > start[line] && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
            return i;
        }
    }

    /// <summary>The text after the edits (non-overlapping, against the original).</summary>
    internal static string Apply(string text, IEnumerable<Edit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            text = string.Concat(text.AsSpan(0, edit.Start), edit.Text, text.AsSpan(edit.End));
        return text;
    }
}
