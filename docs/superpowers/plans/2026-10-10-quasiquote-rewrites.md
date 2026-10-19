# Quasiquoted Rewrite Rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Grammar rules can say `rewrites <[ template ]>`, and their uses are typed, checked and lowered as the template with each `$Field` hole filled by the use's child.

**Architecture:** Part 1: a template parse mode (`Language.ParseTemplate`) where `$Name` matches as a `Hole` leaf at the entry of its rule (generated plain and alias rules, and `ParseExtensible`). Part 2: the `rewrites` clause flows grammar → `DeclarativeRule` (form `Rewrite`, with a `RewriteSpec`) → runtime, where each use gets a per-use instantiation of the template (`FileSemantics` with holes proxying every property to the use's children, and the use proxying its undefined properties to the instantiation's root); `DeclarativeTypes` types and lowers through it.

**Tech Stack:** C# / .NET 10, the grammar compiler (`Nitrogen.Grammar`), the source generator, the self-hosted `.ngr` grammar (`Nitrogen.Ngr`), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-10-quasiquote-rewrites-design.md`

## Global Constraints

- Codes: NGR0135 (unknown hole), NGR0136 (hole for a non-nonterminal field) are validator **errors**; NGR0137 (template doesn't parse), NGR0138 (template declares a name) are grammar-workspace **warnings**; NH0008 (rewrite cycle), NH0009 (template can't be used) are lowering diagnostics at the use.
- A use's diagnostic from its expansion: same code, at the use's span, message prefixed `in the expansion of <Rule>: `.
- No behavior change for grammars without `rewrites`: every existing test passes unchanged.
- The bootstrap parser (`GrammarParser`) and the self-hosted parser (`NgrParser`) produce identical models, spans included (`SelfHostingTests`).
- Never commit `examples/DateCalc/sample.datecalc` or `examples/DateCalc/Snippets.cs` edits.

## Review Focus

- **A template mentioning the same hole twice** (`$X + $X`): both holes stand for the same child; it's lowered once and used twice. Covered in Task 3 (`Twice`).
- **A use whose child has an error**: the child's error stays at the child, the use isn't lowered, and nothing throws. Covered in Task 3.
- **A rewrite that uses itself** (`$Count loops` in `loops`): typing and lowering must stop (no stack overflow), reporting NH0008. Covered in Task 3.
- **A grammar edit that breaks a template while the editor is open**: the language keeps working; the grammar shows NGR0137; uses report NH0009. Covered in Task 4.
- **`$` in ordinary code**: a normal parse never treats `$Name` as a hole. Covered in Task 1.

---

## Files

- Runtime: `Nitrogen.Runtime/SyntaxKinds.cs` (`Hole`), `ParserState.cs` (`Holes`, `TemplateStart`, `TryHole`), `SyntaxModule.cs` (`ExtensionPointDecl.QualifiedName`), `ExtensibleRules.cs` (hole at entry), `Language.cs` (`ParseTemplate`, `RewriteTemplate`), `Semantic/DeclarativeRule.cs` (`Rewrite`, `RewriteSpec`, `RewriteHole`), `Semantic/DeclarativeLowering.cs` (root registration), `Semantic/DeclarativeTypes.cs` (hole and rewrite typing and lowering), `Semantic/HirLowering.cs` (`LoweringContext.Rewriting`), `Semantics/FileSemantics.cs` (instantiation, proxies, expansion diagnostics).
- Grammar: `Nitrogen.Grammar/GrammarModel.cs` (`Rewrites`), `GrammarParser.cs`, `GrammarValidator.cs`, `GrammarDiagnostic.cs` (codes), `GrammarDumper.cs`, `SyntaxCodeWriter.cs` (hole check), `DeclarativeWriter.cs` (emit `RewriteSpec`).
- Self-hosted: `Nitrogen.Ngr/Nitrogen.ngr`, `Nitrogen.Ngr/GrammarSemantics.cs`, `Nitrogen.Ngr/NgrProjector.cs`.
- Workspace: `Nitrogen.Workspace/GrammarWorkspace.cs`.
- Service: `Nitrogen.LanguageService/NitrogenLanguageService.cs` (hover).
- Tests: `Nitrogen.Tests/Grammars/Rewrites.ngr` (new, generator-compiled), `Nitrogen.Tests/Nitrogen.Tests.csproj`, `Nitrogen.Tests/Grammar/NgrGrammarTests.cs` (file list), new `Nitrogen.Tests/TemplateParsingTests.cs`, `Nitrogen.Tests/Grammar/RewriteGrammarTests.cs`, `Nitrogen.Tests/LanguageService/RewriteTests.cs`.
- Docs: `docs/language-guide.md`, the spec (status).

Commands (repo root): `dotnet build Nitrogen.slnx -warnaserror`; `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Class>"`; the whole suite.

---

### Task 1: Holes in the parser

**Files:** `Nitrogen.Runtime/SyntaxKinds.cs`, `ParserState.cs`, `SyntaxModule.cs`, `ExtensibleRules.cs`, `Language.cs`, `Nitrogen.Grammar/SyntaxCodeWriter.cs`; test `Nitrogen.Tests/TemplateParsingTests.cs`.

**Interfaces:**
- Produces: `SyntaxKinds.Hole` (= 8); `ParseResult Language.ParseTemplate(string text, string category, IReadOnlyDictionary<string, string> holes)` (category and hole values are qualified rule names, `Module.Rule`); `bool ParserState.TryHole(string rule)`; `string ExtensionPointDecl.QualifiedName`.

The tests use the generator-compiled Calc test grammar (`Nitrogen.Tests/Grammars/Calc.ngr`): `Program = Statements:Statement*`, `Statement = Assign / ExprStatement` (an alias), extensible `Expr` with `Num`, `Ref`, `Neg = "-" Expr precedence 30`, `Add` (10), `Mul` (20).

- [ ] **Step 1: Write the failing tests**

Create `Nitrogen.Tests/TemplateParsingTests.cs`:

```csharp
using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Parsing templates with holes (spec: quasiquote rewrites, part 1).</summary>
public class TemplateParsingTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
    static readonly Dictionary<string, string> Holes = new() { ["X"] = "Calc.Expr", ["S"] = "Calc.Statement" };

    /// <summary>The tree as kind names, holes as their text: <c>Mul(Hole $X, Num)</c>.</summary>
    static string Shape(SyntaxTree tree, int node)
    {
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Hole) return "Hole " + tree.GetText(node).ToString();
        string name = tree.Language!.GetKindName(kind);
        name = name[(name.LastIndexOf('.') + 1)..];
        // Tokens (leaves other than holes) are left out: the shape is the nodes.
        var children = Enumerable.Range(0, tree.ChildCount(node)).Select(i => tree.Child(node, i))
            .Where(child => tree.ChildCount(child) > 0 || tree.Kind(child) == SyntaxKinds.Hole).Select(child => Shape(tree, child)).ToList();
        return children.Count == 0 ? name : $"{name}({string.Join(", ", children)})";
    }

    static string Template(string text, string category = "Calc.Expr")
    {
        using var result = Calc.ParseTemplate(text, category, Holes);
        Assert.True(result.Success, $"'{text}' did not parse as {category}");
        return Shape(result.Tree, result.Tree.Root);
    }

    [Fact]
    public void A_hole_parses_as_its_rule_and_operators_extend_past_it()
    {
        Assert.Equal("Hole $X", Template("$X"));
        Assert.Equal("Mul(Hole $X, Num)", Template("$X * 2"));
        Assert.Equal("Add(Hole $X, Mul(Num, Hole $X))", Template("$X + 2 * $X"));
    }

    [Fact]
    public void A_hole_is_one_operand()
    {
        // Neg binds tighter than Mul: -$X * 2 is (-$X) * 2, and the hole is a single leaf.
        Assert.Equal("Mul(Neg(Hole $X), Num)", Template("-$X * 2"));
    }

    [Fact]
    public void A_hole_for_an_alias_rule_stands_where_the_alias_does()
    {
        Assert.Equal("Program(List(Hole $S))", Template("$S", "Calc.Program"));
    }

    [Fact]
    public void A_hole_matches_only_where_its_rule_is_parsed()
    {
        using var wrongKind = Calc.ParseTemplate("$S * 2", "Calc.Expr", Holes);
        Assert.False(wrongKind.Success);
        using var unknown = Calc.ParseTemplate("$Y * 2", "Calc.Expr", Holes);
        Assert.False(unknown.Success);
    }

    [Fact]
    public void A_normal_parse_never_matches_a_hole()
    {
        using var result = Calc.Parse("$X;", CalcModule.Program);
        Assert.True(result.HasErrors);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TemplateParsingTests"`
