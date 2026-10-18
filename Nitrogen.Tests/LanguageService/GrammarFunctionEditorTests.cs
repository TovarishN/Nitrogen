using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.Grammar;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Editor support for grammar functions (spec: grammar functions, Editor support).</summary>
public sealed class GrammarFunctionEditorTests : IDisposable
{
    const string Grammar = """
        syntax module Hex
        {
          function HexDigit(d: Grammar.Int) : Grammar.Rule =
            if d < 10 then [char(48 + d)] else [char(55 + d) char(87 + d)];

          function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule =
            if width == 0 then ()
            else choice d in 0 .. 15 {
              let size = 16 ^ (width - 1);
              let low = d * size;
              let part = values & [low .. (low + size - 1)];
              where part != [] : HexDigit(d) HexIn(width - 1, part - low)
            };
        }

        syntax module Demo
        {
          using Hex;

          function Twice(item: Grammar.Rule) : Grammar.Rule = item item;

          function Digits(count: Grammar.Int) : Grammar.Rule =
            let n = count;
            seq i in 1 .. n { ['0' .. '9'] };

          function Pick(limit: Grammar.Int) : Grammar.Rule =
            if limit == 0 then () else choice d in 0 .. limit { let next = d + 1; where d != 1 : Digits(next) };

          token IdStart = ['A'..'Z' 'a'..'z'];
          token Pair    = Twice("ab");
          token Escape  = "\\u" HexIn(2, IdStart);
          syntax Start  = Pair Escape;
        }
        """;

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-grammar-functions-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    static DocumentPosition At(string source, string text, int shift = 0, int occurrence = 0)
    {
        int at = -1;
        for (int i = 0; i <= occurrence; i++) at = source.IndexOf(text, at + 1, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' not found");
        return new LineMap(source).PositionOf(at + shift);
    }

    NitrogenLanguageService Service(string grammar = Grammar, string name = "Demo.ngr")
    {
        File.WriteAllText(Path.Combine(_root, name), grammar);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri(name), 1, grammar);
        return service;
    }

    static string Apply(string text, IEnumerable<DocumentEdit> edits)
    {
        var lines = new LineMap(text);
        foreach (var edit in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(edit.Range.Start)] + edit.NewText + text[lines.OffsetOf(edit.Range.End)..];
        return text;
    }

    // ---- hover ----

    [Fact]
    public void Hover_on_a_call_shows_the_signature_and_the_expansion_size()
    {
        using var service = Service();
        var hover = service.Hover(Uri("Demo.ngr"), At(Grammar, "Twice(\"ab\")", 2));
        Assert.NotNull(hover);
        Assert.Contains("function Twice(item: Grammar.Rule) : Grammar.Rule", hover.Markdown);
        Assert.Contains("declared in Demo.ngr line 20", hover.Markdown);
        Assert.Contains("Expands to 3 nodes, 0 classes, depth 2:", hover.Markdown);
        Assert.Contains("\"ab\" \"ab\"", hover.Markdown);
        Assert.Equal(At(Grammar, "Twice(\"ab\")"), hover.Range.Start);
        Assert.Equal(At(Grammar, "Twice(\"ab\")", 5), hover.Range.End);
    }

    [Fact]
    public void Hover_on_an_imported_call_counts_the_expansion_from_the_composed_modules()
    {
        using var service = Service();
        var hover = service.Hover(Uri("Demo.ngr"), At(Grammar, "HexIn(2", 1));
        Assert.NotNull(hover);
        Assert.Contains("function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule", hover.Markdown);
        Assert.Contains("Grammar function of `Hex` — declared in Demo.ngr line 6", hover.Markdown);

        // The size is that of the expansion the compiler produces for this call.
        var modules = GrammarParser.Parse(Grammar).File!.Modules;
        var escape = (TokenRule)modules[1].Rules.Single(r => r.Name == "Escape");
        Expr? expansion = null;
        GrammarFunctions.Expand(modules, (call, expanded) => { if (call.Name.Name == "HexIn") expansion = expanded; });
        Assert.NotNull(expansion);
        long nodes = 0, classes = 0;
        int Depth(Expr e)
        {
            nodes++;
            if (e is CharClassExpr) classes++;
            return 1 + GrammarExtraction.Children(e).Select(Depth).DefaultIfEmpty(0).Max();
        }
        int depth = Depth(expansion);
        Assert.Contains($"Expands to {nodes} nodes, {classes} classes, depth {depth}:", hover.Markdown);
        Assert.True(classes > 1);
        Assert.NotNull(escape);
    }

