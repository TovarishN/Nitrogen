using System.Text;

namespace Nitrogen.Grammar;

public enum SignatureMode { Grammar, Language, Augment }
public enum AnalysisInputMode { Document, Regions }

public sealed record LanguageReference(string Value, bool IsPath);
public sealed record DocumentSignature(SignatureMode Mode, LanguageReference? Reference,
    AnalysisInputMode Input, int Start, int Length);
public sealed record SignatureReadResult(EquatableArray<DocumentSignature> Signatures,
    string MaskedText, EquatableArray<GrammarDiagnostic> Diagnostics);

/// <summary>Reads bootstrap directives only in the leading trivia, preserving all UTF-16 offsets.</summary>
public static class DocumentSignatures
{
    const string Marker = "#!nitrogen";
    public const string InvalidSignature = "NGR0401";

    public static SignatureReadResult Read(string text)
    {
        var signatures = new List<DocumentSignature>();
        var diagnostics = new List<GrammarDiagnostic>();
        char[] masked = text.ToCharArray();
        int position = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        bool consumed = false;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position == text.Length) break;
            int start = position;
            if (Starts(text, position, "//"))
            {
                int end = LineEnd(text, position, text.Length);
                if (ReadLines(position + 2, end)) Mask(start, end);
                position = end;
            }
            else if (Starts(text, position, "/*") || Starts(text, position, "<!--"))
            {
                bool html = text[position + 1] == '!';
                string close = html ? "-->" : "*/";
                int content = position + (html ? 4 : 2);
                int closeAt = text.IndexOf(close, content, StringComparison.Ordinal);
                int end = closeAt < 0 ? text.Length : closeAt;
                if (ReadLines(content, end))
                {
                    Mask(start, closeAt < 0 ? end : end + close.Length);
                    if (closeAt < 0) Error(start, end, "unterminated signature comment");
                }
                position = closeAt < 0 ? end : end + close.Length;
            }
            else if (IsMarker(text, position, text.Length))
            {
                int end = LineEnd(text, position, text.Length);
                if (!ReadDirective(position, end)) break;
                Mask(start, end);
                position = end;
            }
            else break;
        }
        if (consumed && text.Length > 0 && text[0] == '\uFEFF') masked[0] = ' ';
        return new SignatureReadResult(signatures.ToArray(), new string(masked), diagnostics.ToArray());

        void Mask(int start, int end)
        {
            consumed = true;
            for (int i = start; i < end; i++)
                if (masked[i] is not ('\r' or '\n')) masked[i] = ' ';
        }

        void Error(int start, int end, string message) => diagnostics.Add(new GrammarDiagnostic(
            InvalidSignature, GrammarSeverity.Error, message, GrammarSpan.FromBounds(start, end)));

        bool ReadLines(int start, int end)
        {
            bool found = false;
            while (start < end)
            {
                int next = LineEnd(text, start, end);
                int content = start;
                while (content < next && char.IsWhiteSpace(text[content])) content++;
                if (IsMarker(text, content, next)) found |= ReadDirective(content, next);
                start = next;
                if (start < end && text[start] == '\r') start++;
                if (start < end && text[start] == '\n') start++;
            }
            return found;
        }

        bool ReadDirective(int start, int end)
        {
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            int cursor = start + Marker.Length;
            string mode = Word();
            // Region bodies are interpreted by the host comment extractor, not the header reader.
            if (mode is "region" or "end") return false;
            SignatureMode kind;
            switch (mode)
            {
                case "grammar": kind = SignatureMode.Grammar; break;
                case "language": kind = SignatureMode.Language; break;
                case "augment": kind = SignatureMode.Augment; break;
                default:
                    Error(start, end, "expected grammar, language, or augment after #!nitrogen");
                    return true;
            }
            LanguageReference? reference = null;
            var input = AnalysisInputMode.Document;
            if (kind != SignatureMode.Grammar)
            {
                Skip();
                if (cursor < end && text[cursor] == '"')
                {
                    cursor++;
                    var value = new StringBuilder();
                    bool closed = false;
                    while (cursor < end)
                    {
                        char c = text[cursor++];
                        if (c == '"') { closed = true; break; }
                        if (c == '\\' && cursor < end && text[cursor] is '"' or '\\') c = text[cursor++];
                        value.Append(c);
                    }
                    if (!closed || value.Length == 0)
                    {
                        Error(start, end, "expected a non-empty quoted language path");
                        return true;
                    }
                    // Require whitespace before an optional input selector.
                    if (cursor < end && !char.IsWhiteSpace(text[cursor]))
                    {
                        Error(start, end, "expected whitespace after language path");
                        return true;
                    }
                    reference = new LanguageReference(value.ToString(), true);
                }
                else
                {
                    string name = Word();
                    if (!ValidName(name))
                    {
                        Error(start, end, "expected a quoted local path or a language name");
                        return true;
                    }
                    reference = new LanguageReference(name, false);
                }
                if (kind == SignatureMode.Augment)
                {
                    string selector = Word();
                    if (selector == "regions") input = AnalysisInputMode.Regions;
                    else if (selector.Length > 0 && selector != "document")
                    {
                        Error(start, end, "expected document or regions input selector");
                        return true;
                    }
                }
            }
            Skip();
            if (cursor != end)
            {
                Error(start, end, "unexpected text after Nitrogen signature");
                return true;
            }
            bool conflicting = false;
            foreach (var previous in signatures)
                conflicting |= kind == SignatureMode.Grammar || previous.Mode == SignatureMode.Grammar ||
                    (kind == SignatureMode.Language && previous.Mode == SignatureMode.Language);
            if (conflicting)
                Error(start, end, "incompatible or duplicate Nitrogen document signature");
            else signatures.Add(new DocumentSignature(kind, reference, input, start, end - start));
            return true;

            void Skip()
            {
                while (cursor < end && char.IsWhiteSpace(text[cursor])) cursor++;
            }

            string Word()
            {
                Skip();
                int from = cursor;
                while (cursor < end && !char.IsWhiteSpace(text[cursor])) cursor++;
                return text.Substring(from, cursor - from);
            }
        }
    }

    static bool ValidName(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_')) return false;
        foreach (char c in name)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    static int LineEnd(string text, int start, int end)
    {
        while (start < end && text[start] is not ('\r' or '\n')) start++;
        return start;
    }

    static bool Starts(string text, int start, string value) =>
        start + value.Length <= text.Length && string.CompareOrdinal(text, start, value, 0, value.Length) == 0;

    static bool IsMarker(string text, int start, int end) =>
        start + Marker.Length <= end && Starts(text, start, Marker) &&
        (start + Marker.Length == end || char.IsWhiteSpace(text[start + Marker.Length]));
}