Expected: build error, `ParseTemplate` and `SyntaxKinds.Hole` don't exist.

- [ ] **Step 3: The `Hole` kind**

In `Nitrogen.Runtime/SyntaxKinds.cs`, after `public const int Skipped = 7;` add:

```csharp
    /// <summary>A template's hole (<c>$Name</c>): a leaf standing for a node of the rule the hole names (spec: quasiquote rewrites).</summary>
    public const int Hole = 8;
```

and extend `s_names` with `"Hole"` at the end.

- [ ] **Step 4: `ParserState`**

In `Nitrogen.Runtime/ParserState.cs`, after `internal Language? Language { get; init; }`:

```csharp
    /// <summary>A template parse's holes: hole name → qualified rule (spec: quasiquote rewrites). Null in a normal parse, so a normal parse never matches one.</summary>
    internal IReadOnlyDictionary<string, string>? Holes { get; init; }

    /// <summary>The extension point a template parse starts at, when its category is an extensible rule.</summary>
    internal ExtensionPointDecl? TemplateStart { get; init; }

    /// <summary>
    /// In a template parse, a hole for <paramref name="rule"/> (a qualified rule name) at the position, after
    /// trivia: pushes a <see cref="SyntaxKinds.Hole"/> leaf over <c>$Name</c> and returns true. Any other input,
    /// or a hole for another rule, leaves the state as it was.
    /// </summary>
    public bool TryHole(string rule)
    {
        if (Holes is null) return false;
        int at = PeekPastTrivia();
        if (at >= _text.Length || _text[at] != '$') return false;
        int end = at + 1;
        while (end < _text.Length && IsIdentifierPart(_text[end])) end++;
        if (end == at + 1 || !Holes.TryGetValue(_text[(at + 1)..end].ToString(), out var holeRule) || holeRule != rule) return false;
        Position = at;
        PushLeaf(SyntaxKinds.Hole, end);
        return true;
    }
```

- [ ] **Step 5: Extension points**

In `Nitrogen.Runtime/SyntaxModule.cs`, in `ExtensionPointDecl`, after `public int GlobalId { get; }`:

```csharp
    string? _qualifiedName;

    /// <summary><c>Module.Rule</c>: how templates name this extension point.</summary>
    public string QualifiedName => _qualifiedName ??= Owner.Name + "." + Name;
```

In `Nitrogen.Runtime/ExtensibleRules.cs`, `ParseExtensible`, replace `bool parsed = ParsePrefix(ref s, point);` with:

```csharp
        // A template's hole is a whole prefix result: operators may extend past it, never into it.
        bool parsed = s.TryHole(point.Decl.QualifiedName) || ParsePrefix(ref s, point);
```

- [ ] **Step 6: Plain and alias rules**

In `Nitrogen.Grammar/SyntaxCodeWriter.cs`, `WriteRule`:

1. The alias branch: change the appended expression's start so the hole check comes first:

```csharp
            b.Append($"    internal static bool Parse{rule.Name}(ref ParserState s) =>\n        ")
                .Append($"s.TryHole({CSharpText.Literal(module.Name + "." + rule.Name)})\n        || ")
                .Append(string.Join("\n        || ", alternatives.Select(a => Element(a, module, null, 0))))
                .Append(";\n\n");
```

2. The plain branch: right after `b.Append($"    internal static bool Parse{rule.Name}(ref ParserState s)\n    {{\n");` add:

```csharp
        b.Append($"        if (s.TryHole({CSharpText.Literal(module.Name + "." + rule.Name)})) return true;\n");
```

(`module` is the `ModuleDecl` already in scope in `WriteRule`.)

- [ ] **Step 7: `Language.ParseTemplate`**

In `Nitrogen.Runtime/Language.cs`:

1. Change `bool Run(string text, Rule start, ParseContext context, bool recovering)` to take two optional parameters and pass them to the state:

```csharp
    bool Run(string text, Rule start, ParseContext context, bool recovering,
        IReadOnlyDictionary<string, string>? holes = null, ExtensionPointDecl? templateStart = null)
    {
        …
        var state = new ParserState(text, arena, context.Memo, Trivia, TriviaStart) { Language = this, Holes = holes, TemplateStart = templateStart };
```

2. Add:

```csharp
    /// <summary>
    /// Parses <paramref name="text"/> as the rule <paramref name="category"/> (qualified: <c>Module.Rule</c>, a
    /// syntax or extensible rule), where each <c>$Name</c> of <paramref name="holes"/> stands for a node of the
    /// rule it maps to (spec: quasiquote rewrites). The fast pass only: a template either parses or doesn't.
    /// </summary>
    public ParseResult ParseTemplate(string text, string category, IReadOnlyDictionary<string, string> holes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(holes);
        int dot = category.LastIndexOf('.');
        string moduleName = dot < 0 ? "" : category[..dot], ruleName = category[(dot + 1)..];
        Rule start;
        ExtensionPointDecl? point = null;
        if (Modules.FirstOrDefault(m => m.Name == moduleName)?.GetRule(ruleName) is { } rule) start = rule;
        else
        {
            point = _points.FirstOrDefault(p => p is not null && p.Decl.QualifiedName == category)?.Decl
                ?? throw new ArgumentException($"'{category}' is not a rule of this language.", nameof(category));
            start = new Rule(category, &ParseTemplateStart);
        }
        var context = ParseContext.Acquire();
        try
        {
            var arena = context.Arena;
            if (Run(text, start, context, recovering: false, holes, point))
                return ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], text, this), arena);
            return ParseResult.Failed(TreeFreezer.ErrorTree(text, this), arena, ParseResult.CaptureFailure(arena));
        }
        finally
        {
            ParseContext.Release(context);
        }
    }

    static bool ParseTemplateStart(ref ParserState s) => s.ParseExtensible(s.TemplateStart!, 0);
```

   (If `ExtensionPoint` exposes its declaration under another name than `Decl`, use that; `ExtensibleRules` reads `point.Decl.Name`.)

- [ ] **Step 8: Run the tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`, then `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TemplateParsingTests"`.
Expected: 5 passed. Then the whole suite: all pass (every generated parser now starts with a `TryHole` call that returns false at once).

- [ ] **Step 9: Commit**