    [Fact]
    public void Hover_on_a_declaration_and_on_parameters_and_variables()
    {
        using var service = Service();
        var declaration = service.Hover(Uri("Demo.ngr"), At(Grammar, "function Digits", 10));
        Assert.NotNull(declaration);
        Assert.Contains("function Digits(count: Grammar.Int) : Grammar.Rule", declaration.Markdown);
        Assert.DoesNotContain("Expands to", declaration.Markdown);

        var parameter = service.Hover(Uri("Demo.ngr"), At(Grammar, "= count", 3));
        Assert.NotNull(parameter);
        Assert.Contains("`parameter count: Grammar.Int` — in `Digits`", parameter.Markdown);

        var variable = service.Hover(Uri("Demo.ngr"), At(Grammar, ".. n", 3));
        Assert.NotNull(variable);
        Assert.Contains("`variable n` — in `Digits`", variable.Markdown);
    }

    [Fact]
    public void A_call_that_fails_to_evaluate_says_so()
    {
        string grammar = Grammar.Replace("Twice(\"ab\")", "Digits(\"ab\")", StringComparison.Ordinal);
        using var service = Service(grammar);
        var hover = service.Hover(Uri("Demo.ngr"), At(grammar, "Digits(\"ab\")", 1));
        Assert.NotNull(hover);
        Assert.Contains("does not expand", hover.Markdown);
    }

    [Fact]
    public void Built_in_calls_have_a_hover_usages_and_highlights_but_no_declaration()
    {
        using var service = Service();
        var at = At(Grammar, "char(48", 1);
        var hover = service.Hover(Uri("Demo.ngr"), at);
        Assert.NotNull(hover);
        Assert.Equal("```ngr\nchar(code: Grammar.Int) : Grammar.Char\n```\nBuilt-in grammar function: the character with this UTF-16 code.", hover.Markdown);
        Assert.Equal(At(Grammar, "char(48"), hover.Range.Start);

        Assert.Empty(service.Definition(Uri("Demo.ngr"), at));
        var uses = new[] { At(Grammar, "char(48"), At(Grammar, "char(55"), At(Grammar, "char(87") };
        Assert.Equal(uses, service.References(Uri("Demo.ngr"), at, includeDeclaration: true).Select(r => r.Range.Start).OrderBy(p => (p.Line, p.Character)).ToArray());
        Assert.Equal(uses, service.Highlights(Uri("Demo.ngr"), at).Select(h => h.Range.Start).OrderBy(p => (p.Line, p.Character)).ToArray());
        Assert.All(service.Highlights(Uri("Demo.ngr"), at), h => Assert.Equal(HighlightKind.Read, h.Kind));

        var refused = Assert.Throws<RenameRefusedException>(() => service.PrepareRename(Uri("Demo.ngr"), at));
        Assert.Contains("'char' is a built-in grammar function", refused.Message);
    }

    [Fact]
    public void Highlights_cover_functions_and_variables_with_their_declarations_as_writes()
    {
        using var service = Service();
        var highlights = service.Highlights(Uri("Demo.ngr"), At(Grammar, "item item", 6));
        Assert.Equal(3, highlights.Count);
        Assert.Equal(At(Grammar, "item: Grammar.Rule"), Assert.Single(highlights, h => h.Kind == HighlightKind.Write).Range.Start);
    }

