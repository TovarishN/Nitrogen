# Quick fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workspace language supplies quick fixes in C#. The server offers a fix only after checking it removes the error and adds none, and serves fixes as LSP `quickfix` code actions. DateCalc fixes DC0001 (nearest valid date) and DC0002 (`+` → `-`).

**Architecture:**
- Runtime types (`FixRequest`, `TextEdit`, `QuickFix`, `DiagnosticFixes`) let a helper source map diagnostic codes to C# fixers.
- `GrammarWorkspace` discovers the language's one `DiagnosticFixes`, as it does `EvaluationProfile`.
- `NitrogenLanguageService.QuickFixes` proposes fixes for the errors in a range. It checks each by re-parsing the edited text in a scratch project, and maps fixes for tagged C# strings back into the C# file.
- `LspServer` answers `textDocument/codeAction`.

**Tech Stack:** C# / .NET 10, xUnit, LSP 3.17 code actions.

**Spec:** `docs/superpowers/specs/2026-10-07-quick-fixes-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `quick-fixes`.

**Findings from a probe while planning** (a throwaway test, since deleted):
- DC0002's span is the whole `a + b` expression. That node has exactly three children: left operand, a `+` token node, right operand. So the fixer finds the node whose span equals the diagnostic's and replaces its middle child, with no DateCalc node kinds needed.
- DC0001's span is exactly the date literal.
- In `(1 + 2026-10-05) + 2026-10-06`, only the inner `+` is reported. The check will rightly reject `-` there.
- No `TextEdit` or `QuickFix` type exists yet. `SyntaxTree.NodeCount` is public.

**Title wording:** the spec writes DC0002's title as "Use `-`". Editors show code-action titles as plain text, so the code uses `Use '-'`. Task 6 updates the spec to match.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.Runtime/Semantics/DiagnosticFixes.cs` | `FixRequest`, `TextEdit`, `QuickFix`, `DiagnosticFixes` | 1 |
| `Nitrogen.Workspace/GrammarWorkspace.cs`, `WorkspaceSnapshot.cs` | discover `DiagnosticFixes`; `NGR0004` | 2 |
| `Nitrogen.LanguageService/LanguageRegistry.cs`, `GrammarLanguages.cs` | `LanguageEntry.Fixes` | 2 |
| `examples/DateCalc/DateCalcFixes.cs`, `nitrogen.json` | DateCalc's fixers | 3 |
| `Nitrogen.Tests/Nitrogen.Tests.csproj` | copy `DateCalcFixes.cs` | 3 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Fixes.cs` | `QuickFixes`, the check | 4 |
| `Nitrogen.LanguageService/ServiceTypes.cs` | `ServiceFix` | 4 |
| `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `LspServer.cs` | code actions | 5 |
| tests | per task | 1–5 |

---

### Task 1: Runtime types

**Files:**
- Create: `Nitrogen.Runtime/Semantics/DiagnosticFixes.cs`
- Test: `Nitrogen.Tests/Semantics/DiagnosticFixesTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A language's quick fixes dispatch by diagnostic code.</summary>
public class DiagnosticFixesTests
{
    static readonly DiagnosticFixes Fixes = new(new Dictionary<string, Func<FixRequest, IEnumerable<QuickFix>>>
    {
        ["XX0001"] = request => [new QuickFix("Upper", [new TextEdit(request.Span, request.Text.ToUpperInvariant())])],
    });

    [Fact]
    public void A_fixer_proposes_for_its_code()
    {
        var fix = Assert.Single(Fixes.Propose(new FixRequest("XX0001", new TextSpan(0, 2), "ab", null!)));
        Assert.Equal("Upper", fix.Title);
        Assert.Equal(new TextEdit(new TextSpan(0, 2), "AB"), Assert.Single(fix.Edits));
    }

    [Fact]
    public void A_code_without_a_fixer_proposes_nothing()
    {
        Assert.Empty(Fixes.Propose(new FixRequest("XX0002", new TextSpan(0, 2), "ab", null!)));
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DiagnosticFixesTests"`
Expected: build error `The type or namespace name 'DiagnosticFixes' could not be found`.

- [ ] **Step 3: Implement.** Create `Nitrogen.Runtime/Semantics/DiagnosticFixes.cs`:

```csharp
namespace Nitrogen.Semantics;

/// <summary>A diagnostic to fix: its code and span, and the document it is in.</summary>
public sealed record FixRequest(string Code, TextSpan Span, string Text, SyntaxTree Tree);

/// <summary>Replaces <paramref name="Span"/> of a document with <paramref name="NewText"/>.</summary>
public sealed record TextEdit(TextSpan Span, string NewText);

/// <summary>A proposed fix: what the editor shows, and the edits it makes to the document.</summary>
public sealed record QuickFix(string Title, IReadOnlyList<TextEdit> Edits);

/// <summary>
/// A language's quick fixes, by diagnostic code. A workspace language's helper source exports one as a
/// public static field or property. A fixer only proposes; the host offers a fix after checking it.
/// </summary>
public sealed class DiagnosticFixes(IReadOnlyDictionary<string, Func<FixRequest, IEnumerable<QuickFix>>> byCode)
{
    readonly IReadOnlyDictionary<string, Func<FixRequest, IEnumerable<QuickFix>>> _byCode = byCode ?? throw new ArgumentNullException(nameof(byCode));

    /// <summary>The fixes proposed for the request's diagnostic; empty for a code without a fixer.</summary>
    public IEnumerable<QuickFix> Propose(FixRequest request) =>
        _byCode.TryGetValue(request.Code, out var fixer) ? fixer(request) : [];
}
```

- [ ] **Step 4: Run the tests and check they pass**

Run: the Step 2 command.
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Runtime/Semantics/DiagnosticFixes.cs Nitrogen.Tests/Semantics/DiagnosticFixesTests.cs
git commit -m "Add types for a language's quick fixes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Discovery

**Files:**
- Modify: `Nitrogen.Workspace/WorkspaceSnapshot.cs`, `Nitrogen.Workspace/GrammarWorkspace.cs`
- Modify: `Nitrogen.LanguageService/LanguageRegistry.cs`, `Nitrogen.LanguageService/GrammarLanguages.cs`
- Test: `Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs` (shares its `Sum` grammar and `Compile` helper)

- [ ] **Step 1: Write the failing tests.** Add to `WorkspaceEvaluationTests`:

```csharp
    static string FixesSource(string type) => $$"""
        using Nitrogen.Semantics;

        public static class {{type}}
        {
            public static DiagnosticFixes Fixes { get; } = new(new Dictionary<string, Func<FixRequest, IEnumerable<QuickFix>>>());
        }
        """;

    [Fact]
    public void One_fix_provider_is_discovered()
    {
        using var snapshot = Compile(FixesSource("SumFixes"));
        Assert.NotNull(snapshot.Fixes);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void Without_a_fix_provider_the_language_has_no_fixes()
    {
        using var snapshot = Compile();
        Assert.Null(snapshot.Fixes);
    }

    [Fact]
    public void Two_fix_providers_are_a_warning_and_neither_is_used()
    {
        using var snapshot = Compile(FixesSource("FirstFixes"), FixesSource("SecondFixes"));
        Assert.Null(snapshot.Fixes);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0004", warning.Code);
        Assert.False(warning.IsError);
    }
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceEvaluationTests"`
Expected: build error `'WorkspaceSnapshot' does not contain a definition for 'Fixes'`.

- [ ] **Step 3: The snapshot.** In `WorkspaceSnapshot.cs`, add `using Nitrogen.Semantics;`, a constructor parameter `DiagnosticFixes? fixes = null` after `evaluation`, and `Fixes = fixes;` in the constructor. Add the property:

```csharp
    /// <summary>The helper sources' quick fixes; null when there are none.</summary>
    public DiagnosticFixes? Fixes { get; private set; }
```

In `Dispose`, add `Fixes = null;` after `Evaluation = null;`.

- [ ] **Step 4: Discovery.** In `GrammarWorkspace.cs`, add `using Nitrogen.Semantics;`. Replace the snapshot construction with:

```csharp
            var evaluation = Evaluation(types, language!, diagnostics);
            var fixes = Fixes(types, diagnostics);
            var snapshot = new WorkspaceSnapshot(version, diagnostics, language, context, modules, evaluation, fixes);
```

Add after `Evaluation(...)`:

```csharp
    /// <summary>The one public static <see cref="DiagnosticFixes"/> of <paramref name="types"/>; two are a warning (NGR0004), and the language has none.</summary>
    static DiagnosticFixes? Fixes(IEnumerable<Type> types, List<WorkspaceDiagnostic> diagnostics)
    {
        var found = StaticValues(types, t => t == typeof(DiagnosticFixes)).OfType<DiagnosticFixes>().Distinct().ToList();
        if (found.Count <= 1) return found.FirstOrDefault();
        diagnostics.Add(new WorkspaceDiagnostic("", 0, 0, "NGR0004",
            $"{found.Count} quick fix providers; a language has at most one, so none is used", IsError: false));
        return null;
    }
```

In the class summary, change "and an `<see cref="EvaluationProfile"/>` among them says how an editor shows its values" to "an `<see cref="EvaluationProfile"/>` among them says how an editor shows its values, and a `<see cref="DiagnosticFixes"/>` how it fixes its errors".

- [ ] **Step 5: The language entry.** In `LanguageRegistry.cs`, add `using Nitrogen.Semantics;`, the doc line `/// <param name="fixes">Its quick fixes; none when null.</param>`, a constructor parameter `DiagnosticFixes? fixes = null` after `evaluation`, and:

```csharp
    public DiagnosticFixes? Fixes { get; } = fixes;
```

In `GrammarLanguages.cs` `Compile`, change the entry construction's last argument line to `evaluation: snapshot.Evaluation, fixes: snapshot.Fixes);`.

- [ ] **Step 6: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceEvaluationTests|FullyQualifiedName~WorkspaceTests|FullyQualifiedName~LoweredLanguageTests"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Nitrogen.Workspace Nitrogen.LanguageService/LanguageRegistry.cs Nitrogen.LanguageService/GrammarLanguages.cs Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs
git commit -m "Find a workspace language's quick fixes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: DateCalc's fixers

**Files:**
- Create: `examples/DateCalc/DateCalcFixes.cs`
- Modify: `examples/DateCalc/nitrogen.json`, `Nitrogen.Tests/Nitrogen.Tests.csproj`
- Test: `Nitrogen.Tests/DateCalc/DateCalcTests.cs`

- [ ] **Step 1: Write the failing tests.** Add to `DateCalcTests`:

```csharp
    [Theory]
    [InlineData("2026-02-30", "2026-02-28")]
    [InlineData("2028-02-30", "2028-02-29")]
    [InlineData("2026-13-05", "2026-12-05")]
    [InlineData("2026-00-00", "2026-01-01")]
    [InlineData("2026-04-31", "2026-04-30")]
    public void The_nearest_date_clamps_the_month_then_the_day(string text, string expected) =>
        Assert.Equal(expected, DateCalcFixes.Nearest(text)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalcTests"`
Expected: build error `The name 'DateCalcFixes' does not exist`.

- [ ] **Step 3: Implement.** Create `examples/DateCalc/DateCalcFixes.cs`:

```csharp
using System.Globalization;
using Nitrogen.Semantics;

namespace DateCalc.Syntax;

/// <summary>
/// DateCalc's quick fixes: the nearest calendar date for an invalid one (DC0001), and <c>-</c> for a
/// <c>+</c> that doesn't apply (DC0002). The language server finds <see cref="Fixes"/> and offers a fix
/// only when it removes the error, so <c>-</c> is offered for <c>date + date</c> but not for
/// <c>duration + date</c>.
/// </summary>
public static class DateCalcFixes
{
    public static readonly DiagnosticFixes Fixes = new(new Dictionary<string, Func<FixRequest, IEnumerable<QuickFix>>>
    {
        ["DC0001"] = NearestDate,
        ["DC0002"] = Subtract,
    });

    /// <summary>The valid date nearest a <c>yyyy-MM-dd</c> text: its year clamped to 1–9999, its month to 1–12, then its day to that month's days; null when it is not that shape.</summary>
    public static DateOnly? Nearest(string text)
    {
        string[] parts = text.Split('-');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int day))
            return null;
        year = Math.Clamp(year, 1, 9999);
        month = Math.Clamp(month, 1, 12);
        return new DateOnly(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)));
    }

    static IEnumerable<QuickFix> NearestDate(FixRequest request)
    {
        if (Nearest(request.Text.Substring(request.Span.Start, request.Span.Length)) is not { } date) return [];
        string text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return [new QuickFix($"Change to {text}", [new TextEdit(request.Span, text)])];
    }

    /// <summary>The <c>+</c> of the binary expression at the diagnostic's span, as a <c>-</c>.</summary>
    static IEnumerable<QuickFix> Subtract(FixRequest request)
    {
        var tree = request.Tree;
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.Span(node) != request.Span || tree.ChildCount(node) != 3) continue;
            var op = tree.Span(tree.Child(node, 1));
            if (request.Text.Substring(op.Start, op.Length) == "+")
                return [new QuickFix("Use '-'", [new TextEdit(op, "-")])];
        }
        return [];
    }
}
```