```bash
git add Nitrogen.Runtime Nitrogen.Grammar/SyntaxCodeWriter.cs Nitrogen.Tests/TemplateParsingTests.cs
git commit -m "Parse templates with holes standing for rules

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: The `rewrites` clause in grammars

**Files:** `Nitrogen.Grammar/GrammarModel.cs`, `GrammarParser.cs`, `GrammarValidator.cs`, `GrammarDiagnostic.cs`, `GrammarDumper.cs`, `DeclarativeWriter.cs`; `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`; `Nitrogen.Ngr/Nitrogen.ngr`, `GrammarSemantics.cs`, `NgrProjector.cs`; tests `Nitrogen.Tests/Grammar/RewriteGrammarTests.cs`, `Nitrogen.Tests/Grammars/Rewrites.ngr`, `Nitrogen.Tests/Nitrogen.Tests.csproj`, `Nitrogen.Tests/Grammar/NgrGrammarTests.cs`.

**Interfaces:**
- Consumes: Task 1's hole check (generated code calls `TryHole`).
- Produces: `BindingClauseKind.Rewrites` (its `Target` is `NameDecl(templateText, templateSpan)`: the text between `<[` and `]>`, verbatim; the clause span runs from `rewrites` to the end of `]>`); `DeclarativeForm.Rewrite`; `public sealed record RewriteHole(string Name, int Child, string Rule)`; `public sealed record RewriteSpec(string Text, string Category, IReadOnlyList<RewriteHole> Holes)`; `DeclarativeRule.Rewrite` (a `RewriteSpec?`, non-null exactly for `Rewrite`); codes `GrammarCodes.UnknownHole = "NGR0135"`, `HoleNotARule = "NGR0136"`, `TemplateDoesNotParse = "NGR0137"`, `TemplateDeclares = "NGR0138"`.

- [ ] **Step 1: Write the failing tests**

Create `Nitrogen.Tests/Grammars/Rewrites.ngr`:

```ngr
// Rewrite rules for the tests of quasiquoted rewrites: a statement form and an operator form.
syntax module Rewrites
{
  token Number = ['0'..'9']+;

  syntax Program   = Statements:Statement*;
  syntax Statement = Show / Twice;
  syntax Show      = Value:Expr ";";
  syntax Twice     = "twice" Value:Expr ";"  rewrites <[ $Value + $Value; ]>;

  extensible syntax Expr
  {
    | Num     = Value:Number
    | Add     = Left:Expr "+" Right:Expr  precedence 10 left
    | Mul     = Left:Expr "*" Right:Expr  precedence 20 left
    | Doubled = Inner:Expr "doubled"  precedence 30  rewrites <[ $Inner * 2 ]>
  }
}
```

In `Nitrogen.Tests/Nitrogen.Tests.csproj`, after the `Counted.ngr` `AdditionalFiles` line:

```xml
    <AdditionalFiles Include="Grammars\Rewrites.ngr" Namespace="Nitrogen.Tests.Rewrites" />
```

In `Nitrogen.Tests/Grammar/NgrGrammarTests.cs`, add `"Rewrites.ngr"` to `GrammarFiles()`.

Create `Nitrogen.Tests/Grammar/RewriteGrammarTests.cs`:

```csharp
using Nitrogen.Grammar;
using Nitrogen.Semantic;
using Nitrogen.Tests.Rewrites;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The rewrites clause from grammar text to generated rules (spec: quasiquote rewrites).</summary>
public class RewriteGrammarTests
{
    static BindingClause RewriteClause(string grammar)
    {
        var result = GrammarParser.Parse(grammar);
        Assert.True(result.Success, result.Diagnostics.Count > 0 ? result.Diagnostics[0].ToString() : "");
        var rule = (SyntaxRule)result.File!.Modules[0].Rules.Single(r => r.Name == "R");
        return rule.Clauses.Single(c => c.Kind == BindingClauseKind.Rewrites);
    }

    [Fact]
    public void The_template_is_kept_verbatim_with_its_span()
    {
        const string grammar = "syntax module M { token N = ['0'..'9']+; syntax R = \"r\" X:N  rewrites <[ a\n  ]>; }";
        var clause = RewriteClause(grammar);
        int open = grammar.IndexOf("<[", StringComparison.Ordinal) + 2;
        Assert.Equal(" a\n  ", clause.Target!.Name);
        Assert.Equal(open, clause.Target.Span.Start);
        Assert.Equal(grammar.IndexOf("rewrites", StringComparison.Ordinal), clause.Span.Start);
        Assert.Equal(grammar.IndexOf("]>", StringComparison.Ordinal) + 2, clause.Span.End);
    }

    static GrammarDiagnostic[] Validate(string grammar) =>
        GrammarValidator.Validate(GrammarParser.Parse(grammar).File!.Modules).ToArray();

    [Fact]
    public void An_unknown_hole_and_a_hole_for_a_token_are_errors()
    {
        var unknown = Assert.Single(Validate("syntax module M { token N = ['0'..'9']+; syntax R = \"r\" X:S  rewrites <[ $Y ]>; syntax S = N; }"));
        Assert.Equal("NGR0135", unknown.Code);
        var token = Assert.Single(Validate("syntax module M { token N = ['0'..'9']+; syntax R = \"r\" X:N  rewrites <[ $X ]>; }"));
        Assert.Equal("NGR0136", token.Code);
    }

    [Fact]
    public void A_rule_takes_lowers_or_rewrites_not_both()
    {
        var both = Validate("syntax module M { token N = ['0'..'9']+; syntax S = N; syntax R = \"r\" X:S  lowers literal Core.Scalar X  rewrites <[ $X ]>; }");
        Assert.Contains(both, d => d.Code == GrammarCodes.DuplicateClause);
    }