    // ---- expansion preview ----

    [Fact]
    public void The_expansion_preview_is_readable_ngr_that_parses_back_to_the_same_expression()
    {
        using var service = Service();
        var expansion = service.ExpandGrammarCall(Uri("Demo.ngr"), At(Grammar, "HexIn(2", 3));
        Assert.NotNull(expansion);
        Assert.Equal("HexIn(2, IdStart)", expansion.Call);
        Assert.NotNull(expansion.Text);

        var action = service.InlineGrammarCall(Uri("Demo.ngr"), new(At(Grammar, "HexIn(2", 2), At(Grammar, "HexIn(2", 2)));
        Assert.NotNull(action);
        Assert.Null(action.DisabledReason);
        Assert.Equal("Inline expansion of HexIn(2, IdStart)", action.Title);
        string inlined = Apply(Grammar, action.Edits[Uri("Demo.ngr")]);
        Assert.DoesNotContain("HexIn(2", inlined);

        static string EscapeBody(string text)
        {
            var parsed = GrammarParser.Parse(text);
            Assert.NotNull(parsed.File);
            var expanded = GrammarFunctions.Expand(parsed.File.Modules);
            Assert.DoesNotContain(expanded.Diagnostics, d => d.Severity == GrammarSeverity.Error);
            return GrammarDumper.Dump(((TokenRule)expanded.Modules.Single(m => m.Name == "Demo").Rules.Single(r => r.Name == "Escape")).Body);
        }
        Assert.Equal(EscapeBody(Grammar), EscapeBody(inlined));
    }

    const string WideGrammar = """
        syntax module Wide
        {
          function HexDigit(d: Grammar.Int) : Grammar.Rule =
            if d < 10 then [char(48 + d)] else [char(55 + d) char(87 + d)];

          function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule =
            if width == 0 then ()
            else choice d in 0 .. 15 {
              let size = 16 ^ (width - 1);
              let low = d * size;
              let part = values & [low .. (low + size - 1)];
              where part != [] : HexDigit(d) HexIn(width - 1, part - low)
            };

          token Letter = ['A'..'Z' 'a'..'z' 'À'..'Ö' 'Ø'..'ö'];
          token Escape = "\\u" HexIn(4, Letter);
          syntax Start = Escape;
        }
        """;

    [Fact]
    public void Hover_shows_a_long_expansion_one_alternative_per_line()
    {
        using var service = Service(WideGrammar);
        var hover = service.Hover(Uri("Demo.ngr"), At(WideGrammar, "HexIn(4", 1));
        Assert.NotNull(hover);
        Assert.Contains("""
            ```ngr
            ['0'] ['0'] (
                ['4' '6'] ['1'..'9' 'A'..'F' 'a'..'f']
              / ['5' '7'] ['0'..'9' 'A' 'a']
            """, hover.Markdown);
        Assert.Contains("\n  / ['F' 'f'] ['0'..'6']\n)\n```", hover.Markdown);
    }

    [Fact]
    public void Hover_anywhere_on_a_call_shows_its_expansion_but_rule_names_keep_their_own_hover()
    {
        using var service = Service(WideGrammar);
        var onArgument = service.Hover(Uri("Demo.ngr"), At(WideGrammar, "4, Letter)"));
        Assert.NotNull(onArgument);
        Assert.StartsWith("```ngr\nfunction HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule\n```\nExpands to", onArgument.Markdown);
        Assert.Equal(At(WideGrammar, "HexIn(4"), onArgument.Range.Start);
        Assert.Equal(At(WideGrammar, "Letter);", 7), onArgument.Range.End);

        var onRule = service.Hover(Uri("Demo.ngr"), At(WideGrammar, "Letter);", 1));
        Assert.DoesNotContain("Expands to", onRule?.Markdown ?? "");
    }