In `examples/DateCalc/nitrogen.json`, change `sources` to `["DateCalcLanguage.cs", "MathModule.cs", "DateCalcEvaluator.cs", "DateCalcFixes.cs"]`.

In `Nitrogen.Tests/Nitrogen.Tests.csproj`, add `..\examples\DateCalc\DateCalcFixes.cs;` after `..\examples\DateCalc\DateCalcEvaluator.cs;` in the `DateCalcLanguage` copy item.

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalc|FullyQualifiedName~LoweredLanguageTests"`
Expected: all pass. `LoweredLanguageTests` now compiles `DateCalcFixes.cs` as a helper source, and its empty-diagnostics check confirms it compiles.

- [ ] **Step 5: Commit**

```bash
git add examples/DateCalc/DateCalcFixes.cs examples/DateCalc/nitrogen.json Nitrogen.Tests/Nitrogen.Tests.csproj Nitrogen.Tests/DateCalc/DateCalcTests.cs
git commit -m "Add DateCalc's quick fixes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `QuickFixes` in the service

**Files:**
- Modify: `Nitrogen.LanguageService/ServiceTypes.cs`
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.Fixes.cs`
- Create: `Nitrogen.Tests/LanguageService/QuickFixTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>DateCalc's quick fixes in the editor: proposed by its fixers, offered only when they work.</summary>
public sealed class QuickFixTests : IDisposable
{
    static readonly DocumentRange Line0 = new(new DocumentPosition(0, 0), new DocumentPosition(0, int.MaxValue));

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-fixes-").FullName;

    public QuickFixTests()
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

    IReadOnlyList<ServiceFix> Fixes(NitrogenLanguageService service, string text, DocumentRange? range = null)
    {
        string uri = Uri("a.datecalc");
        service.Open(uri, 1, text);
        return service.QuickFixes(uri, range ?? Line0);
    }

    static string Apply(string text, ServiceFix fix)
    {
        var lines = new LineMap(text);
        foreach (var edit in fix.Edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
        {
            int start = lines.OffsetOf(edit.Range.Start), end = lines.OffsetOf(edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }
        return text;
    }

    [Theory]
    [InlineData("let d = 2026-02-30;", "Change to 2026-02-28", "let d = 2026-02-28;")]
    [InlineData("2026-13-05;", "Change to 2026-12-05", "2026-12-05;")]
    [InlineData("2026-12-25 + 2026-10-05;", "Use '-'", "2026-12-25 - 2026-10-05;")]
    public void An_error_gets_a_fix_that_removes_it(string text, string title, string fixedText)
    {
        using var service = Service();
        var fix = Assert.Single(Fixes(service, text));

        Assert.Equal(title, fix.Title);
        Assert.Equal(fixedText, Apply(text, fix));
        Assert.Equal(fix.Title.StartsWith("Use", StringComparison.Ordinal) ? "DC0002" : "DC0001", fix.Diagnostic.Code);
    }

    [Theory]
    [InlineData("2 weeks + 2026-10-05;")]          // '-' doesn't apply either
    [InlineData("(1 + 2026-10-05) + 2026-10-06;")] // nor here
    public void A_proposed_fix_that_does_not_remove_the_error_is_not_offered(string text)
    {
        using var service = Service();
        Assert.Empty(Fixes(service, text));
    }

    [Fact]
    public void Only_errors_in_the_range_get_fixes()
    {
        using var service = Service();
        Assert.Empty(Fixes(service, "1 + 1;\nlet d = 2026-02-30;"));
    }

    [Fact]
    public void A_language_without_fixes_offers_none()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = q; }");
        Assert.Empty(service.QuickFixes("file:///w/a.scopes", Line0));
    }

    [Fact]
    public void A_tagged_csharp_string_gets_the_fix_at_csharp_positions()
    {
        using var service = Service();
        string uri = Uri("C.cs");
        const string host = "class C { const string D = /*lang=datecalc*/ \"2026-02-30;\"; }";
        service.Open(uri, 1, host);
        int date = host.IndexOf("2026-02-30", StringComparison.Ordinal);

        var fix = Assert.Single(service.QuickFixes(uri, new DocumentRange(new DocumentPosition(0, date + 2), new DocumentPosition(0, date + 2))));

        Assert.Equal("Change to 2026-02-28", fix.Title);
        Assert.Equal(host.Replace("2026-02-30", "2026-02-28"), Apply(host, fix));
        Assert.Equal(new DocumentPosition(0, date), fix.Diagnostic.Range.Start);
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~QuickFixTests"`
Expected: build error `The type or namespace name 'ServiceFix' could not be found`.

