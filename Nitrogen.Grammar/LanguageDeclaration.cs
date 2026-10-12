namespace Nitrogen.Grammar;

public sealed record LanguagePresentationOption(string Category, string Name, string Value);
public sealed record LanguageSetting(string Name, EquatableArray<NameDecl> Values, GrammarSpan Span);
public sealed record LanguageDecl(string Name, string Start, string? Namespace,
    EquatableArray<string> Grammars, EquatableArray<string> Sources,
    EquatableArray<string> Usings, EquatableArray<string> Extensions,
    EquatableArray<LanguagePresentationOption> Presentation,
    GrammarSpan Span = default, EquatableArray<LanguageSetting> Settings = default);

/// <summary>Shared declaration validation for bootstrap and HIR projection.</summary>
public static class LanguageDeclarations
{
    public static LanguageDecl Build(string name, EquatableArray<LanguageSetting> settings, GrammarSpan span)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var presentation = new List<LanguagePresentationOption>();
        foreach (var setting in settings)
        {
            string key = setting.Name;
            if (key is "tokens" or "types")
            {
                if (setting.Values.Count != 2) throw Invalid("expected presentation name and style", setting.Span);
                key += ":" + setting.Values[0].Name;
                presentation.Add(new LanguagePresentationOption(setting.Name, setting.Values[0].Name, setting.Values[1].Name));
            }
            else if (key is not ("start" or "namespace" or "grammars" or "sources" or "usings" or "extensions"))
                throw Invalid("unknown language setting '" + key + "'", setting.Span);
            if (!seen.Add(key)) throw Invalid("duplicate language setting '" + key + "'", setting.Span);
            if (setting.Values.Count == 0 || setting.Values.Any(v => v.Name.Length == 0))
                throw Invalid("language setting must have non-empty values", setting.Span);
        }
        var start = settings.FirstOrDefault(s => s.Name == "start");
        if (start is null) throw Invalid("language requires a start rule", span);
        if (start.Values.Count != 1 || start.Values[0].Name.IndexOf('.') < 1)
            throw Invalid("start must be a qualified rule name", start.Span);
        var ns = settings.FirstOrDefault(s => s.Name == "namespace");
        if (ns is not null && ns.Values.Count != 1) throw Invalid("expected one namespace", ns.Span);
        return new LanguageDecl(name, start.Values[0].Name, ns?.Values[0].Name,
            Values("grammars"), Values("sources"), Values("usings"), Values("extensions"),
            presentation.ToArray(), span, settings);

        EquatableArray<string> Values(string key) =>
            settings.FirstOrDefault(s => s.Name == key)?.Values.Select(v => v.Name).ToArray() ?? new string[0];
    }

    static LanguageDeclarationException Invalid(string message, GrammarSpan span) => new(message, span);
}

public sealed class LanguageDeclarationException : Exception
{
    public LanguageDeclarationException(string message, GrammarSpan span) : base(message) => Span = span;
    public GrammarSpan Span { get; }
}
