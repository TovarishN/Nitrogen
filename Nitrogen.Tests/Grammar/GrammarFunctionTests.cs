using Nitrogen.Cli;
using Nitrogen.Grammar;
using Nitrogen.LanguageService;
using Nitrogen.Tests.HexValues;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Grammar functions (spec: grammar functions), exercised through functions a grammar declares itself.</summary>
public sealed class GrammarFunctionTests : IDisposable
{
    const string Hex = "(class '0'..'9' 'A'..'F' 'a'..'f')";

    /// <summary>Functions every test module declares, as a grammar that needs them would (CSNitrogen declares HexIn).</summary>
    const string Functions = """
        function HexDigit(d: Grammar.Int) : Grammar.Rule = if d < 10 then [char(48 + d)] else [char(55 + d) char(87 + d)];
        function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule =
          if width == 0 then ()
          else choice d in 0 .. 15 {
            let size = 16 ^ (width - 1);
            let low = d * size;
            let part = values & [low .. (low + size - 1)];
            where part != [] : HexDigit(d) HexIn(width - 1, part - low)
          };
        function Keyword(word: Grammar.Text) : Grammar.Rule = seq c in word { [upper(c) lower(c)] };
        function Repeat(item: Grammar.Rule, min: Grammar.Int, max: Grammar.Int) : Grammar.Rule = seq i in 1 .. max { if i <= min then item else item? };
        function TrailingList(item: Grammar.Rule, separator: Grammar.Rule) : Grammar.Rule = (item; separator)* separator?;
        """;

    static string Module(string members) => "syntax module M { " + Functions + " " + members + " }";

