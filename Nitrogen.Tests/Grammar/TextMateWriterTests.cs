using System.Text.Json;
using System.Text.RegularExpressions;
using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>TextMate grammars generated from Nitrogen grammars paint what the server's palette will.</summary>
public sealed class TextMateWriterTests
{
    static readonly IReadOnlyDictionary<GrammarLiteralRole, string> RoleScopes = new Dictionary<GrammarLiteralRole, string>
    {
        [GrammarLiteralRole.Operation] = "entity.name.function",
        [GrammarLiteralRole.DeclarationKeyword] = "storage.type",
        [GrammarLiteralRole.SectionKeyword] = "keyword.other",
        [GrammarLiteralRole.ModifierWord] = "keyword.operator.word",
        [GrammarLiteralRole.ConstantWord] = "constant.language",
        [GrammarLiteralRole.BlockBracket] = "punctuation.section.block",
        [GrammarLiteralRole.Parenthesis] = "punctuation.section.parens",
        [GrammarLiteralRole.SquareBracket] = "punctuation.section.brackets",
        [GrammarLiteralRole.Terminator] = "punctuation.terminator",
        [GrammarLiteralRole.Separator] = "punctuation.separator",
        [GrammarLiteralRole.Operator] = "keyword.operator",
    };

    static JsonElement Generate(string grammar)
    {
        var result = GrammarCompiler.TextMate([new GrammarInput("p.ngr", grammar, "N")],
            new TextMateOptions("palette", "source.palette", RoleScopes,
                new Dictionary<string, string> { ["goal"] = "entity.name.type.class" }, "variable.other.readwrite"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Diagnostic.Severity == GrammarSeverity.Error);
        return JsonDocument.Parse(result.Json!).RootElement;
    }

    static string PaletteGrammar => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grammars", "Palette.ngr"));

    /// <summary>The pattern named <paramref name="scope"/> whose match matches <paramref name="text"/> exactly.</summary>
    static JsonElement PatternFor(JsonElement grammar, string scope, string text) =>
        Assert.Single(grammar.GetProperty("patterns").EnumerateArray(), p =>
            p.TryGetProperty("name", out var name) && name.GetString() == scope &&
            p.TryGetProperty("match", out var match) && Regex.IsMatch(text, "^(?:" + match.GetString() + ")$"));

    [Fact]
    public void Generates_role_patterns()
    {
        var grammar = Generate(PaletteGrammar);
        Assert.Equal("source.palette", grammar.GetProperty("scopeName").GetString());
        PatternFor(grammar, "storage.type.palette", "goal");
        PatternFor(grammar, "keyword.other.palette", "train");
        PatternFor(grammar, "keyword.operator.word.palette", "sharpness");
        PatternFor(grammar, "constant.language.palette", "xz");
        PatternFor(grammar, "constant.language.palette", "kg");
        PatternFor(grammar, "entity.name.function.palette", "policy");
        PatternFor(grammar, "keyword.operator.palette", "..");
        PatternFor(grammar, "punctuation.section.block.palette", "{");
        PatternFor(grammar, "punctuation.terminator.palette", ";");
    }

    [Fact]
    public void A_word_with_several_roles_takes_the_strongest()
    {
        // "hold" is a modifier in Step and a section keyword in Hold.
        PatternFor(Generate(PaletteGrammar), "keyword.other.palette", "hold");
    }

    /// <summary>The context pattern (keyword, then what follows) that matches <paramref name="text"/>.</summary>
    static JsonElement ContextPattern(JsonElement grammar, string text) =>
        Assert.Single(grammar.GetProperty("patterns").EnumerateArray(), p => p.TryGetProperty("captures", out _)
            && Regex.IsMatch(text, "^(?:" + p.GetProperty("match").GetString() + ")$"));

    static string Capture(JsonElement pattern, int index) => pattern.GetProperty("captures").GetProperty(index.ToString()).GetProperty("name").GetString()!;

    [Fact]
    public void Reference_and_constant_context_patterns()
    {
        var grammar = Generate(PaletteGrammar);
        var reference = ContextPattern(grammar, "use stand");
        Assert.Equal("keyword.other.palette", Capture(reference, 1));
        Assert.Equal("entity.name.type.class.palette", Capture(reference, 2));
        var toggle = ContextPattern(grammar, "perturb on");
        Assert.Equal("keyword.other.palette", Capture(toggle, 1));
        Assert.Equal("constant.language.palette", Capture(toggle, 2));
        var constant = ContextPattern(grammar, "anchored xz");
        Assert.Equal("entity.name.function.palette", Capture(constant, 1));
        Assert.Equal("constant.language.palette", Capture(constant, 2));
    }

    [Fact]
    public void Declaration_name_pattern()
    {
        var pattern = ContextPattern(Generate(PaletteGrammar), "goal stand");
        Assert.Equal("storage.type.palette", pattern.GetProperty("captures").GetProperty("1").GetProperty("name").GetString());
        Assert.Equal("entity.name.type.class.palette", pattern.GetProperty("captures").GetProperty("2").GetProperty("name").GetString());
    }

    [Fact]
    public void Delimited_strings_span_lines()
    {
        var grammar = Generate("""
            syntax module M
            {
              token TemplateString = "<[" (!"]>" .)* "]>";
              syntax S = "quote" Value:TemplateString;
            }
            """);
        var template = Assert.Single(grammar.GetProperty("patterns").EnumerateArray(), p => p.TryGetProperty("begin", out var b) && b.GetString() == "<\\[");
        Assert.Equal("string.quoted.double.palette", template.GetProperty("name").GetString());
        Assert.Equal("\\]>", template.GetProperty("end").GetString());
        Assert.False(template.TryGetProperty("patterns", out _)); // no backslash escapes in this rule
    }

    [Fact]
    public void Metacharacters_are_escaped()
    {
        var grammar = Generate("""
            syntax module M
            {
              token Name = ['a'..'z']+;
              syntax S = Name "?" Name "*" "(" Name ")" "|" Name;
            }
            """);
        PatternFor(grammar, "keyword.operator.palette", "?");
        PatternFor(grammar, "keyword.operator.palette", "*");
        PatternFor(grammar, "keyword.operator.palette", "|");
        PatternFor(grammar, "punctuation.section.parens.palette", "(");
    }

    [Fact]
    public void Strings_numbers_and_comments_come_first()
    {
        var grammar = Generate("""
            syntax module M
            {
              token String = "\"" ([^ '"' '\\' '\n'] / "\\" .)* "\"";
              token Number = ['0'..'9']+ ("." ['0'..'9']+)?;
              syntax S = "set" Value:String Count:Number;
            }
            """);
        var names = grammar.GetProperty("patterns").EnumerateArray()
            .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() : null).ToList();
        Assert.Equal("comment.line.double-slash.palette", names[0]);
        Assert.Equal("comment.block.palette", names[1]);
        var text = Assert.Single(grammar.GetProperty("patterns").EnumerateArray(), p => p.TryGetProperty("name", out var n) && n.GetString() == "string.quoted.double.palette");
        Assert.Equal("\"", text.GetProperty("begin").GetString());
        Assert.Equal("\"", text.GetProperty("end").GetString());
        Assert.Equal("\\\\.", text.GetProperty("patterns")[0].GetProperty("match").GetString()); // `\"` does not end the string
        PatternFor(grammar, "constant.numeric.palette", "12.5");
        Assert.True(names.IndexOf("string.quoted.double.palette") < names.IndexOf("keyword.other.palette"));
    }
}
