using Nitrogen.Grammar;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    IReadOnlyList<DocumentLocation> GrammarDeclarationDefinition(string uri, DocumentPosition position)
    {
        if (!TryDocument(uri, out var document) || document.Language.Name != "ngr") return [];
        var file = GrammarParser.Parse(document.Text).File;
        if (file is null) return [];
        int offset = document.Lines.OffsetOf(position);
        foreach (var declaration in file.Languages)
            foreach (var setting in declaration.Settings)
                foreach (var value in setting.Values)
                {
                    if (offset < value.Span.Start || offset >= value.Span.End) continue;
                    if (setting.Name is "sources" or "grammars")
                    {
                        if (!Uri.TryCreate(uri, UriKind.Absolute, out var source) || !source.IsFile) return [];
                        string path = Path.GetFullPath(value.Name, Path.GetDirectoryName(source.LocalPath)!);
                        if (ReadNavigationText(path) is null) return [];
                        return [new DocumentLocation(new Uri(path).AbsoluteUri,
                            new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 0)))];
                    }
                    if (setting.Name != "start") continue;
                    int dot = value.Name.LastIndexOf('.');
                    string moduleName = value.Name[..dot], ruleName = value.Name[(dot + 1)..];
                    var module = file.Modules.FirstOrDefault(m => m.Name == moduleName);
                    if (module is null) return [];
                    return _projects[document.Language][uri].Declarations
                        .Where(s => s.Kind == "rule" && s.Name == ruleName &&
                            s.NameSpan.Start >= module.Span.Start && s.NameSpan.End <= module.Span.End)
                        .Select(LocationOf).ToArray();
                }
        return [];
    }
}