- [ ] **Step 3: Add `ServiceFix`.** Append to `ServiceTypes.cs`:

```csharp
/// <summary>A quick fix the service checked: its title, the diagnostic it removes, and its edits to the document.</summary>
public sealed record ServiceFix(string Title, ServiceDiagnostic Diagnostic, IReadOnlyList<DocumentEdit> Edits);
```

- [ ] **Step 4: Implement.** Create `Nitrogen.LanguageService/NitrogenLanguageService.Fixes.cs`:

```csharp
using Nitrogen.Binding;
using Nitrogen.Semantics;

namespace Nitrogen.LanguageService;

/// <summary>
/// Quick fixes: a language's fixers propose edits for its errors, and the service offers one only when
/// the edited document, parsed and checked on its own, no longer has that error there and has fewer
/// errors overall. The check sees one document, so a fix that relies on another file can be rejected.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    /// <summary>The checked fixes for the errors overlapping <paramref name="range"/>; for a C# host, those of the tagged string at its start, at host positions.</summary>
    public IReadOnlyList<ServiceFix> QuickFixes(string uri, DocumentRange range)
    {
        if (_hosts.ContainsKey(uri))
        {
            if (Into(uri, range.Start) is not { } inner) return [];
            return QuickFixes(inner.Uri, new DocumentRange(inner.Position, inner.Position))
                .Select(fix => new ServiceFix(fix.Title, fix.Diagnostic with { Range = OutOf(inner.Uri, fix.Diagnostic.Range) },
                    fix.Edits.Select(edit => edit with { Range = OutOf(inner.Uri, edit.Range) }).ToList()))
                .ToList();
        }
        if (!_documents.TryGetValue(uri, out var document) || document.Language.Fixes is not { } fixes) return [];
        var errors = Diagnostics(uri).Where(d => d.Severity == ServiceSeverity.Error && Overlaps(d.Range, range)).ToList();
        if (errors.Count == 0) return [];

        int before = Errors(document.Language, document.Start, uri, document.Text).Count;
        var offered = new List<ServiceFix>();
        foreach (var error in errors)
        {
            int start = document.Lines.OffsetOf(error.Range.Start);
            var request = new FixRequest(error.Code, new TextSpan(start, document.Lines.OffsetOf(error.Range.End) - start), document.Text, document.Parsed.Tree);
            List<QuickFix> proposed;
            try
            {
                proposed = fixes.Propose(request).ToList();
            }
            catch (Exception) // a fixer that throws proposes nothing
            {
                continue;
            }
            foreach (var fix in proposed)
            {
                if (Apply(document.Text, fix.Edits) is not { } applied) continue;
                var after = Errors(document.Language, document.Start, uri, applied.Text);
                if (after.Count >= before || after.Any(e => e.Code == error.Code && applied.Edited.Any(edited => Overlaps(e.Span, edited)))) continue;
                offered.Add(new ServiceFix(fix.Title, error,
                    fix.Edits.Select(edit => new DocumentEdit(document.Lines.RangeOf(edit.Span), edit.NewText)).ToList()));
            }
        }
        return offered;
    }

    /// <summary>The text with the edits made, and where each edit's new text lies in it; null when edits overlap or fall outside the text.</summary>
    static (string Text, List<TextSpan> Edited)? Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var ordered = edits.OrderBy(e => e.Span.Start).ToList();
        var result = new System.Text.StringBuilder();
        var edited = new List<TextSpan>();
        int at = 0;
        foreach (var edit in ordered)
        {
            if (edit.Span.Start < at || edit.Span.End > text.Length) return null;
            result.Append(text, at, edit.Span.Start - at);
            edited.Add(new TextSpan(result.Length, edit.NewText.Length));
            result.Append(edit.NewText);
            at = edit.Span.End;
        }
        result.Append(text, at, text.Length - at);
        return (result.ToString(), edited);
    }

    /// <summary>The errors of <paramref name="text"/> as a document of <paramref name="language"/> on its own: parse, binding and check errors.</summary>
    static List<(string Code, TextSpan Span)> Errors(LanguageEntry language, Rule start, string uri, string text)
    {
        using var document = new Document(uri, 0, text, language, start);
        var project = new Project(language.Language);
        project.Set(uri, document.Parsed.Tree);
        var errors = new List<(string Code, TextSpan Span)>();
        foreach (var diagnostic in document.Parsed.Diagnostics)
            if (Severity(diagnostic.Severity) == ServiceSeverity.Error) errors.Add((diagnostic.Code.ToString(), diagnostic.Span));
        foreach (var diagnostic in project.Diagnostics(uri)) errors.Add((diagnostic.Code, diagnostic.Span));
        foreach (var diagnostic in new ProjectSemantics(project)[uri].Diagnostics())
            if (!diagnostic.Code.StartsWith("NS000", StringComparison.Ordinal)) errors.Add((diagnostic.Code, diagnostic.Span));
        return errors;
    }

    static bool Overlaps(DocumentRange a, DocumentRange b) =>
        (a.Start.Line, a.Start.Character).CompareTo((b.End.Line, b.End.Character)) <= 0 &&
        (b.Start.Line, b.Start.Character).CompareTo((a.End.Line, a.End.Character)) <= 0;

    static bool Overlaps(TextSpan a, TextSpan b) => a.Start <= b.End && b.Start <= a.End;
}
```