    [Fact]
    public void A_long_expansion_is_inlined_on_indented_lines_that_parse_back_to_the_same_expression()
    {
        using var service = Service(WideGrammar);
        var action = service.InlineGrammarCall(Uri("Demo.ngr"), new(At(WideGrammar, "HexIn(4", 1), At(WideGrammar, "HexIn(4", 1)));
        Assert.NotNull(action);
        string inlined = Apply(WideGrammar, action.Edits[Uri("Demo.ngr")]);
        Assert.Contains("""
              token Escape = "\\u" (['0'] ['0'] (
                    ['4' '6'] ['1'..'9' 'A'..'F' 'a'..'f']
                  / ['5' '7'] ['0'..'9' 'A' 'a']
            """, inlined);

        static string EscapeBody(string text)
        {
            var parsed = GrammarParser.Parse(text);
            Assert.NotNull(parsed.File);
            var expanded = GrammarFunctions.Expand(parsed.File.Modules);
            Assert.DoesNotContain(expanded.Diagnostics, d => d.Severity == GrammarSeverity.Error);
            return GrammarDumper.Dump(((TokenRule)expanded.Modules.Single().Rules.Single(r => r.Name == "Escape")).Body);
        }
        Assert.Equal(EscapeBody(WideGrammar), EscapeBody(inlined));
    }

    [Fact]
    public void Inlining_parenthesizes_where_precedence_needs_it()
    {
        string grammar = Grammar.Replace("token Pair    = Twice(\"ab\");", "token Pair    = Twice(\"ab\")* \"c\";", StringComparison.Ordinal);
        using var service = Service(grammar);
        var action = service.InlineGrammarCall(Uri("Demo.ngr"), new(At(grammar, "Twice(\"ab\")", 1), At(grammar, "Twice(\"ab\")", 1)));
        Assert.NotNull(action);
        Assert.Contains("token Pair    = (\"ab\" \"ab\")* \"c\";", Apply(grammar, action.Edits[Uri("Demo.ngr")]));
    }

    [Fact]
    public void No_inline_action_outside_rule_body_calls()
    {
        using var service = Service();
        Assert.Null(service.InlineGrammarCall(Uri("Demo.ngr"), new(At(Grammar, "Digits(next)", 1), At(Grammar, "Digits(next)", 1))));
        Assert.Null(service.InlineGrammarCall(Uri("Demo.ngr"), new(At(Grammar, "IdStart =", 1), At(Grammar, "IdStart =", 1))));
    }

    [Fact]
    public async Task The_language_server_offers_the_inline_refactoring()
    {
        using var service = Service();
        var at = At(Grammar, "HexIn(2", 2);
        static string Request(int id, string method, object parameters) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        var (_, messages, _) = await LspServerTests.Session(service, Request(1, "initialize", new { capabilities = new { } }),
            Request(2, "textDocument/codeAction", new
            {
                textDocument = new { uri = Uri("Demo.ngr") },
                range = new { start = new { line = at.Line, character = at.Character }, end = new { line = at.Line, character = at.Character } },
                context = new { only = new[] { "refactor.inline" } },
            }),
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");
        var result = messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2).GetProperty("result");
        var action = Assert.Single(result.EnumerateArray());
        Assert.Equal("refactor.inline", action.GetProperty("kind").GetString());
        Assert.Equal("Inline expansion of HexIn(2, IdStart)", action.GetProperty("title").GetString());
        var edit = Assert.Single(action.GetProperty("edit").GetProperty("changes").GetProperty(Uri("Demo.ngr")).EnumerateArray());
        Assert.StartsWith("(", edit.GetProperty("newText").GetString());
    }

    // ---- semantic tokens ----

