using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

public sealed record AnalysisSelection(string Key, LanguageDefinition Definition, AnalysisInputMode Input, bool IsPrimary);
public sealed record SourceProjection(string HostPath, string Text, int HostStart)
{
    public AnalysisLocation Map(int start, int length) => new(HostPath, HostStart + start, length);
}
public sealed record ProjectedInput(AnalysisSelection Selection, SourceProjection Projection);
public sealed record ProjectionResult(IReadOnlyList<ProjectedInput> Inputs, IReadOnlyList<AnalysisIssue> Diagnostics);

public static class RegionExtractor
{
    public static ProjectionResult Extract(DocumentInput input, ILanguageDefinitionResolver resolver, InputSnapshot inputs)
    {
        var regions = new List<ProjectedInput>();
        var issues = new List<AnalysisIssue>();
        foreach (var comment in HostCommentExtractor.Read(input.Path, input.Text))
        {
            bool html = comment.Text.StartsWith("<!--", StringComparison.Ordinal);
            bool block = html || comment.Text.StartsWith("/*", StringComparison.Ordinal);
            string close = html ? "-->" : "*/";
            bool terminated = block && comment.Text.EndsWith(close, StringComparison.Ordinal);
            int from = html ? 4 : 2, end = terminated ? comment.Text.Length - close.Length : comment.Text.Length;
            int bodyStart = -1, markerStart = -1;
            LanguageDefinition? definition = null;
            bool invalid = false;
            for (int line = from; line < end;)
            {
                int finish = line;
                while (finish < end && comment.Text[finish] is not ('\r' or '\n')) finish++;
                int next = finish;
                if (next < end && comment.Text[next] == '\r') next++;
                if (next < end && comment.Text[next] == '\n') next++;
                int content = line;
                while (content < finish && char.IsWhiteSpace(comment.Text[content])) content++;
                string value = comment.Text[content..finish].TrimEnd();
                if (IsDirective(value, "region"))
                {
                    if (!block) Error(content, "regions require a block comment");
                    else if (bodyStart >= 0) { Error(content, "nested Nitrogen regions are not supported"); invalid = true; }
                    else
                    {
                        bodyStart = next;
                        markerStart = content;
                        invalid = false;
                        string header = "#!nitrogen language" + value["#!nitrogen region".Length..];
                        var parsed = DocumentSignatures.Read(header);
                        if (parsed.Diagnostics.Count > 0 || parsed.Signatures.Count != 1)
                        {
                            Error(content, "expected a language reference after #!nitrogen region");
                            invalid = true;
                        }
                        else
                        {
                            var resolved = resolver.Resolve(parsed.Signatures[0].Reference!, input.Path, inputs);
                            issues.AddRange(resolved.Diagnostics.Select(i => i.Location.Path == input.Path
                                ? i with { Location = comment.Map(content, value.Length) } : i));
                            definition = resolved.Definition;
                        }
                    }
                }
                else if (IsDirective(value, "end"))
                {
                    if (bodyStart < 0) Error(content, "Nitrogen end marker has no region");
                    else
                    {
                        if (value != "#!nitrogen end") { Error(content, "unexpected text after Nitrogen end marker"); invalid = true; }
                        string body = comment.Text[bodyStart..line];
                        if (string.IsNullOrWhiteSpace(body)) { Error(content, "Nitrogen region body is empty"); invalid = true; }
                        if (!terminated) { Error(content, "Nitrogen region host comment is unterminated"); invalid = true; }
                        if (!invalid && definition is not null)
                        {
                            var projection = new SourceProjection(input.Path, body, comment.HostStart + bodyStart);
                            regions.Add(new(new AnalysisSelection(definition.Id + "#region:" + projection.HostStart,
                                definition, AnalysisInputMode.Regions, false), projection));
                        }
                        bodyStart = -1;
                        definition = null;
                    }
                }
                line = next;
            }
            if (bodyStart >= 0) Error(markerStart, "Nitrogen region is missing its end marker before the host comment ends");

            void Error(int offset, string message) => issues.Add(new("NGR0404", message,
                comment.Map(offset, Math.Min(end - offset, "#!nitrogen region".Length)), [], "nitrogen", true));
        }
        return new(regions, issues);
    }

    static bool IsDirective(string text, string word)
    {
        string marker = "#!nitrogen " + word;
        return text.StartsWith(marker, StringComparison.Ordinal) &&
            (text.Length == marker.Length || char.IsWhiteSpace(text[marker.Length]));
    }
}
