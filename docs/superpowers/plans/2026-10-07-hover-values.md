# Values on hover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hovering an expression in a language with an evaluation profile adds its value (`= 42 days`, or `= ⚠ reason`) under the hover summary, in language files and tagged C# strings.

**Architecture:** One helper projects a HIR node with a `BoundEvaluation` and returns a formatted value or a failure reason. Inlay hints are rewritten on top of it. `Hover` uses it on the outermost lowered node at the hovered node's source span. No LSP, editor or tagged-string change.

**Tech Stack:** C# / .NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-07-hover-values-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `hover-values`.

**Findings from a probe run while planning** (a throwaway test, since deleted, on `sample.datecalc`):
1. `HirProjector.Project` evaluates a sub-expression on its own: `3 * sprint` gives `42 days`. Its operation is `DateCalc.Times`, not `DateCalc.Scale` as the spec's example says.
2. Hovering a date literal lands on its inner `HirText` (`Core.Text`), which formats as plain `2026-10-05`. Evaluating the **outermost node with the same source span** reaches the `DateCalc.Date` operation instead (`2026-10-05 Mon`). A number literal's outermost same-span node is still its `HirConstant`, so it still gets no value line.
3. A reference to a `let` (`sprint` in `start + sprint`) is lowered by inlining the let's initializer, whose origin is the declaration. Nothing lowered sits under the cursor there, so `Inspect` returns the enclosing `start + sprint`. **Decision (agreed with the user):** a name gets a value line only when the hovered node is itself a `HirSymbolRef` (a builtin such as `pi`). A `let` name keeps today's declaration hover with no value line; its value is already the inlay hint on its declaration line.

Task 3 records these in the spec.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` | `Project` helper; `Hint` on it; `HoverValue` | 1, 2 |
| `Nitrogen.LanguageService/NitrogenLanguageService.cs` | `Hover` appends the value line | 2 |
| `Nitrogen.Tests/LanguageService/HoverValueTests.cs` | hover value tests | 2 |
| the spec | findings and status | 3 |

---

### Task 1: One projection helper for hints and hover

This is a pure refactor. The existing hint tests are its check.

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` (`Hint`)

- [ ] **Step 1: Run the hint tests to record the baseline**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ValueHintTests|FullyQualifiedName~EmbeddedStringTests|FullyQualifiedName~InlayHintLspTests"`
Expected: all pass (7 + 20 + 4).

- [ ] **Step 2: Replace `Hint` with the helper and a thin `Hint`.** Replace the whole `static ValueHint Hint(...)` method with:

```csharp
    static ValueHint Hint(DocumentPosition at, HirNode root, BoundEvaluation evaluation)
    {
        var (value, failure) = Project(root, evaluation);
        return value is not null ? new ValueHint(at, "= " + value, null, false) : Failure(at, failure!);
    }

    /// <summary>A lowered node's value through the evaluation, formatted; or, when it has none, why.</summary>
    static (string? Value, string? Failure) Project(HirNode node, BoundEvaluation evaluation)
    {
        try
        {
            var inputs = HirTraversal.PreOrder(node).OfType<HirSymbolRef>()
                .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
                .Select(s => (Symbol: s, Value: evaluation.Profile.Builtin(s))).Where(p => p.Value is not null)
                .ToDictionary(p => p.Symbol, p => p.Value!);
            var result = HirProjector.Project(node, evaluation.Registry, inputs);
            return result.Value is { } value
                ? (evaluation.Profile.Format(value), null)
                : (null, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }
        catch (Exception error) // a profile's Builtin or Format threw; handlers' own exceptions are NP0005 diagnostics
        {
            return (null, $"{error.GetType().Name}: {error.Message}");
        }
    }
```

- [ ] **Step 3: Run the same tests and check nothing changed**

Run: the Step 1 command.
Expected: all pass, with the same counts.

- [ ] **Step 4: Commit**

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs
git commit -m "Share the projection behind value hints

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: The value line in hover

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` (new `HoverValue`)
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs` (`Hover`)
- Create: `Nitrogen.Tests/LanguageService/HoverValueTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Hovering an expression of a language with an evaluation profile shows its value under the summary.</summary>
public sealed class HoverValueTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-hover-").FullName;

    public HoverValueTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        return service;
    }

    /// <summary>The position of <paramref name="text"/>'s first occurrence, plus <paramref name="shift"/> characters.</summary>
    static DocumentPosition At(string source, string text, int shift = 0)
    {
        int offset = source.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{text}' is not in the text");
        return new LineMap(source).PositionOf(offset + shift);
    }

    /// <summary>The hover markdown at <paramref name="text"/> + <paramref name="shift"/> in a document holding <paramref name="source"/>.</summary>
    string Hover(NitrogenLanguageService service, string name, string source, string text, int shift)
    {
        string uri = Uri(name);
        service.Open(uri, 1, source);
        return service.Hover(uri, At(source, text, shift))?.Markdown ?? "";
    }

    string Sample => File.ReadAllText(Path.Combine(_root, "sample.datecalc"));

    [Fact]
    public void A_subexpression_shows_its_value()
    {
        using var service = Service();
        string hover = Hover(service, "a.datecalc", Sample, "3 * sprint", 2); // at '*'

        Assert.Contains("`DateCalc.Times`", hover, StringComparison.Ordinal);
        Assert.EndsWith("\n\n= 42 days", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_literal_shows_the_date_it_names()
    {
        using var service = Service();
        Assert.EndsWith("\n\n= 2026-10-05 Mon", Hover(service, "a.datecalc", Sample, "2026-10-05;", 3), StringComparison.Ordinal);
    }

    [Fact]
    public void A_builtin_name_shows_its_value()
    {
        using var service = Service();
        Assert.Contains("\n\n= 3.1415927", Hover(service, "a.datecalc", Sample, "pi", 1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_let_name_keeps_its_declaration_hover_without_a_value()
    {
        using var service = Service();
        string hover = Hover(service, "a.datecalc", Sample, "start + sprint", "start + ".Length + 1);

        Assert.Contains("declared in", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n= ", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_literal_has_no_second_value_line()
    {
        using var service = Service();
        string hover = Hover(service, "a.datecalc", Sample, "3 * sprint", 0);

        Assert.Contains("= 3", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n= ", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_expression_shows_why()
    {
        using var service = Service();
        Assert.Contains("\n\n= ⚠ NP0005: ", Hover(service, "a.datecalc", "9999-12-31 + 1 days;", "+", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_on_another_line_does_not_hide_the_value()
    {
        using var service = Service();
        Assert.EndsWith("\n\n= 3", Hover(service, "a.datecalc", "1 + 2;\n2026-10-05 + 2026-10-06;", "+", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_language_without_a_profile_shows_no_value()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = 1; let z = y; }");
        string hover = service.Hover("file:///w/a.scopes", At("unit a { let y = 1; let z = y; }", "y; }"))?.Markdown ?? "";

        Assert.DoesNotContain("\n\n= ", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tagged_csharp_string_shows_values_on_hover()
    {
        using var service = Service();
        const string host = "class C { const string D = /*lang=datecalc*/ \"let s = 2 weeks; 3 * s;\"; }";

        Assert.EndsWith("\n\n= 42 days", Hover(service, "C.cs", host, "3 * s", 2), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~HoverValueTests"`