    [Fact]
    public void Functions_parameters_variables_and_body_keywords_are_coloured()
    {
        using var service = Service();
        var tokens = service.SemanticTokens(Uri("Demo.ngr"));
        TokenType? TypeAt(string text, int occurrence = 0, int length = -1)
        {
            var at = At(Grammar, text, 0, occurrence);
            if (tokens.Where(t => t.Start == at).Select(t => (SemanticToken?)t).FirstOrDefault() is not { } token) return null;
            if (length >= 0) Assert.Equal(length, token.Length);
            return token.Type;
        }
        Assert.Equal(TokenType.Function, TypeAt("Twice", length: 5));
        Assert.Equal(TokenType.Function, TypeAt("Twice", 1, length: 5));
        Assert.Equal(TokenType.Function, TypeAt("HexIn(2", length: 5));
        Assert.Equal(TokenType.Function, TypeAt("HexDigit", length: 8));
        Assert.Equal(TokenType.Function, TypeAt("Digits(next)", length: 6));
        Assert.Equal(TokenType.Parameter, TypeAt("item"));
        Assert.Equal(TokenType.Parameter, TypeAt("item", 1));
        Assert.Equal(TokenType.Parameter, TypeAt("item", 2));
        Assert.Equal(TokenType.Parameter, TypeAt("count", 1));
        Assert.Equal(TokenType.Variable, TypeAt("n ="));
        Assert.Equal(TokenType.Variable, TypeAt("i in"));
        Assert.Equal(TokenType.Variable, TypeAt("next", 1));
        foreach (var (context, shift) in new[] { ("let n", 0), ("seq i", 0), ("i in", 2), ("if limit", 0), ("then ()", 0), ("else choice", 0),
                     ("choice d", 0), ("where d", 0), ("{ let next", 2) })
        {
            var at = At(Grammar, context, shift);
            Assert.True(tokens.Any(t => t.Start == at && t.Type == TokenType.Keyword), $"'{context}' at {shift} is not a keyword");
        }
    }

    // ---- completion ----

    [Fact]
    public void Completion_in_a_rule_body_offers_the_module_and_imported_functions()
    {
        string grammar = Grammar.Replace("syntax Start  = Pair Escape;", "syntax Start  = Pair Escape;\n  token Other = ;", StringComparison.Ordinal);
        using var service = Service(grammar);
        var items = service.Completion(Uri("Demo.ngr"), At(grammar, "token Other = ", 14));
        var twice = Assert.Single(items, i => i.Label == "Twice");
        Assert.Equal(CompletionKind.Function, twice.Kind);
        Assert.Equal("function Twice(item: Grammar.Rule) : Grammar.Rule", twice.Detail);
        var hexIn = Assert.Single(items, i => i.Label == "HexIn");
        Assert.Equal("function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule (Hex)", hexIn.Detail);
        Assert.Contains(items, i => i.Label == "HexDigit");
    }

    [Fact]
    public void Completion_filters_by_the_typed_prefix_and_works_in_function_bodies()
    {
        string grammar = Grammar.Replace("token Pair    = Twice(\"ab\");", "token Pair    = Hex;", StringComparison.Ordinal);
        using var service = Service(grammar);
        var items = service.Completion(Uri("Demo.ngr"), At(grammar, "= Hex;", 5));
        Assert.Contains(items, i => i.Label == "HexIn" && i.Kind == CompletionKind.Function);
        Assert.Contains(items, i => i.Label == "HexDigit");
        Assert.DoesNotContain(items, i => i.Label == "Twice");

        var inBody = service.Completion(Uri("Demo.ngr"), At(grammar, "where d != 1 : Digits", 15));
        Assert.Contains(inBody, i => i.Label == "Digits" && i.Kind == CompletionKind.Function);
    }

    [Fact]
    public void Functions_of_other_modules_need_a_using()
    {
        string grammar = Grammar.Replace("  using Hex;\n", "", StringComparison.Ordinal)
            .Replace("token Escape  = \"\\\\u\" HexIn(2, IdStart);", "token Escape  = \"\\\\u\";", StringComparison.Ordinal)
            .Replace("syntax Start  = Pair Escape;", "syntax Start  = Pair Escape;\n  token Other = ;", StringComparison.Ordinal);
        using var service = Service(grammar);
        var items = service.Completion(Uri("Demo.ngr"), At(grammar, "token Other = ", 14));
        Assert.Contains(items, i => i.Label == "Twice");
        Assert.DoesNotContain(items, i => i.Label == "HexIn");
    }

