using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;

namespace Nitrogen.Cli;

/// <summary><c>nitrogen lsp</c>: the language server on stdin/stdout for .ngr and workspace languages.</summary>
internal static class LspCommand
{
    public static LanguageRegistry Registry()
    {
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("ngr", NgrParser.Language, new Dictionary<string, Rule> { [".ngr"] = NitrogenModule.File }, NgrStyles, new NgrAssist()));
        return registry;
    }

    // What a grammar lowers to (GrammarSemantics) shows on hover; its colours stay those of the grammar's syntax. The palette
    // entries are the scopes of editors/vscode/syntaxes/ngr.tmLanguage.json, so the server paints .ngr as that grammar does.
    static readonly Presentation NgrStyles = new(new Dictionary<string, SymbolStyle>
    {
        ["module"] = new(TokenType.Namespace, OutlineKind.Module, Palette.Ngr("entity.name.namespace")),
        ["rule"] = new(TokenType.Type, OutlineKind.Class, Palette.Ngr("entity.name.function.rule")),
        ["property"] = new(TokenType.Property, OutlineKind.Property, Palette.Ngr("variable.other.property")),
    }, colorFromLowering: false, literals: NgrLiterals(), tokens: new Dictionary<string, string>
    {
        ["String"] = Palette.Ngr("string.quoted.double"),
        ["Char"] = Palette.Ngr("string.quoted.single"),
        ["Integer"] = Palette.Ngr("constant.numeric.integer"),
        ["TemplateString"] = Palette.Ngr("string.quoted.other.template"),
    }, comments: (Palette.Ngr("comment.line.double-slash"), Palette.Ngr("comment.block")));

    /// <summary>Literals whose ngr.tmLanguage.json scope depends on the rule they are in, by (rule, literal).</summary>
    static Dictionary<(string Kind, string Literal), string> NgrLiterals()
    {
        var literals = new Dictionary<(string Kind, string Literal), string>();
        void Add(string kind, string literal, string scope) => literals[(kind, literal)] = Palette.Ngr(scope);
        void Words(string scope, params string[] words)
        {
            foreach (string word in words) Add("*", word, scope);
        }
        // By spelling, in every rule.
        Words("storage.type.syntax", "syntax");
        Words("storage.type.token", "token");
        Words("storage.type.function", "function");
        Words("storage.type.property", "property");
        Words("storage.type.symbol", "symbol");
        Words("storage.type.language", "language");
        Words("storage.type.module", "module");
        Words("storage.modifier.direction", "out", "in");
        Words("storage.modifier.extensible", "extensible");
        Words("storage.modifier.extend", "extend");
        Words("storage.modifier.flag", "hover");
        Words("keyword.other.clause", "lowers", "rewrites", "scope", "except", "sequential", "export", "dynamic");
        Words("keyword.other.declares", "declares");
        Words("keyword.other.references", "references");
        Words("keyword.other.precedence", "precedence");
        Words("keyword.other.associativity", "left", "right");
        Words("keyword.other.builtin", "builtin");
        Words("keyword.other.check", "check");
        Words("keyword.other.symbols", "symbols");
        Words("keyword.other.for", "for");
        Words("keyword.other.using", "using");
        Words("keyword.other.let", "let");
        Words("keyword.other.lowering", "initializer", "literal", "operation", "reference", "type", "text", "sequence", "optional",
            "value", "repeat", "template", "expand");
        Words("keyword.other.setting", "start", "namespace", "grammars", "sources", "extensions", "usings", "tokens", "types");
        Words("keyword.control.match", "match");
        Words("keyword.control.conditional", "if", "then", "else");
        Words("keyword.control.where", "where");
        Words("keyword.control.loop", "choice", "seq");
        Words("keyword.operator.word", "and", "or", "not");
        Words("variable.language.wildcard", "_");
        Add("*", "=", "keyword.operator.assignment");
        Add("*", "=>", "keyword.operator.arrow");
        Add("*", "..", "keyword.operator.range");
        Add("*", "^", "keyword.operator.negation");
        Add("*", ",", "punctuation.separator.comma");
        Add("*", ";", "punctuation.terminator");
        foreach (string peg in (string[])["/", "*", "+", "!", "&", "?"]) Add("*", peg, "keyword.operator.peg");
        Add("*", "|", "keyword.operator.alternative");
        Add("*", "{", "punctuation.section.block.begin");
        Add("*", "}", "punctuation.section.block.end");
        Add("*", ":", "punctuation.separator.type");
        // By rule, where the spelling is not enough.
        Add("References", "?", "keyword.operator.optional");
        Add("ComputedOperation", "?", "keyword.operator.optional");
        Add("KindGroup", "|", "keyword.operator.peg");
        Add("QuoteMatchArm", "|", "keyword.operator.peg");
        Add("QuoteMatchBody", "|", "keyword.operator.peg");
        Add("UnionTail", "|", "keyword.operator.peg");
        Add("Semantics", "{", "punctuation.section.semantics.begin");
        Add("Semantics", "}", "punctuation.section.semantics.end");
        Add("Check", ":", "punctuation.separator.message");
        Add("MatchDiagnostic", ":", "punctuation.separator.message");
        foreach (string kind in (string[])["QuoteHelper", "QuoteMatchHelper", "QuoteCaptures", "GrammarFunction"])
        {
            Add(kind, "(", "punctuation.section.parameters.begin");
            Add(kind, ")", "punctuation.section.parameters.end");
        }
        foreach (string kind in (string[])["HelperRewrite", "LowersTemplate", "LowersExpand"])
        {
            Add(kind, "(", "punctuation.section.arguments.begin");
            Add(kind, ")", "punctuation.section.arguments.end");
        }
        Add("Module", "module", "storage.type.module");
        Add("Module", "{", "punctuation.section.module.begin");
        Add("Module", "}", "punctuation.section.module.end");
        Add("CharClass", "[", "punctuation.definition.character-class.begin");
        Add("CharClass", "]", "punctuation.definition.character-class.end");
        Add("Parenthesized", "(", "punctuation.section.group.begin");
        Add("GroupClose", ")", "punctuation.section.group.end");
        Add("SeparatorTail", ";", "punctuation.separator.list");
        Add("SeparatorTail", ")", "punctuation.section.group.end");
        Add("EmptyGroup", "(", "punctuation.section.group.begin");
        Add("EmptyGroup", ")", "punctuation.section.group.end");
        Add("KindGroup", "(", "punctuation.section.group.begin");
        Add("KindGroup", ")", "punctuation.section.group.end");
        Add("Any", ".", "constant.character.any");
        Add("LabelPrefix", ":", "punctuation.separator.label");
        Add("LowersCall", "(", "punctuation.section.arguments.begin");
        Add("LowersCall", ")", "punctuation.section.arguments.end");
        foreach (string kind in (string[])["LowersText", "LowersTextArgument", "LowersOptionalTextArgument"])
            Add(kind, "text", "keyword.other.lowering");
        foreach (string kind in (string[])["LowersSequence", "LowersSequenceArgument"])
            Add(kind, "sequence", "keyword.other.lowering");
        foreach (string kind in (string[])["LowersOptionalArgument", "LowersOptionalTextArgument"])
            Add(kind, "optional", "keyword.other.lowering");
        return literals;
    }

    /// <param name="configRoot">The directory of a <c>--config</c> nitrogen.json; null serves the client's workspace.</param>
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, CancellationToken cancel, string? configRoot = null)
    {
        using var service = new NitrogenLanguageService(Registry());
        return await new LspServer(new JsonRpcConnection(input, output), service, log, configRoot).RunAsync(cancel);
    }

    /// <summary>The directory of <paramref name="path"/>, which must be an existing file named nitrogen.json; null with an error otherwise.</summary>
    public static string? ConfigRoot(string path, out string error)
    {
        string full = Path.GetFullPath(path);
        error = Path.GetFileName(full) != "nitrogen.json" ? $"--config must name a nitrogen.json file, not '{path}'"
            : !File.Exists(full) ? $"no file '{path}'" : "";
        return error.Length == 0 ? Path.GetDirectoryName(full) : null;
    }
}