Expected: FAIL for the subexpression, date literal, builtin, failing-expression, other-line-error and C# string tests (no value line yet). The let-name, number-literal and no-profile tests pass already.

- [ ] **Step 3: Add `HoverValue`.** In `NitrogenLanguageService.Evaluation.cs`, add after `Project`:

```csharp
    /// <summary>
    /// The value line of a hover: the outermost lowered node at the hovered node's span (a date literal's
    /// operation, not its text), projected. Null without an evaluation, at a literal constant, or at a name
    /// that didn't lower to a reference of its own (a let the language inlines: its value is its hint).
    /// </summary>
    static string? HoverValue(Document document, SemanticInspection inspection, bool atName)
    {
        if (document.Language.Evaluation is not { } evaluation) return null;
        if (atName && inspection.Node is not HirSymbolRef) return null;
        var node = HirTraversal.PreOrder(inspection.Root).FirstOrDefault(n =>
            n.Origins.Any(o => o.Path == document.Uri && document.Lines.RangeOf(o.Span) == inspection.Range)) ?? inspection.Node;
        if (node is HirConstant or HirText) return null;
        var (value, failure) = Project(node, evaluation);
        return value is not null ? "= " + value : "= ⚠ " + failure;
    }
```

- [ ] **Step 4: Append it in `Hover`.** In `NitrogenLanguageService.cs`, in `Hover`, replace:

```csharp
        if (NameAt(uri, position) is not { Symbols.Count: > 0 }) existing = null;
```

with:

```csharp
        bool atName = NameAt(uri, position) is { Symbols.Count: > 0 };
        if (!atName) existing = null;
```

Then, right before `return new HoverInfo(existing is null ? summary : …`, add:

```csharp
        if (HoverValue(_documents[uri], inspection, atName) is { } value) summary += "\n\n" + value;
```

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~HoverValueTests|FullyQualifiedName~LanguageService|FullyQualifiedName~EmbeddedStringTests|FullyQualifiedName~LoweredLanguageTests|FullyQualifiedName~ValueHintTests"`
Expected: all pass. Two tests may need attention:
- **`A_builtin_name_shows_its_value`:** if it fails because hovering `pi` doesn't return a `HirSymbolRef` node, print `service.Inspect(...)!.Node` and stop and report. That means DateCalc builtins lower differently from what the plan assumes, and the name rule needs another decision.
- **Existing hover tests** that assert a hover's exact full text: if one fails only because a value line was appended, update its expected text to include the line, and say so in the commit message.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.cs Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs Nitrogen.Tests/LanguageService/HoverValueTests.cs
git commit -m "Show an expression's value on hover

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Spec, full check

**Files:**
- Modify: `docs/superpowers/specs/2026-10-07-hover-values-design.md`

- [ ] **Step 1: Update the spec.**
  - Status line: `Status: implemented (YYYY-MM-DD). Builds on`, using the date of this step.
  - In the Goal example, change `` `DateCalc.Scale` `` to `` `DateCalc.Times` ``.
  - In section 1, replace the **Constant literals** bullet with: "**Literals:** the value is that of the outermost lowered node at the hovered node's source span. A date literal's inner text has the same span as its `DateCalc.Date` operation, so it shows `= 2026-10-05 Mon`. A number literal (`HirConstant`) already shows `= 2` in its summary, so it gets no value line, and neither does a text constant."
  - Replace the **Names** bullet with: "**Names:** a name gets a value line only when the hovered node is itself a reference (`HirSymbolRef`), as for a builtin such as `pi`. DateCalc lowers a `let` reference by inlining its initializer, whose origin is the declaration, so nothing lowered sits under such a name; it keeps today's declaration hover with no value line. Its value is the inlay hint on its declaration line."
  - In section 4, replace the `let` reference test bullet with two: "a builtin (`pi`) → `= 3.1415927`;" and "a `let` reference (`sprint` in `start + sprint`) → keeps its declaration text, no value line;".

- [ ] **Step 2: Full build and tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass: the previous 1,049 plus 9 new.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs/2026-10-07-hover-values-design.md
git commit -m "Record how hover values treat literals and names

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