If `Severity` isn't static, or `DiagnosticSeverity`'s namespace isn't imported in this file, follow the compiler message: `Severity` is declared in `NitrogenLanguageService.cs` (around line 387). Don't duplicate it.

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~QuickFixTests|FullyQualifiedName~EmbeddedStringTests|FullyQualifiedName~ValueHintTests"`
Expected: all pass. If `An_error_gets_a_fix_that_removes_it` rejects a fix it should keep, print `before` and the `after` errors for that text. A count that doesn't drop means `Errors` sees an error the service's `Diagnostics` doesn't, or the reverse; stop and report rather than loosening the check.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/ServiceTypes.cs Nitrogen.LanguageService/NitrogenLanguageService.Fixes.cs Nitrogen.Tests/LanguageService/QuickFixTests.cs
git commit -m "Offer quick fixes that remove their error

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Code actions over LSP

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/QuickFixTests.cs`

- [ ] **Step 1: Write the failing test.** Add to `QuickFixTests` (and `using System.Text.Json;` at the top):

```csharp
    [Fact]
    public async Task The_server_answers_code_actions_with_quick_fixes()
    {
        string doc = Uri("a.datecalc");
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri + "\",\"capabilities\":{}}}";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":\"2026-02-30;\"}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/codeAction\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\"},\"range\":{\"start\":{\"line\":0,\"character\":2},\"end\":{\"line\":0,\"character\":2}},\"context\":{\"diagnostics\":[]}}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service, initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            open, request, """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var kinds = messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("codeActionProvider").GetProperty("codeActionKinds");
        Assert.Equal("quickfix", Assert.Single(kinds.EnumerateArray()).GetString());
        var action = Assert.Single(messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5)
            .GetProperty("result").EnumerateArray());
        Assert.Equal("Change to 2026-02-28", action.GetProperty("title").GetString());
        Assert.Equal("quickfix", action.GetProperty("kind").GetString());
        Assert.Equal("DC0001", Assert.Single(action.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        var edit = Assert.Single(action.GetProperty("edit").GetProperty("changes").GetProperty(doc).EnumerateArray());
        Assert.Equal("2026-02-28", edit.GetProperty("newText").GetString());
        Assert.Equal(10, edit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
    }
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~QuickFixTests"`
Expected: the new test fails with `KeyNotFoundException` (no `codeActionProvider`).

- [ ] **Step 3: Messages.** In `LspMessages.cs`:
  - Add the last `ServerCapabilities` parameter: `CodeActionOptions? CodeActionProvider = null` (after `InlayHintProvider`).
  - Add the records:

```csharp
public sealed record CodeActionOptions(string[] CodeActionKinds);

/// <summary>The params of <c>textDocument/codeAction</c>; the client's diagnostics are not used: the server checks its own.</summary>
public sealed record CodeActionParams(TextDocumentIdentifier TextDocument, LspRange Range);

public sealed record LspCodeAction(string Title, string Kind, LspDiagnostic[] Diagnostics, WorkspaceEdit Edit);
```

  - Register them on `LspJson`: `[JsonSerializable(typeof(CodeActionParams))]` and `[JsonSerializable(typeof(LspCodeAction[]))]`.

- [ ] **Step 4: Server.** In `LspServer.cs`:
  - Add `CodeActionProvider: new CodeActionOptions(["quickfix"])` to the `initialize` capabilities, after `InlayHintProvider: true`.
  - Add the request case before `default:`:

```csharp
            case "textDocument/codeAction":
            {
                var request = Params(parameters, LspJson.Default.CodeActionParams);
                string uri = request.TextDocument.Uri;
                var actions = service.QuickFixes(uri, new DocumentRange(Position(request.Range.Start), Position(request.Range.End)))
                    .Select(fix => new LspCodeAction(fix.Title, "quickfix",
                        [new LspDiagnostic(Range(fix.Diagnostic.Range), (int)fix.Diagnostic.Severity, fix.Diagnostic.Code, "nitrogen", fix.Diagnostic.Message)],
                        new WorkspaceEdit(new Dictionary<string, LspTextEdit[]>
                        {
                            [uri] = fix.Edits.Select(edit => new LspTextEdit(Range(edit.Range), edit.NewText)).ToArray(),
                        })))
                    .ToArray();
                await RespondAsync(id, actions, LspJson.Default.LspCodeActionArray, cancel);
                break;
            }
```

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~QuickFixTests|FullyQualifiedName~LspServerTests|FullyQualifiedName~InlayHintLspTests|FullyQualifiedName~TodayValueTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp Nitrogen.Tests/LanguageService/QuickFixTests.cs
git commit -m "Serve quick fixes as LSP code actions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Spec, full check, probe

**Files:**
- Modify: `docs/superpowers/specs/2026-10-07-quick-fixes-design.md`

- [ ] **Step 1: Spec.**
  - Status line: `Status: implemented (YYYY-MM-DD). Builds on`, using the date of this step.
  - Replace both occurrences of "Use `-`" with "Use '-'".
  - In section 6, DC0002 bullet: add "The fixer finds the node whose span equals the diagnostic's and has three children, and replaces the middle one when it is `+`."

- [ ] **Step 2: Full build and tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass: the previous 1,070 plus 2 (`DiagnosticFixesTests`) + 3 (workspace) + 5 (`DateCalcTests` cases) + 8 (`QuickFixTests` cases) + 1 (LSP) = 1,089.

- [ ] **Step 3: Probe the built server.** Build the CLI with `dotnet build Nitrogen.Cli -c Release`. Over stdio, as for earlier features: initialize with `examples/DateCalc` as the root, open a scratch `fix-probe.datecalc` (not written to disk) with the text `let d = 2026-02-30;\n2026-12-25 + 2026-10-05;\n2 weeks + 2026-10-05;`, and request `textDocument/codeAction` for each of its three lines. Expected:
  - line 1: "Change to 2026-02-28";
  - line 2: "Use '-'";
  - line 3: nothing.

  Report the output.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-10-07-quick-fixes-design.md
git commit -m "Record how DateCalc's '+' fix finds its operator

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