    static GrammarFunctionsResult Expand(string members)
    {
        var parsed = GrammarParser.Parse(Module(members));
        Assert.True(parsed.Success, parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].ToString() : "");
        return GrammarFunctions.Expand(parsed.File!.Modules);
    }

    /// <summary>The last rule's body after expansion; no diagnostics allowed.</summary>
    static string Body(string members)
    {
        var result = Expand(members);
        Assert.True(result.Diagnostics.Count == 0, string.Join("; ", result.Diagnostics));
        return GrammarDumper.Dump(BodyOf(result.Modules[0].Rules.Last()));
    }

    static Expr BodyOf(RuleDecl rule) => rule is TokenRule token ? token.Body : ((SyntaxRule)rule).Body;

    static GrammarDiagnostic Single(string members) => Assert.Single(Expand(members).Diagnostics);

    [Fact]
    public void Functions_check_without_diagnostics() => Assert.Empty(Expand("token T = HexIn(2, ['A']);").Diagnostics);

    [Theory]
    [InlineData("token T = HexIn(2, ['A'..'C']);", "(seq (class '4') (class '1'..'3'))")]
    [InlineData("token T = HexIn(1, ['\\u000a'..'\\u000f']);", "(class 'A'..'F' 'a'..'f')")]
    [InlineData("token T = HexIn(2, ['\\u0000'..'\\u00ff']);", "(seq " + Hex + " " + Hex + ")")]
    [InlineData("token T = HexIn(2, ['\\u0010'..'\\u001f' '\\u0030'..'\\u003f']);", "(seq (class '1' '3') " + Hex + ")")]
    [InlineData("token T = HexIn(2, ['A' '\\u0050'..'\\u005f']);", "(choice (seq (class '4') (class '1')) (seq (class '5') " + Hex + "))")]
    [InlineData("token T = HexIn(1, [^ '\\u0000'..'\\u000e']);", "(class 'F' 'f')")]
    [InlineData("token T = HexIn(8, ['A']);", "(seq (class '0') (class '0') (class '0') (class '0') (class '0') (class '0') (class '4') (class '1'))")]
    [InlineData("token L = ['A'..'C']; token T = HexIn(2, L);", "(seq (class '4') (class '1'..'3'))")]
    [InlineData("token A = ['A']; token B = ['C']; token L = A / B; token T = HexIn(2, L);", "(seq (class '4') (class '1' '3'))")]
    [InlineData("token L = ['A'..'C']; token K = L; token T = HexIn(2, M.K);", "(seq (class '4') (class '1'..'3'))")]
    [InlineData("token T = HexIn(2, ['A'..'C'] | ['a']);", "(choice (seq (class '4') (class '1'..'3')) (seq (class '6') (class '1')))")]
    public void HexIn_expands_to_a_factored_digit_trie(string members, string expected) => Assert.Equal(expected, Body(members));

    [Theory]
    [InlineData("token T = \"\\\\u\" HexIn(2, ['A']) Keyword(\"if\");", "(seq (lit \"\\\\u\") (seq (class '4') (class '1')) (seq (class 'I' 'i') (class 'F' 'f')))")]
    [InlineData("token D = ['0'..'9']; token T = Repeat(D, 1, 3);", "(seq (ref M.D) (opt (ref M.D)) (opt (ref M.D)))")]
    [InlineData("token B = \"b\"; token T = Repeat({ \"a\" B }, 0, 1);", "(opt (seq (lit \"a\") (ref M.B)))")]
    [InlineData("syntax Item = \"i\"; syntax L = TrailingList(Item, \",\");", "(seq (sep* (ref M.Item) (lit \",\")) (opt (lit \",\")))")]
    [InlineData("syntax Item = \"i\"; syntax L = Items:TrailingList(Item, \",\") \";\";", "(seq (label Items (seq (sep* (ref M.Item) (lit \",\")) (opt (lit \",\")))) (lit \";\"))")]
    public void Functions_expand_where_they_are_called(string members, string expected) => Assert.Equal(expected, Body(members));

    [Theory]
    [InlineData("token X = \"x\"; token Y = \"y\"; function F() : Grammar.Rule = \"a\" X / \"b\" Y / \"c\" X; token T = F();",
        "(choice (seq (class 'a' 'c') (ref M.X)) (seq (class 'b') (ref M.Y)))")]
    [InlineData("token X = \"x\"; token Y = \"y\"; function F() : Grammar.Rule = ['a' 'b'] X / \"b\" Y / \"b\" X; token T = F();",
        "(choice (seq (class 'a'..'b') (ref M.X)) (seq (class 'b') (ref M.Y)) (seq (class 'b') (ref M.X)))")]
    [InlineData("token X = \"x\"; token Y = \"y\"; function F() : Grammar.Rule = \"a\" X / \"b\" Y / \"c\" X; syntax T = F();",
        "(choice (seq (lit \"a\") (ref M.X)) (seq (lit \"b\") (ref M.Y)) (seq (lit \"c\") (ref M.X)))")]
    public void Choices_factor_in_token_rules_only_when_order_cannot_change(string members, string expected) =>
        Assert.Equal(expected, Body(members));

    [Theory]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = let a = n * 2 + 1; let b = n ^ 2 * 9; seq i in a .. b { [char(48 + i)] }; token T = F(1);",
        "(seq (class '3') (class '4') (class '5') (class '6') (class '7') (class '8') (class '9'))")]
    [InlineData("function F(t: Grammar.Text, c: Grammar.Char) : Grammar.Rule = if length(t) == 2 and c != 'x' or false then t else \"no\"; token T = F(\"ab\", 'c');",
        "(lit \"ab\")")]
    [InlineData("function F(c: Grammar.Class) : Grammar.Rule = [(((~c | ['x'] - ['x']) & ['a'..'z'] - ['b'..'y']) + 1)]; token T = F([^ 'a']);", "(class 'b')")]
    [InlineData("function F(c: Grammar.Class) : Grammar.Rule = if c == [] then \"empty\" else c; token T = F(['q'] - ['q']);",
        "(lit \"empty\")")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = choice d in 0 .. n { where d % 2 == 1 : [char(code('a') + d)] }; token T = F(4);",
        "(class 'b' 'd')")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = if n == 0 then () else \"x\" F(n - 1); token T = F(2);",
        "(seq (lit \"x\") (lit \"x\"))")]
    public void Values_and_comprehensions_evaluate(string members, string expected) => Assert.Equal(expected, Body(members.Replace("false", "1 == 2")));

    [Theory]
    [InlineData("token T = HexIn(2, Missing);", "Missing", "NGR0150", "'Missing' is not a parameter, a let, a variable or a rule")]
    [InlineData("syntax S = \"s\"; token T = HexIn(2, S);", "HexIn(2, S)", "NGR0155", "'S' is not a character-class token rule")]
    [InlineData("token L = \"ab\"; token T = HexIn(2, L);", "HexIn(2, L)", "NGR0155", "token rule 'L' is not a character class")]
    [InlineData("token L = K; token K = L; token T = HexIn(2, L);", "HexIn(2, L)", "NGR0155", "token rule 'L' refers to itself")]
    [InlineData("token T = HexIn(1, ['\\u0100']);", "HexIn(1, ['\\u0100'])", "NGR0155", "'choice' produced no alternatives (in HexIn)")]
    [InlineData("token T = Hexx(1, ['a']);", "Hexx", "NGR0151", "unknown function 'Hexx'")]
    [InlineData("token T = HexIn(1);", "HexIn", "NGR0152", "'HexIn' takes 2 arguments, found 1")]
    [InlineData("token T = HexIn(\"4\", ['a']);", "\"4\"", "NGR0153", "argument 'width' of 'HexIn' expects Grammar.Int, found Grammar.Text")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = if n then \"a\" else \"b\";", "n then", "NGR0150", "expected a condition, found Grammar.Int")]
    [InlineData("function F(n: Expr) : Grammar.Rule = \"a\";", "Expr", "NGR0150", "a grammar function parameter is Grammar.Int, Grammar.Char, Grammar.Text, Grammar.Class or Grammar.Rule")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = n;", "n;", "NGR0150", "'n' is Grammar.Int, not a rule")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = [(n + \"a\")];", "n + \"a\"", "NGR0150", "'+' does not apply to Grammar.Int and Grammar.Text")]
    [InlineData("function F() : Grammar.Rule = \"a\"; function F() : Grammar.Rule = \"b\";", "F", "NGR0158", "'F' is already defined in module 'M'")]
    [InlineData("function char() : Grammar.Rule = \"a\";", "char", "NGR0158", "'char' is a built-in function")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = [char(n / 0)]; token T = F(1);", "F(1)", "NGR0155", "division by zero (in F)")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = [char(n)]; token T = F(70000);", "F(70000)", "NGR0155", "char(70000) is outside U+0000–U+FFFF (in F)")]
    [InlineData("function F(n: Grammar.Int) : Grammar.Rule = \"a\" F(n + 1); token T = F(0);", "F(0)", "NGR0154", "grammar function calls nest deeper than 200")]
    public void Reports_invalid_functions_and_calls(string members, string at, string code, string message)
    {
        string text = Module(members);
        var diagnostic = Single(members);
        Assert.Equal(code, diagnostic.Code);
        Assert.StartsWith(message, diagnostic.Message);
        Assert.Equal("M", diagnostic.Module);
        int start = text.LastIndexOf(at, StringComparison.Ordinal);
        Assert.Equal(new GrammarSpan(start, at.TrimEnd(';').Replace(" then", "").Length), diagnostic.Span);
    }

    [Fact]
    public void A_character_level_call_in_a_syntax_rule_is_a_warning()
    {
        var diagnostic = Single("syntax S = HexIn(2, ['A']);");
        Assert.Equal(GrammarCodes.CharacterLevelCall, diagnostic.Code);
        Assert.Equal(GrammarSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void A_function_name_without_a_call_gets_a_hint()
    {
        var result = GrammarCompiler.Compile(new[] { new GrammarInput("a.ngr", Module("syntax R = TrailingList;"), "N") });
        Assert.Contains(result.Diagnostics, d => d.Diagnostic.Message == "'TrailingList' is a grammar function; call it as TrailingList(...) with no space before '('");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_four_digit_value_matches_exactly_when_the_class_holds_it(bool negated)
    {
        var random = new Random(17);
        var ranges = new List<(int First, int Last)>();
        for (int at = random.Next(64); at <= char.MaxValue; at += random.Next(1, 700))
        {
            int last = Math.Min(char.MaxValue, at + (random.Next(3) == 0 ? 0 : random.Next(400)));
            ranges.Add((at, last));
            at = last + 1;
        }
        string items = string.Join(" ", ranges.Select(r => r.First == r.Last ? $"'\\u{r.First:x4}'" : $"'\\u{r.First:x4}'..'\\u{r.Last:x4}'"));
        var result = Expand($"token T = HexIn(4, [{(negated ? "^ " : "")}{items}]);");
        Assert.Empty(result.Diagnostics);
        var expansion = ((TokenRule)result.Modules[0].Rules[0]).Body;
        for (int value = 0; value <= char.MaxValue; value++)
        {
            bool member = ranges.Any(r => r.First <= value && value <= r.Last) != negated;
            Assert.True(member == (Match(expansion, value.ToString("x4"), 0) == 4), $"{value:x4}");
            Assert.True(member == (Match(expansion, value.ToString("X4"), 0) == 4), $"{value:X4}");
        }
    }

    /// <summary>A minimal matcher for the expansion's forms: the end of a match at <paramref name="at"/>, or -1.</summary>
    static int Match(Expr expr, string text, int at) => expr switch
    {
        CharClassExpr c => at < text.Length && c.Ranges.Any(r => r.First <= text[at] && text[at] <= r.Last) ? at + 1 : -1,
        SequenceExpr s => s.Items.Aggregate(at, (p, item) => p < 0 ? -1 : Match(item, text, p)),
        ChoiceExpr c => c.Alternatives.Select(a => Match(a, text, at)).FirstOrDefault(end => end >= 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    [Fact]
    public void An_expansion_takes_the_span_of_its_call()
    {
        const string members = "token T = \"x\" HexIn(2, ['A']);";
        var body = (SequenceExpr)((TokenRule)Expand(members).Modules[0].Rules[0]).Body;
        string text = Module(members);
        var call = new GrammarSpan(text.LastIndexOf("HexIn", StringComparison.Ordinal), "HexIn(2, ['A'])".Length);
        Assert.Equal(call, body.Items[1].Span);
        Assert.All(((SequenceExpr)body.Items[1]).Items, item => Assert.Equal(call, item.Span));
    }

    [Theory]
    [InlineData("\\u0041", 6)]
    [InlineData("\\u004a", 6)]
    [InlineData("\\u00E9", 6)]
    [InlineData("\\U00000041", 10)]
    [InlineData("\\u0030", -1)]
    [InlineData("\\u0020", -1)]
    [InlineData("\\u00d7", -1)]
    [InlineData("\\u004", -1)]
    [InlineData("\\U00010041", -1)]
    public void Generated_matcher_accepts_only_escapes_of_the_class(string text, int end) =>
        Assert.Equal(end, HexValuesModule.MatchEscapedLetter(text, 0));

    [Fact]
    public void Generated_matchers_use_the_other_functions()
    {
        Assert.Equal(6, HexValuesModule.MatchSelect("SeLeCt", 0));
        Assert.Equal(-1, HexValuesModule.MatchSelect("selekt", 0));
        Assert.Equal(3, HexValuesModule.MatchOctet("2550", 0));
        Assert.Equal(-1, HexValuesModule.MatchOctet("x", 0));
    }

    [Fact]
    public void Function_calls_and_rule_names_in_arguments_are_navigable()
    {
        const string grammar = "syntax module H { function Twice(Letter: Grammar.Rule) : Grammar.Rule = Letter Letter; " +
            "token Letter = ['A'..'Z']; token Escaped = \"\\\\u\" HexIn(4, Letter) Twice(Letter); syntax Program = Escaped; }";
        File.WriteAllText(Path.Combine(_root, "a.ngr"), grammar);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """{"languages":[{"name":"Test","extensions":[".test"],"start":"H.Program","grammars":["a.ngr"]}]}""");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string uri = new Uri(Path.Combine(_root, "a.ngr")).AbsoluteUri;
        service.Open(uri, 1, grammar);
        var map = new LineMap(grammar);
        int letter = grammar.IndexOf("token Letter", StringComparison.Ordinal) + "token ".Length;
        var definition = Assert.Single(service.Definition(uri, map.PositionOf(grammar.IndexOf("HexIn(4, Letter)", StringComparison.Ordinal) + 10)));
        Assert.Equal(map.RangeOf(new(letter, "Letter".Length)), definition.Range);
        var function = Assert.Single(service.Definition(uri, map.PositionOf(grammar.LastIndexOf("Twice", StringComparison.Ordinal) + 1)));
        Assert.Equal(map.RangeOf(new(grammar.IndexOf("Twice", StringComparison.Ordinal), "Twice".Length)), function.Range);
        // A parameter shadows the rule of the same name inside the body.
        var parameter = Assert.Single(service.Definition(uri, map.PositionOf(grammar.IndexOf("Letter Letter", StringComparison.Ordinal) + 1)));
        Assert.Equal(map.RangeOf(new(grammar.IndexOf("Letter: ", StringComparison.Ordinal), "Letter".Length)), parameter.Range);
    }

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-grammar-functions-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