    [Fact]
    public void No_function_completion_outside_expressions()
    {
        using var service = Service();
        var items = service.Completion(Uri("Demo.ngr"), At(Grammar, "token IdStart", 6));
        Assert.DoesNotContain(items, i => i.Label == "Twice");
    }

    // ---- rename ----

    [Fact]
    public void Renaming_a_function_changes_its_declaration_and_calls()
    {
        using var service = Service();
        var range = service.PrepareRename(Uri("Demo.ngr"), At(Grammar, "Twice(\"ab\")", 2));
        Assert.Equal(At(Grammar, "Twice(\"ab\")"), range.Start);
        var edits = service.Rename(Uri("Demo.ngr"), At(Grammar, "Twice(\"ab\")", 2), "Double");
        string renamed = Apply(Grammar, edits[Uri("Demo.ngr")]);
        Assert.Contains("function Double(item: Grammar.Rule)", renamed);
        Assert.Contains("token Pair    = Double(\"ab\");", renamed);
        Assert.DoesNotContain("Twice", renamed);
    }

    [Fact]
    public void Renaming_a_function_from_its_declaration_updates_calls_in_other_files()
    {
        const string other = """
            syntax module Other
            {
              using Demo;
              token Twins = Demo.Twice("x") Twice("y");
            }
            """;
        File.WriteAllText(Path.Combine(_root, "Other.ngr"), other);
        using var service = Service();
        service.Open(Uri("Other.ngr"), 1, other);
        var edits = service.Rename(Uri("Demo.ngr"), At(Grammar, "function Twice", 10), "Double");
        Assert.Contains("function Double(", Apply(Grammar, edits[Uri("Demo.ngr")]));
        Assert.Contains("token Twins = Demo.Double(\"x\") Double(\"y\");", Apply(other, edits[Uri("Other.ngr")]));
    }

    [Fact]
    public void Renaming_a_parameter_or_variable_stays_inside_its_function()
    {
        using var service = Service();
        var edits = service.Rename(Uri("Demo.ngr"), At(Grammar, "item item", 1), "part");
        string renamed = Apply(Grammar, edits[Uri("Demo.ngr")]);
        Assert.Contains("function Twice(part: Grammar.Rule) : Grammar.Rule = part part;", renamed);

        var variable = service.Rename(Uri("Demo.ngr"), At(Grammar, "let n", 4), "total");
        Assert.Contains("let total = count;\n    seq i in 1 .. total {", Apply(Grammar, variable[Uri("Demo.ngr")]));
    }

    [Theory]
    [InlineData("Twice(\"ab\")", "Digits", "already names a function")]
    [InlineData("Twice(\"ab\")", "choice", "not a valid function name")]
    [InlineData("let n", "count", "already used in 'Digits'")]
    [InlineData("HexIn(2", "HexDigit", "already names a function")]
    public void Rename_refuses_conflicts_and_keywords(string at, string newName, string reason)
    {
        using var service = Service();
        var position = At(Grammar, at, at.StartsWith("let", StringComparison.Ordinal) ? 4 : 1);
        var error = Assert.Throws<RenameRefusedException>(() => service.Rename(Uri("Demo.ngr"), position, newName));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Go_to_definition_still_resolves_calls_and_parameters()
    {
        using var service = Service();
        var function = Assert.Single(service.Definition(Uri("Demo.ngr"), At(Grammar, "Digits(next)", 1)));
        Assert.Equal(At(Grammar, "Digits(count"), function.Range.Start);
        var parameter = Assert.Single(service.Definition(Uri("Demo.ngr"), At(Grammar, "item item", 6)));
        Assert.Equal(At(Grammar, "item:"), parameter.Range.Start);
    }
}