    [Fact]
    public void The_generated_rules_carry_the_template_its_category_and_its_holes()
    {
        var rewrites = RewritesModule.Instance.DeclarativeRules.Where(r => r.Form == DeclarativeForm.Rewrite).ToList();
        var doubled = Assert.Single(rewrites, r => r.Rewrite!.Category == "Rewrites.Expr").Rewrite!;
        Assert.Equal(" $Inner * 2 ", doubled.Text);
        Assert.Equal(new RewriteHole("Inner", 0, "Rewrites.Expr"), Assert.Single(doubled.Holes));

        // A plain rule listed by one alias parses its template as that alias.
        var twice = Assert.Single(rewrites, r => r.Rewrite!.Category == "Rewrites.Statement").Rewrite!;
        Assert.Equal(new RewriteHole("Value", 1, "Rewrites.Expr"), Assert.Single(twice.Holes));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RewriteGrammarTests"`
Expected: build error (`BindingClauseKind.Rewrites`, `DeclarativeForm.Rewrite`, `RewriteHole` don't exist; the generator reports `Rewrites.ngr`'s `rewrites` as a syntax error).

- [ ] **Step 3: Model and codes**

`Nitrogen.Grammar/GrammarModel.cs`: add `Rewrites,` after `LowersExpand,` in `BindingClauseKind`; in the `BindingClause` doc, extend `Target`'s description with `; Rewrites: the template text between <[ and ]>, verbatim, with its span`.

`Nitrogen.Grammar/GrammarDiagnostic.cs`, after `InvalidTemplateClause`:

```csharp
    public const string UnknownHole = "NGR0135";
    public const string HoleNotARule = "NGR0136";
    public const string TemplateDoesNotParse = "NGR0137";
    public const string TemplateDeclares = "NGR0138";
```

- [ ] **Step 4: The bootstrap parser**

In `Nitrogen.Grammar/GrammarParser.cs`:

1. `IsClauseWord`: add `or "rewrites"`.
2. In `ParseClauses`, before the `else if (AtKeyword("scope") || AtKeyword("dynamic"))` branch:

```csharp
                else if (AtKeyword("rewrites"))
                {
                    var keyword = Advance();
                    clauses.Add(Rewrite(keyword));
                }
```

3. A method next to `Code(…)`:

```csharp
        /// <summary>
        /// <c>rewrites &lt;[ text ]&gt;</c> (spec: quasiquote rewrites): the text runs verbatim to the first <c>]&gt;</c>,
        /// never tokenized. Lexing resumes after it.
        /// </summary>
        BindingClause Rewrite(GrammarToken keyword)
        {
            int open = GrammarLexer.SkipTrivia(_text, keyword.End);
            if (string.CompareOrdinal(_text, open, "<[", 0, 2) != 0)
                throw new GrammarSyntaxException("expected '<[' after 'rewrites'", new GrammarSpan(open, 0));
            int start = open + 2, close = _text.IndexOf("]>", start, StringComparison.Ordinal);
            if (close < 0) throw new GrammarSyntaxException("expected ']>' to close the template", new GrammarSpan(open, 2));
            _tokens.RemoveRange(_position, _tokens.Count - _position);
            _lexed = close + 2;
            return new BindingClause(BindingClauseKind.Rewrites, default, "", default, false, false,
                GrammarSpan.FromBounds(keyword.Start, close + 2),
                Target: new NameDecl(_text.Substring(start, close - start), GrammarSpan.FromBounds(start, close)));
        }
```

- [ ] **Step 5: The validator**

In `Nitrogen.Grammar/GrammarValidator.cs`, in the clause loop (where `seen` is built):

1. Count `Rewrites` as a lowering for the at-most-one check: add `or BindingClauseKind.Rewrites` to the list that maps to `BindingClauseKind.Lowers`, and in `Keyword(…)` map `BindingClauseKind.Rewrites => "lowers' or 'rewrites"` so the message reads naturally.
2. After the `LowersExpand` check, add:

```csharp
                if (clause.Kind == BindingClauseKind.Rewrites)
                    foreach (System.Text.RegularExpressions.Match hole in System.Text.RegularExpressions.Regex.Matches(clause.Target!.Name, @"\$([A-Za-z_][A-Za-z0-9_]*)"))
                    {
                        string name = hole.Groups[1].Value;
                        var span = new GrammarSpan(clause.Target.Span.Start + hole.Index, hole.Length);
                        var field = SyntaxCodeWriter.Elements(body).OfType<LabeledExpr>().FirstOrDefault(element => element.Label == name);
                        if (field is null)
                            Report(GrammarCodes.UnknownHole, $"'${name}' is not a field of this rule", span, module);
                        else if (field.Inner is not ReferenceExpr reference ||
                                 Resolve(reference, module) is not { Rule: SyntaxRule or ExtensibleRule })
                            Report(GrammarCodes.HoleNotARule, $"'${name}' must be a field of a syntax or extensible rule to be a hole", span, module);
                    }
```

   `Resolve(reference, module)` stands for the validator's existing way of resolving a rule reference to its `GrammarSymbol` (it validates references for NGR0101 `UndefinedRule`); call that helper. A reference that doesn't resolve is already NGR0101, so report nothing more for it.

- [ ] **Step 6: The runtime rule**

In `Nitrogen.Runtime/Semantic/DeclarativeRule.cs`:

1. In `DeclarativeForm`, after `Expand`:

```csharp
    /// <summary>A rule whose uses mean its quasiquoted template (spec: quasiquote rewrites); see <see cref="DeclarativeRule.Rewrite"/>.</summary>
    Rewrite,
```

2. Before `public sealed class DeclarativeRule`:

```csharp
/// <summary>A rewrite template's hole: the field it names, that field's child index in a use, and the qualified rule it stands for.</summary>
public sealed record RewriteHole(string Name, int Child, string Rule);

/// <summary>A rule's rewrite: the template text (verbatim), the qualified rule it parses as, and its holes.</summary>
public sealed record RewriteSpec(string Text, string Category, IReadOnlyList<RewriteHole> Holes);
```

3. The constructor gains a last parameter `RewriteSpec? rewrite = null`, the check `if ((form == DeclarativeForm.Rewrite) != (rewrite is not null)) throw new ArgumentException("A rewrite needs exactly its template.", nameof(rewrite));`, the assignment `Rewrite = rewrite;`, and the property:

```csharp
    /// <summary>The template a rewrite's uses mean; null for every other form.</summary>
    public RewriteSpec? Rewrite { get; }
```

- [ ] **Step 7: The generator**

In `Nitrogen.Grammar/DeclarativeWriter.cs`, `Entry`:

1. Add `or BindingClauseKind.Rewrites` to the `lowers` lookup.
2. Before `else if (lowers is not null)`, add:

```csharp
        else if (lowers is { Kind: BindingClauseKind.Rewrites })
        {
            form = "Rewrite";
            string implicitModule = kind.ImplicitModule ?? info.Module.Name;
            string Qualified(GrammarSymbol symbol) => symbol.Module.Name + "." + symbol.Rule.Name;
            var holes = new List<string>();
            foreach (var name in System.Text.RegularExpressions.Regex.Matches(lowers.Target!.Name, @"\$([A-Za-z_][A-Za-z0-9_]*)")
                         .Select(m => m.Groups[1].Value).Distinct())
            {
                var field = elements.OfType<LabeledExpr>().First(element => element.Label == name);
                var rule = model.Resolve(((ReferenceExpr)field.Inner).Name, info.Module, implicitModule);
                holes.Add($"new global::Nitrogen.Semantic.RewriteHole({CSharpText.Literal(name)}, {BindingWriter.ChildIndex(elements, name)}, {CSharpText.Literal(Qualified(rule))})");
            }
            extraArguments += $", rewrite: new global::Nitrogen.Semantic.RewriteSpec({CSharpText.Literal(lowers.Target.Name)}, " +
                $"{CSharpText.Literal(Category(model, info, kind))}, new global::Nitrogen.Semantic.RewriteHole[] {{ {string.Join(", ", holes)} }})";
        }
```

3. A helper in the class:

```csharp
    /// <summary>
    /// What a rewrite's template parses as: an alternative's extension point; a plain rule's alias when exactly one
    /// alias of its module lists it; otherwise the rule itself.
    /// </summary>
    static string Category(EmitModel model, ModuleInfo info, KindInfo kind)
    {
        if (kind.Point is { } point) return point.Module.Name + "." + point.Rule.Name;
        var rule = (SyntaxRule)kind.Rule!;
        var aliases = info.Module.Rules.OfType<SyntaxRule>().Where(EmitModel.IsAlias)
            .Where(alias => (alias.Body is ChoiceExpr choice ? choice.Alternatives.ToList() : new List<Expr> { alias.Body })
                .Any(alternative => alternative is ReferenceExpr reference && reference.Name == rule.Name))
            .ToList();
        return info.Module.Name + "." + (aliases.Count == 1 ? aliases[0].Name : rule.Name);
    }
```

   (`model.Resolve(name, module, implicitModule)` is the resolver `SyntaxCodeWriter` uses; `GrammarSymbol` has `Module` and `Rule`. If `ModuleDecl.Rules` isn't the right collection name, use the one `ModuleWriter` iterates.)

   Note `extraArguments` must already be the positional list it is for operations or empty; `rewrite:` is a named argument, so it can follow either.

- [ ] **Step 8: The self-hosted grammar**

In `Nitrogen.Ngr/Nitrogen.ngr`:

1. With the tokens (after `CsCode`):

```ngr
  token Template  = "<[" (!"]>" .)* "]>";
```

2. Replace `syntax Clause         = Declares / References / ScopeClause / DynamicClause / Lowers;` with:

```ngr
  syntax Clause         = Declares / References / ScopeClause / DynamicClause / Lowers / Rewrites;
  syntax Rewrites       = "rewrites" Text:Template  lowers Grammar.Rewrites(text Text);
```

In `Nitrogen.Ngr/GrammarSemantics.cs`, after `Op("Dynamic", Types.Clause),`:

```csharp
        Op("Rewrites", Types.Clause, Text),
```

In `Nitrogen.Ngr/NgrProjector.cs`, `Clauses`, before `case "Lowers":`:

```csharp
                case "Rewrites":
                {
                    // The token includes its delimiters; the bootstrap parser keeps the text between them.
                    var token = Span(a[0]);
                    string text = Text(a[0]);
                    clauses.Add(new BindingClause(BindingClauseKind.Rewrites, default, "", default, false, false,
                        GrammarSpan.FromBounds(Span(node).Start, token.End),
                        Target: new NameDecl(text[2..^2], GrammarSpan.FromBounds(token.Start + 2, token.End - 2))));
                    break;
                }
```

In `Nitrogen.Grammar/GrammarDumper.cs`, `Dump(BindingClause)`: add a case `BindingClauseKind.Rewrites => $"(rewrites {CSharpText.Literal(clause.Target!.Name)})",` (follow the neighbouring cases' style).

If the `.ngr` editor colors tokens by kind (`Nitrogen.Cli/NgrAssist.cs`, `NgrStyles`), give `Template` the string style used for `String`.

- [ ] **Step 9: Run the tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`, then `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RewriteGrammarTests|FullyQualifiedName~SelfHostingTests|FullyQualifiedName~NgrGrammarTests|FullyQualifiedName~GrammarValidatorTests|FullyQualifiedName~GrammarParserTests"`.
Expected: all pass, including `SelfHostingTests.Every_grammar_file_maps_to_the_bootstrap_model("Rewrites.ngr")` (both parsers agree, spans included). Then the whole suite.

- [ ] **Step 10: Commit**

```bash
git add Nitrogen.Grammar Nitrogen.Runtime/Semantic/DeclarativeRule.cs Nitrogen.Ngr Nitrogen.Cli/NgrAssist.cs Nitrogen.Tests/Grammars/Rewrites.ngr Nitrogen.Tests/Nitrogen.Tests.csproj Nitrogen.Tests/Grammar
git commit -m "Add the rewrites clause to grammars

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Uses: types, checks and lowering

**Files:** `Nitrogen.Runtime/Language.cs`, `Semantics/FileSemantics.cs`, `Semantic/DeclarativeTypes.cs`, `Semantic/HirLowering.cs`, `Semantic/DeclarativeLowering.cs`; test `Nitrogen.Tests/LanguageService/RewriteTests.cs`.

**Interfaces:**
- Consumes: `Language.ParseTemplate`, `DeclarativeRule.Rewrite`, `SyntaxKinds.Hole`.
- Produces: `public sealed record RewriteTemplate(ParseResult? Parsed, string? Error)` and `RewriteTemplate Language.RewriteTemplate(DeclarativeRule rule)` (cached per rule); `FileSemantics.Instance(int node)` (internal), `FileSemantics.Holes` (internal); `LoweringContext.Rewriting(FileSemantics, IReadOnlyDictionary<int, HirNode>)` (internal); codes NH0008, NH0009.

The tests use DateCalc through the grammar workspace (the language service), with DateCalc's grammar extended in a temporary folder.

- [ ] **Step 1: Write the failing tests**

Create `Nitrogen.Tests/LanguageService/RewriteTests.cs`:

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Uses of rewrite rules in a DateCalc grammar extended with them (spec: quasiquote rewrites).</summary>
public sealed class RewriteTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-rewrites-").FullName;

    internal const string Alternatives = """
            | Fortnights = Count:Expr "fortnights" precedence 30  rewrites <[ $Count * 2 weeks ]>
            | NextDay    = Date:Expr "next" "day" precedence 30  rewrites <[ $Date + 1 days ]>
            | Doubled    = Value:Expr "doubled" precedence 30  rewrites <[ $Value + $Value ]>
            | Monthish   = Count:Expr "monthish" precedence 30  rewrites <[ $Count * 2 fortnights ]>
            | Loops      = Count:Expr "loops" precedence 30  rewrites <[ $Count loops ]>

        """;

    public RewriteTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        string grammar = Path.Combine(_root, "DateCalc.ngr");
        string text = File.ReadAllText(grammar);
        int days = text.IndexOf("    | Days    =", StringComparison.Ordinal);
        File.WriteAllText(grammar, text.Insert(days, Alternatives));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        return service;
    }

    /// <summary>A lowered node without origins: operations, constants and the rest by type.</summary>
    static string Shape(HirNode node) => node switch
    {
        HirOperation operation => $"{operation.Signature.Id}({string.Join(", ", operation.Arguments.Select(Shape))})",
        HirConstant constant => $"{constant.Value}:{constant.Type}",
        _ => $"{node.GetType().Name}:{node.Type}",
    };

    string Lowered(NitrogenLanguageService service, string name, string text)
    {
        service.Open(Uri(name), 1, text);
        var inspection = service.InspectDocument(Uri(name))!;
        Assert.Empty(inspection.Diagnostics.Where(d => d.Code is not ("NH0001" or "NH0002" or "NH0003")));
        return Shape(Assert.Single(inspection.Roots));
    }

    [Fact]
    public void A_use_lowers_as_its_expansion()
    {
        using var service = Service();
        Assert.Equal(Lowered(service, "b.datecalc", "3 * 2 weeks;"), Lowered(service, "a.datecalc", "3 fortnights;"));
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")));
    }

    [Fact]
    public void A_use_has_its_expansions_type_and_passes_it_on()
    {
        using var service = Service();
        const string text = "let x = 3 fortnights;\nx;";
        service.Open(Uri("a.datecalc"), 1, text);
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")));
        Assert.Contains("DateCalc.Duration", service.Hover(Uri("a.datecalc"), new DocumentPosition(1, 0))!.Markdown);
    }

    [Fact]
    public void A_use_shows_its_value()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "2026-10-05 next day;");
        var whole = new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(1, 0));
        Assert.Equal("= 2026-10-06 Tue", Assert.Single(service.ValueHints(Uri("a.datecalc"), whole)).Label);
    }

    [Fact]
    public void An_error_in_the_expansion_is_reported_at_the_use()
    {
        using var service = Service();
        const string text = "3 next day;";
        service.Open(Uri("a.datecalc"), 1, text);
        var error = Assert.Single(service.Diagnostics(Uri("a.datecalc")), d => d.Severity == ServiceSeverity.Error);
        Assert.Equal("DC0002", error.Code);
        Assert.StartsWith("in the expansion of", error.Message);
        Assert.Contains("NextDay", error.Message);
        Assert.Equal(new DocumentPosition(0, 0), error.Range.Start);
    }

    [Fact]
    public void An_error_in_a_child_stays_at_the_child()
    {
        using var service = Service();
        const string text = "2026-02-30 next day;";
        service.Open(Uri("a.datecalc"), 1, text);
        var error = Assert.Single(service.Diagnostics(Uri("a.datecalc")), d => d.Severity == ServiceSeverity.Error);
        Assert.Equal("DC0001", error.Code);
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 10)), error.Range);
    }

    [Fact]
    public void A_hole_used_twice_stands_for_the_same_child()
    {
        using var service = Service();
        Assert.Equal(Lowered(service, "b.datecalc", "(1 + 2) + (1 + 2);"), Lowered(service, "a.datecalc", "(1 + 2) doubled;"));
    }

    [Fact]
    public void A_rewrite_using_another_expands_both()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "1 monthish;\n1 * (2 * 2 weeks);");
        var whole = new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(2, 0));
        var hints = service.ValueHints(Uri("a.datecalc"), whole);
        Assert.Equal(2, hints.Count);
        Assert.Equal(hints[1].Label, hints[0].Label);
    }

    [Fact]
    public void A_cycle_is_reported_and_stops()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "1 loops;");
        Assert.Contains(service.InspectDocument(Uri("a.datecalc"))!.Diagnostics, d => d.Code == "NH0008");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RewriteTests"`
Expected: the uses don't lower or type (no lowering root, no instantiation): `A_use_lowers_as_its_expansion` fails with no root for `3 fortnights;`, and the others accordingly. (A failure from the grammar not compiling means Task 2 is incomplete; fix that first.)

- [ ] **Step 3: Parsed templates on the language**

In `Nitrogen.Runtime/Language.cs`:

```csharp
    readonly System.Collections.Concurrent.ConcurrentDictionary<Nitrogen.Semantic.DeclarativeRule, RewriteTemplate> _rewrites = new();

    /// <summary>
    /// A rewrite rule's template, parsed once as its category with its holes (spec: quasiquote rewrites); or why it
    /// can't be used: it doesn't parse, or it declares a name.
    /// </summary>
    public RewriteTemplate RewriteTemplate(Nitrogen.Semantic.DeclarativeRule rule) => _rewrites.GetOrAdd(rule, r =>
    {
        var spec = r.Rewrite ?? throw new ArgumentException("Not a rewrite rule.", nameof(rule));
        var holes = spec.Holes.ToDictionary(h => h.Name, h => h.Rule);
        ParseResult parsed;
        try { parsed = ParseTemplate(spec.Text.Trim(), spec.Category, holes); }
        catch (ArgumentException error) { return new RewriteTemplate(null, error.Message); }
        if (!parsed.Success)
        {
            parsed.Dispose();
            return new RewriteTemplate(null, $"'{spec.Text.Trim()}' does not parse as {spec.Category}");
        }
        var project = new Nitrogen.Binding.Project(this);
        project.Set("rewrite", parsed.Tree);
        if (project["rewrite"].Declarations.Count > 0)
            return new RewriteTemplate(null, "a template cannot declare names");
        return new RewriteTemplate(parsed, null);
    });
```

and, at the end of the file:

```csharp
/// <summary>A rewrite rule's parsed template, or why it can't be used.</summary>
public sealed record RewriteTemplate(ParseResult? Parsed, string? Error);
```

The template text is trimmed before parsing, so hole and node spans in its tree are relative to the trimmed text; nothing maps them back to the grammar.

- [ ] **Step 4: The instantiation and the proxies**

In `Nitrogen.Runtime/Semantics/FileSemantics.cs`:

1. Fields and members:

```csharp
    /// <summary>A rewrite's use as a per-use instantiation of its template: its semantics, the template's root, and its rule.</summary>
    internal sealed record RewriteInstance(FileSemantics File, int Root, Nitrogen.Semantic.DeclarativeRule Rule);

    readonly Dictionary<int, RewriteInstance?> _instances = new();
    readonly HashSet<int> _cyclic = new();

    /// <summary>In an instantiation: the use's document, and for each Hole node the use's child it stands for.</summary>
    internal (FileSemantics File, IReadOnlyDictionary<int, int> Children)? Holes { get; private set; }

    /// <summary>In an instantiation: the rules being expanded around it, to stop a rewrite that reaches itself.</summary>
    System.Collections.Immutable.ImmutableHashSet<Nitrogen.Semantic.DeclarativeRule> _chain =
        System.Collections.Immutable.ImmutableHashSet<Nitrogen.Semantic.DeclarativeRule>.Empty;

    /// <summary>Whether <paramref name="node"/> uses a rewrite that reaches itself (NH0008 when lowered).</summary>
    internal bool IsCyclicRewrite(int node) => Instance(node) is null && _cyclic.Contains(node);

    /// <summary>The instantiation of the rewrite <paramref name="node"/> uses; null when its kind has none, its template can't be used, or it is cyclic.</summary>
    internal RewriteInstance? Instance(int node)
    {
        if (_language.Declarative.RuleFor(Tree.Kind(node))?.Rule is not { Form: Nitrogen.Semantic.DeclarativeForm.Rewrite, Rewrite: { } spec } rule)
            return null;
        if (_instances.TryGetValue(node, out var known)) return known;
        RewriteInstance? instance = null;
        if (_chain.Contains(rule)) _cyclic.Add(node);
        else if (_language.RewriteTemplate(rule).Parsed is { } parsed)
        {
            var template = parsed.Tree;
            var project = new Nitrogen.Binding.Project(_language);
            project.Set("rewrite", template);
            var file = new ProjectSemantics(project)["rewrite"];
            var children = new Dictionary<int, int>();
            for (int hole = 0; hole < template.NodeCount; hole++)
                if (template.Kind(hole) == SyntaxKinds.Hole)
                {
                    string name = template.GetText(hole)[1..].ToString();
                    children[hole] = Tree.Child(node, spec.Holes.First(h => h.Name == name).Child);
                }
            file.Holes = (this, children);
            file._chain = _chain.Add(rule);
            instance = new RewriteInstance(file, template.Root, rule);
        }
        _instances[node] = instance;
        return instance;
    }
```

2. At the top of `Get<T>`:

```csharp
        // A template's hole reads every property on the use's child it stands for.
        if (Holes is { } holes && Tree.Kind(node) == SyntaxKinds.Hole && holes.Children.TryGetValue(node, out int child))
            return holes.File.Get(child, property);
```

3. In `Synthesized<T>`, replace the final `return property.Default();` with:

```csharp
        // A rewrite's use: what its own rule doesn't define comes from its expansion.
        if (Instance(node) is { } instance) return instance.File.Get(instance.Root, property);
        return property.Default();
```

4. Expansion diagnostics in `Diagnostics()`: compute once and include them, and in the counts:

```csharp
    List<SemanticDiagnostic>? _expansions;

    /// <summary>Each rewrite use's expansion diagnostics, at the use, prefixed with the rule's name.</summary>
    List<SemanticDiagnostic> ExpansionDiagnostics()
    {
        if (_expansions is not null) return _expansions;
        var found = new List<SemanticDiagnostic>();
        for (int node = 0; node < Tree.NodeCount; node++)
            if (Instance(node) is { } instance)
            {
                string name = _language.GetKindName(Tree.Kind(node));
                name = name[(name.LastIndexOf('.') + 1)..];
                foreach (var diagnostic in instance.File.Diagnostics())
                    found.Add(new SemanticDiagnostic(diagnostic.Code, Tree.Span(node), $"in the expansion of {name}: {diagnostic.Message}"));
            }
        return _expansions = found;
    }
```

   In `Diagnostics()`, use `var expansions = ExpansionDiagnostics();`, add `expansions.Count` to the counts tuple (`(int Checks, int Evaluation, int Declarative, int Expansions)`), and concatenate `expansions` with the other lists before sorting. In `DiagnosticsForSubtree`, also concatenate the expansion diagnostics whose span lies in the subtree's span.

- [ ] **Step 5: Typing and lowering**

In `Nitrogen.Runtime/Semantic/HirLowering.cs`, `LoweringContext`:

```csharp
    /// <summary>The context of a rewrite's instantiation: each Hole node lowers to the use's child, lowered in the caller; reports go to the caller.</summary>
    LoweringContext(FileSemantics file, LoweringContext caller, IReadOnlyDictionary<int, HirNode> holes)
        : this(file, caller.SnapshotId, caller.CheckScope, caller.Admission)
    {
        _reported = caller._reported;
        _activeTemplates = caller._activeTemplates;
        Holes = holes;
    }

    /// <summary>In a rewrite's instantiation: the lowered child each Hole node stands for; null elsewhere.</summary>
    internal IReadOnlyDictionary<int, HirNode>? Holes { get; }

    internal LoweringContext Rewriting(FileSemantics instance, IReadOnlyDictionary<int, HirNode> holes) => new(instance, this, holes);
```

In `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs`:

1. `Compute`, at the start (after the Missing check):

```csharp
        if (kind == SyntaxKinds.Hole)
            return _file.Holes is { } holes && holes.Children.TryGetValue(node, out int child)
                ? holes.File.DeclarativeTypes.TypeOf(child) : SemanticTypes.Error;
```

   (move `int kind = tree.Kind(node);` above it), and in the form switch:

```csharp
                DeclarativeForm.Rewrite => _file.Instance(node) is { } instance
                    ? instance.File.DeclarativeTypes.TypeOf(instance.Root) : SemanticTypes.Error,
```

2. `Lower`, at the start:

```csharp
        if (_file.Tree.Kind(node) == SyntaxKinds.Hole)
            return context.Holes is { } holes && holes.TryGetValue(node, out var lowered) ? lowered : null;
        if (_lowering.RuleFor(_file.Tree.Kind(node)) is { Rule.Form: DeclarativeForm.Rewrite } rewrite)
            return LowerRewrite(context, node, rewrite.Rule);
```

3. `LowerRoot`'s parent check: `{ Rule.Form: DeclarativeForm.Operation or DeclarativeForm.Template or DeclarativeForm.Rewrite }`.

4. The method:

```csharp
    /// <summary>
    /// Lowers a rewrite's use as its instantiation, each hole bound to the use's child lowered here (once per child).
    /// The result's origins start with the use's. NH0008 for a rewrite reaching itself, NH0009 when its template can't be used.
    /// </summary>
    HirNode? LowerRewrite(LoweringContext context, int node, DeclarativeRule rule)
    {
        if (_file.IsCyclicRewrite(node))
        {
            context.Report("NH0008", context.Origin(node), "A rewrite that reaches itself prevents lowering.");
            return null;
        }
        if (_file.Instance(node) is not { } instance)
        {
            string reason = _file.Tree.Language!.RewriteTemplate(rule).Error ?? "it has no template";
            context.Report("NH0009", context.Origin(node), $"This rule's rewrite template can't be used: {reason}.");
            return null;
        }
        var holes = new Dictionary<int, HirNode>();
        var byChild = new Dictionary<int, HirNode?>();
        foreach (var (hole, child) in instance.File.Holes!.Value.Children)
        {
            if (!byChild.TryGetValue(child, out var lowered)) byChild[child] = lowered = Lower(context, child);
            if (lowered is null) return null;
            holes[hole] = lowered;
        }
        var body = context.Rewriting(instance.File, holes);
        if (!HirLowering.Admit(body, instance.Root)) return null;
        var result = instance.File.DeclarativeTypes.LowerNested(body, instance.Root);
        return result is null ? null
            : HirTraversal.WithOrigins(result, result.Origins.Prepend(context.Origin(node)).Distinct().ToArray());
    }
```

   A cycle inside an instantiation is reported by the innermost use; the reports go to the outer context through the shared report list.

In `Nitrogen.Runtime/Semantic/DeclarativeLowering.cs`, where roots are registered, make a rewrite's use a root like an expansion:

```csharp
                if (rule.Form is DeclarativeForm.Expand or DeclarativeForm.Rewrite)
                    lowerers.Add(new LoweringRegistration(kind, rule.Form == DeclarativeForm.Expand ? "<expand>" : "<rewrite>",
                        (context, node) => context.File.DeclarativeTypes.LowerRoot(context, node), DynamicOperation: true));
```

(replacing the `if (rule.Form == DeclarativeForm.Expand)` registration). If the composition rejects an unknown form anywhere before this point, let `Rewrite` through: it has no operation or type to check.

- [ ] **Step 6: Run the tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`, then `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RewriteTests"`.
Expected: 8 passed. Debugging hints:
- No root for a use: check the registration (Step 5) and that `HirLowering.Lower` accepts a dynamic root whose result is the template's operation.
- `x` untyped: check the `Synthesized` fallback (Step 4.3) is reached for DateCalc's `Type` property on the use.
- An expansion error missing: check `ExpansionDiagnostics` runs for the use and that the instantiation's checks see the hole's type through `Get`.
- No NH0008 for `1 loops;`: the cyclic use types as an error, and if a declarative type diagnostic for it blocks the use's admission before `LowerRewrite` runs, report NH0008 from `ExpansionDiagnostics` instead (a cyclic use is a semantic error at the use) and change the test to read `service.Diagnostics` for it. Either way the requirement is: reported, and no stack overflow.
Then the whole suite.

- [ ] **Step 7: Commit**

```bash
git add Nitrogen.Runtime Nitrogen.Tests/LanguageService/RewriteTests.cs
git commit -m "Type, check and lower rewrite uses as their expansions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Template diagnostics in the grammar workspace

**Files:** `Nitrogen.Workspace/GrammarWorkspace.cs`; test `Nitrogen.Tests/LanguageService/RewriteTests.cs`.

**Interfaces:**
- Consumes: `Language.RewriteTemplate`, `GrammarCodes.TemplateDoesNotParse`, `GrammarCodes.TemplateDeclares`.

- [ ] **Step 1: Write the failing tests**

Append to `RewriteTests` a second grammar variant: a broken template and a declaring one.

```csharp
    void AddRules(string alternatives, string rules = "")
    {
        string grammar = Path.Combine(_root, "DateCalc.ngr");
        string text = File.ReadAllText(grammar);
        int days = text.IndexOf("    | Days    =", StringComparison.Ordinal);
        text = text.Insert(days, alternatives);
        if (rules.Length > 0)
        {
            text = text.Replace("syntax Statement = Let / Show;", "syntax Statement = Let / Show / Setup;");
            int program = text.IndexOf("  syntax Program", StringComparison.Ordinal);
            text = text.Insert(program, rules);
        }
        File.WriteAllText(grammar, text);
    }

    [Fact]
    public void A_template_that_does_not_parse_warns_and_its_uses_report()
    {
        AddRules("    | Broken = Count:Expr \"broken\" precedence 30  rewrites <[ $Count * ]>\n");
        using var service = Service();
        service.Open(Uri("DateCalc.ngr"), 1, File.ReadAllText(Path.Combine(_root, "DateCalc.ngr")));
        var warning = Assert.Single(service.Diagnostics(Uri("DateCalc.ngr")), d => d.Code == "NGR0137");
        Assert.Equal(ServiceSeverity.Warning, warning.Severity);

        service.Open(Uri("a.datecalc"), 1, "1 broken;\n2 fortnights;");
        Assert.Contains(service.InspectDocument(Uri("a.datecalc"))!.Diagnostics, d => d.Code == "NH0009");
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")).Where(d => d.Range.Start.Line == 1)); // the language still works
    }

    [Fact]
    public void A_template_that_declares_a_name_warns()
    {
        AddRules("", "  syntax Setup = \"setup\" \";\"  rewrites <[ let tmp = 1; ]>;\n");
        using var service = Service();
        service.Open(Uri("DateCalc.ngr"), 1, File.ReadAllText(Path.Combine(_root, "DateCalc.ngr")));
        Assert.Contains(service.Diagnostics(Uri("DateCalc.ngr")), d => d.Code == "NGR0138" && d.Severity == ServiceSeverity.Warning);
    }
```

(The constructor already inserts the main alternatives; `AddRules` adds more.)

The second test also exercises a plain rule's category: `Setup` is listed by the `Statement` alias, so its template parses as a `Statement` (a `Let`).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RewriteTests"`
Expected: the two new tests fail: no NGR0137 / NGR0138 diagnostics.

- [ ] **Step 3: Report them after composing**

In `Nitrogen.Workspace/GrammarWorkspace.cs`, `Compile()`, after the language is composed successfully (where helper sources are inspected for the evaluation profile, around the NGR0002–NGR0005 checks), add:

```csharp
        diagnostics.AddRange(TemplateDiagnostics(language));
```

and the method:

```csharp
    /// <summary>
    /// A rewrite template that can't be used (spec: quasiquote rewrites) is a warning at the template in its grammar
    /// file: NGR0137 when it doesn't parse, NGR0138 when it declares a name. The language is still served.
    /// </summary>
    IEnumerable<WorkspaceDiagnostic> TemplateDiagnostics(Language language)
    {
        foreach (var rule in language.Modules.SelectMany(module => module.DeclarativeRules))
        {
            if (rule.Rewrite is not { } spec || language.RewriteTemplate(rule).Error is not { } error) continue;
            string code = error.Contains("declare", StringComparison.Ordinal) ? GrammarCodes.TemplateDeclares : GrammarCodes.TemplateDoesNotParse;
            foreach (var (path, text) in _files)   // the workspace's grammar files and their texts
            {
                int at = text.IndexOf("<[" + spec.Text + "]>", StringComparison.Ordinal);
                if (at < 0) continue;
                var (line, column) = LineColumn(text, at);
                yield return new WorkspaceDiagnostic(path, line, column, code, $"rewrite template: {error}", IsError: false);
            }
        }
    }
```

   `_files` holds the workspace's grammar paths and texts, and `LineColumn` is its existing offset-to-position helper (used for grammar diagnostics). Call `TemplateDiagnostics` after `builder.TryBuild(out var language, …)` succeeds, before the snapshot is created; the warnings don't fail the compile, so the composed language is still returned.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RewriteTests"`.
Expected: 10 passed. Then the whole suite.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Workspace/GrammarWorkspace.cs Nitrogen.Tests/LanguageService/RewriteTests.cs
git commit -m "Warn about rewrite templates that can't be used

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Hover and docs

**Files:** `Nitrogen.LanguageService/NitrogenLanguageService.cs` (hover); `docs/language-guide.md`; the spec; test `Nitrogen.Tests/LanguageService/RewriteTests.cs`.

- [ ] **Step 1: Write the failing test**

Append to `RewriteTests`:

```csharp
    [Fact]
    public void Hover_on_a_use_shows_its_template()
    {
        using var service = Service();
        const string text = "3 fortnights;";
        service.Open(Uri("a.datecalc"), 1, text);
        var hover = service.Hover(Uri("a.datecalc"), new DocumentPosition(0, text.IndexOf("fortnights", StringComparison.Ordinal) + 2))!;
        Assert.Contains("rewrites <[ $Count * 2 weeks ]>", hover.Markdown);
        Assert.DoesNotContain("rewrites", service.Hover(Uri("a.datecalc"), new DocumentPosition(0, 0))?.Markdown ?? ""); // on the child
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~Hover_on_a_use"`
Expected: FAIL: no `rewrites` line.

- [ ] **Step 3: Implement**

In `Nitrogen.LanguageService/NitrogenLanguageService.cs`, `Hover`: compute the rewrite at the position and append it (`"\n\n`rewrites <[" + text + "]>`"`), returning a hover with only that line when there was none:

```csharp
    /// <summary>The template of the rewrite use whose own text holds the offset (not one of its holes' children); null otherwise.</summary>
    static string? RewriteAt(Document document, int offset)
    {
        var tree = document.Parsed.Tree;
        var declarative = document.Language.Language.Declarative;
        int innermost = -1, length = int.MaxValue;
        for (int node = 0; node < tree.NodeCount; node++)
        {
            var span = tree.Span(node);
            if (span.Start <= offset && offset < span.End && span.Length < length) (innermost, length) = (node, span.Length);
        }
        for (int node = innermost; node >= 0; node = tree.Parent(node))
            if (declarative.RuleFor(tree.Kind(node))?.Rule.Rewrite is { } spec)
            {
                foreach (var hole in spec.Holes)
                {
                    var child = tree.Span(tree.Child(node, hole.Child));
                    if (child.Start <= offset && offset < child.End) return null;
                }
                return spec.Text;
            }
        return null;
    }
```

   (`DeclarativeLowering.RuleFor` is internal to the runtime; if it isn't visible here, add a public `DeclarativeRule? RuleOf(int kind)` on `DeclarativeLowering` returning `RuleFor(kind)?.Rule`, and use it.) The hover text uses the spec's verbatim text: `rewrites <[ $Count * 2 weeks ]>`.

- [ ] **Step 4: Docs**

In `docs/language-guide.md`, add a section **Rewrite rules** after the section on declarative lowering (`lowers`):

```markdown
## Rewrite rules

A rule can mean code the language already has, written in the language itself: `rewrites <[ … ]>`
replaces a `lowers` clause, and each `$Field` in the template is filled by that field of the use.

    extensible syntax Expr
    {
      | Fortnights = Count:Expr "fortnights" precedence 30  rewrites <[ $Count * 2 weeks ]>
    }

`3 fortnights` then types, checks, lowers and evaluates exactly as `3 * 2 weeks`. A hole is one node:
`!$Cond` with `a + b` in the hole means `!(a + b)`. The template parses as the rule's category: the
extensible rule of an alternative, or the alias that lists a plain rule. Errors inside an expansion are
reported at the use (`in the expansion of Fortnights: …`), and hover on a use shows its template.

Templates may not declare names, and holes stand for syntax or extensible rules, not tokens. A template
that doesn't parse or declares a name is a warning in the grammar (NGR0137, NGR0138); its uses report
NH0009.
```

In the spec, set `Status: implemented (2026-10-10).`

- [ ] **Step 5: Verify and commit**

Run `dotnet build Nitrogen.slnx -warnaserror`, the whole suite, and `python3 eng/check-links.py`.

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.cs Nitrogen.Runtime/Semantic/DeclarativeLowering.cs Nitrogen.Tests/LanguageService/RewriteTests.cs docs/language-guide.md docs/superpowers/specs/2026-10-10-quasiquote-rewrites-design.md
git commit -m "Show a rewrite use's template on hover, and document rewrite rules

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
