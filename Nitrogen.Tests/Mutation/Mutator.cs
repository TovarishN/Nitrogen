namespace Nitrogen.Tests;

/// <summary>A single-edit way to break a file (issue 235, spec §8).</summary>
public enum MutationKind
{
    DeleteToken,
    InsertToken,
    DuplicateLine,
    SwapTokens,
    Truncate,
}

/// <summary>
/// The original text with <c>[Start, Start + Removed)</c> replaced by <c>Inserted</c> characters.
/// Positions are in the original text.
/// </summary>
public sealed record Mutant(string File, MutationKind Kind, int Site, string Text, int Start, int Removed, int Inserted)
{
    public int Delta => Inserted - Removed;

    public int EditEnd => Start + Removed;

    public override string ToString() => $"{Path.GetFileName(File)} {Kind}#{Site} at {Start}";
}

/// <summary>Seeded, deterministic mutants on token boundaries (issue 235, spec §8).</summary>
public static class Mutator
{
    static readonly string[] Strays = ["}", ";", "zz", "7"];

    /// <summary>Token spans: identifier and number runs, quoted strings, single other characters. Whitespace and comments are skipped.</summary>
    public static List<(int Start, int Length)> Tokens(string text)
    {
        var tokens = new List<(int Start, int Length)>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }
            int start = i;
            if (c == '"')
            {
                int close = text.IndexOf('"', i + 1);
                i = close < 0 ? text.Length : close + 1;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            }
            else
            {
                i++;
            }
            tokens.Add((start, i - start));
        }
        return tokens;
    }

    /// <summary><paramref name="sitesPerKind"/> mutants of every kind, seeded from the file name and the kind.</summary>
    public static IEnumerable<Mutant> Mutants(string file, string text, int sitesPerKind)
    {
        var tokens = Tokens(text);
        if (tokens.Count < 2) yield break;
        foreach (var kind in Enum.GetValues<MutationKind>())
        {
            var random = new Random(StableHash(Path.GetFileName(file)) ^ (((int)kind + 1) * 7919));
            for (int site = 0; site < sitesPerKind; site++)
                yield return Make(file, text, tokens, kind, site, random);
        }
    }

    static Mutant Make(string file, string text, List<(int Start, int Length)> tokens, MutationKind kind, int site, Random random)
    {
        switch (kind)
        {
            case MutationKind.DeleteToken:
            {
                var (start, length) = tokens[random.Next(tokens.Count)];
                return new(file, kind, site, text.Remove(start, length), start, length, 0);
            }
            case MutationKind.InsertToken:
            {
                int at = tokens[random.Next(tokens.Count)].Start;
                string stray = Strays[random.Next(Strays.Length)] + " ";
                return new(file, kind, site, text.Insert(at, stray), at, 0, stray.Length);
            }
            case MutationKind.DuplicateLine:
            {
                int at = tokens[random.Next(tokens.Count)].Start;
                int lineStart = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
                int newline = text.IndexOf('\n', at);
                string line = newline < 0 ? text[lineStart..] + "\n" : text[lineStart..(newline + 1)];
                return new(file, kind, site, text.Insert(lineStart, line), lineStart, 0, line.Length);
            }
            case MutationKind.SwapTokens:
            {
                int i = random.Next(tokens.Count - 1);
                var (aStart, aLength) = tokens[i];
                var (bStart, bLength) = tokens[i + 1];
                int end = bStart + bLength;
                string swapped = text.Substring(bStart, bLength)
                    + text.Substring(aStart + aLength, bStart - aStart - aLength)
                    + text.Substring(aStart, aLength);
                return new(file, kind, site, text[..aStart] + swapped + text[end..], aStart, end - aStart, end - aStart);
            }
            default:
            {
                int at = tokens[random.Next(1, tokens.Count)].Start;
                return new(file, kind, site, text[..at], at, text.Length - at, 0);
            }
        }
    }

    /// <summary>FNV-1a: stable across processes, unlike <c>string.GetHashCode</c>.</summary>
    static int StableHash(string text)
    {
        unchecked
        {
            int hash = (int)2166136261;
            foreach (char c in text) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
