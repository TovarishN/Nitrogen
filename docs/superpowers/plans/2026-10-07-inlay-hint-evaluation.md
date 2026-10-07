# Inlay-hint evaluation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workspace language whose helper sources export an `EvaluationProfile` shows each statement's value as an LSP inlay hint in VS Code and Rider. This is proven with `examples/DateCalc`.

**Architecture:** `EvaluationProfile` (Nitrogen.Runtime) names the statement syntax kinds, a catalog → handlers factory, builtin values and a formatter. `GrammarWorkspace` finds the one public static profile in the helper sources and binds it to the composed catalog (`BoundEvaluation`). The language service carries it on `LanguageEntry`. `ValueHints` lowers the selected statements, projects each one with `HirProjector`, and caches the hints by version. `LspServer` serves `textDocument/inlayHint` and sends `workspace/inlayHint/refresh` when languages are recompiled.

**Tech Stack:** C# / .NET 10, xUnit, Nitrogen runtime HIR (`HirLowering.LowerSelected`, `HirProjector`, `ProjectionRegistry`), LSP 3.17 inlay hints, Kotlin (Rider platform LSP API 2026.2).

**Spec:** `docs/superpowers/specs/2026-10-07-inlay-hint-evaluation-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `inlay-hint-evaluation`.

**Refinements to the spec**, decided while planning (Task 7 records them in the spec):
1. `EvaluationProfile.Bind` returns `BoundEvaluation(Profile, Registry)` rather than a bare `ProjectionRegistry`. `WorkspaceSnapshot` and `LanguageEntry` each carry one `BoundEvaluation? Evaluation` instead of two properties.
2. `NGR0002` and `NGR0003` are warnings, because the language still works without hints.
3. `HirProjector` already turns a handler exception into an `NP0005` diagnostic. The service's own `try`/`catch` only guards `Builtin` and `Format`. The "throwing handler" test uses `9999-12-31 + 1 days;`, where `DateOnly.AddDays` throws.
4. A hint goes at the end of the nearest ancestor of the root's origin node (or the node itself) whose kind is a statement kind, after trimming trailing whitespace. That's because a `Let` root's origin may be its value expression.
5. Rider: the 2026.2 platform has `LspInlayHintSupport.shouldAskServerForInlayHints(VirtualFile)`, the same opt-in pattern as semantic tokens. `NitrogenHighlighting.kt` is embedded verbatim in generated plugins, so one Kotlin change covers the generic and generated Rider plugins.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.Runtime/Semantic/EvaluationProfile.cs` | `EvaluationProfile`, `BoundEvaluation` | 1 |
| `Nitrogen.Tests/Semantic/EvaluationProfileTests.cs` | profile binding tests | 1 |
| `examples/DateCalc/DateCalcEvaluator.cs` | `Profile`; lazy `Language`; `Run` on the profile | 2 |
| `examples/DateCalc/nitrogen.json` | adds `DateCalcEvaluator.cs` to `sources` | 2 |
| `Nitrogen.Tests/Nitrogen.Tests.csproj` | copies `DateCalcEvaluator.cs` to `DateCalcLanguage/` | 2 |
| `Nitrogen.Workspace/GrammarWorkspace.cs` | profile discovery and binding, `NGR0002`/`NGR0003` | 3 |
| `Nitrogen.Workspace/WorkspaceSnapshot.cs` | `Evaluation` | 3 |
| `Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs` | discovery tests | 3 |
| `Nitrogen.LanguageService/LanguageRegistry.cs` | `LanguageEntry.Evaluation` | 4 |
| `Nitrogen.LanguageService/GrammarLanguages.cs` | passes the evaluation on; `LanguagesVersion` | 4 |
| `Nitrogen.LanguageService/ServiceTypes.cs` | `ValueHint` | 4 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` | `ValueHints`, budget, cache | 4 |
| `Nitrogen.LanguageService/NitrogenLanguageService.cs` | drops cached hints on close | 4 |
| `Nitrogen.Tests/LanguageService/ValueHintTests.cs` | service tests on DateCalc | 4 |
| `Nitrogen.LanguageService/Lsp/LspMessages.cs` | inlay hint messages and capability | 5 |
| `Nitrogen.LanguageService/Lsp/LspServer.cs` | `textDocument/inlayHint`, refresh | 5 |
| `Nitrogen.Tests/LanguageService/InlayHintLspTests.cs` | LSP session tests | 5 |
| `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenHighlighting.kt` | inlay hint opt-in | 6 |
| `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs` | asserts the opt-in is rendered | 6 |
| `README.md`, the spec | docs | 7 |

---

### Task 1: `EvaluationProfile` and `BoundEvaluation`

**Files:**
- Create: `Nitrogen.Runtime/Semantic/EvaluationProfile.cs`
- Test: `Nitrogen.Tests/Semantic/EvaluationProfileTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>An evaluation profile builds its handlers from the catalog it is bound to, and the registry checks them.</summary>
public class EvaluationProfileTests
{
    static readonly OperationSignature Double = new("Twice.Double", SemanticTypes.Scalar, SemanticTypes.Scalar);

    static readonly SemanticCatalog Catalog =
        SemanticCatalog.Compose([new SemanticModule("Twice", ["Core"], [], [Double])], out _)!;

    static EvaluationProfile Profile(Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers) =>
        new(new HashSet<int> { 7 }, handlers, _ => null, value => $"<{value.Value}>");

    static ProjectionHandler Doubling(OperationSignature signature) =>
        new(signature, arguments => new ProjectedValue(SemanticTypes.Scalar, 2 * (float)arguments[0].Value));

    [Fact]
    public void Bind_builds_the_handlers_from_the_catalog()
    {
        var bound = Profile(catalog => [Doubling(catalog.Operations["Twice.Double"])]).Bind(Catalog);

        Assert.Same(Catalog, bound.Registry.Catalog);
        Assert.Equal(new[] { 7 }, bound.Profile.StatementKinds);
    }

    [Fact]
    public void Bind_rejects_an_operation_the_catalog_does_not_export()
    {
        var profile = Profile(_ => [Doubling(new OperationSignature("Twice.Triple", SemanticTypes.Scalar, SemanticTypes.Scalar))]);

        Assert.Throws<ArgumentException>(() => profile.Bind(Catalog));
    }

    [Fact]
    public void Bind_rejects_two_handlers_for_one_operation()
    {
        var profile = Profile(catalog => [Doubling(catalog.Operations["Twice.Double"]), Doubling(catalog.Operations["Twice.Double"])]);

        Assert.Throws<ArgumentException>(() => profile.Bind(Catalog));
    }

    [Fact]
    public void Format_delegates_to_the_profile()
    {
        Assert.Equal("<3>", Profile(_ => []).Format(new ProjectedValue(SemanticTypes.Scalar, 3f)));
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EvaluationProfileTests"`
Expected: build error `The type or namespace name 'EvaluationProfile' could not be found`.

- [ ] **Step 3: Implement**

`Nitrogen.Runtime/Semantic/EvaluationProfile.cs`:

```csharp
using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>
/// How an editor shows the values of a language's statements: which syntax kinds are statements, the
/// host handlers that project their HIR, the values of builtin symbols, and how a value reads as text.
/// A workspace language's helper source exports one as a public static field or property; the
/// workspace binds it to the language's composed catalog.
/// </summary>
/// <param name="handlers">The handlers, built from the catalog the profile is bound to (so their signatures are the catalog's own).</param>
/// <param name="builtins">The value of a builtin symbol, or null when it has none.</param>
public sealed class EvaluationProfile(
    IReadOnlySet<int> statementKinds,
    Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
    Func<Symbol, ProjectedValue?> builtins,
    Func<ProjectedValue, string> format)
{
    readonly Func<SemanticCatalog, IEnumerable<ProjectionHandler>> _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    readonly Func<Symbol, ProjectedValue?> _builtins = builtins ?? throw new ArgumentNullException(nameof(builtins));
    readonly Func<ProjectedValue, string> _format = format ?? throw new ArgumentNullException(nameof(format));

    /// <summary>The syntax kinds whose nodes are lowered and shown, one value each.</summary>
    public IReadOnlySet<int> StatementKinds { get; } = statementKinds ?? throw new ArgumentNullException(nameof(statementKinds));

    /// <summary>The profile with its handlers checked against <paramref name="catalog"/>; throws when a handler differs from it or repeats.</summary>
    public BoundEvaluation Bind(SemanticCatalog catalog) => new(this, new ProjectionRegistry(catalog, _handlers(catalog)));

    public ProjectedValue? Builtin(Symbol symbol) => _builtins(symbol);

    public string Format(ProjectedValue value) => _format(value);
}

/// <summary>A profile bound to one language's catalog: what an editor projects that language's statements with.</summary>
public sealed record BoundEvaluation(EvaluationProfile Profile, ProjectionRegistry Registry);
```

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EvaluationProfileTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Runtime/Semantic/EvaluationProfile.cs Nitrogen.Tests/Semantic/EvaluationProfileTests.cs
git commit -m "Add EvaluationProfile, bound to a language's catalog

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: DateCalc exports its profile

`Run` keeps its output, so the existing `DateCalcTests` are the regression check. `Language` becomes lazy, so reading `Profile` in the workspace doesn't build a second language.

**Files:**
- Modify: `examples/DateCalc/DateCalcEvaluator.cs` (whole file below)
- Modify: `examples/DateCalc/nitrogen.json`
- Modify: `Nitrogen.Tests/Nitrogen.Tests.csproj:42`
- Test: `Nitrogen.Tests/DateCalc/DateCalcTests.cs`

- [ ] **Step 1: Write the failing test.** Add it to `DateCalcTests`:

```csharp
    [Fact]
    public void The_profile_binds_to_the_language_and_selects_its_statements()
    {
        var bound = DateCalcEvaluator.Profile.Bind(DateCalcEvaluator.Language.SemanticCatalog);

        Assert.Equal(new HashSet<int> { DateCalcKinds.Let, DateCalcKinds.Show }, bound.Profile.StatementKinds);
        Assert.Equal("2026-10-05 Mon", DateCalcEvaluator.Profile.Format(
            new Nitrogen.Semantic.ProjectedValue(DateCalcLanguage.Date, new DateOnly(2026, 10, 5))));
    }
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalcTests"`
Expected: build error `'DateCalcEvaluator' does not contain a definition for 'Profile'`.

- [ ] **Step 3: Implement.** First check that nothing else assigns or captures `DateCalcEvaluator.Language` as a field: `grep -rn "DateCalcEvaluator\.\(Language\|Handlers\)" --include=*.cs . | grep -v /obj/`. Expect only this file and the new test. Then replace `examples/DateCalc/DateCalcEvaluator.cs` with:

```csharp
using System.Globalization;
using Nitrogen;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace DateCalc.Syntax;

/// <summary>One shown value, or one diagnostic, at a source line (1-based).</summary>
public sealed record DateCalcLine(int Line, string Text, bool IsError = false);

/// <summary>
/// Runs DateCalc source: parse, bind, check, lower to typed HIR, then project each statement through
/// <see cref="Profile"/>'s handlers. The language server finds <see cref="Profile"/> too and shows each
/// statement's value as an inlay hint.
/// </summary>
public static class DateCalcEvaluator
{
    static readonly Lazy<Language> s_language = new(() => new LanguageBuilder().Add(DateCalcModule.Instance)
        .AddSemantic(MathModule.Semantics).AddSemantic(DateCalcLanguage.Semantics).Build());

    static readonly Lazy<BoundEvaluation> s_bound = new(() => Profile.Bind(Language.SemanticCatalog));

    /// <summary>The language <see cref="Run"/> parses with, built on first use: reading <see cref="Profile"/> does not build it.</summary>
    public static Language Language => s_language.Value;

    /// <summary>Lets and shown expressions, each projected to a number, date, duration or text.</summary>
    public static readonly EvaluationProfile Profile = new(
        new HashSet<int> { DateCalcKinds.Let, DateCalcKinds.Show },
        Handlers,
        symbol => MathModule.Constants.Where(c => c.Name == symbol.Name)
            .Select(c => new ProjectedValue(DateCalcLanguage.Number, c.Value)).FirstOrDefault(),
        value => Show(value.Value));

    /// <summary>The value of each statement, in order, or the diagnostics that stop it from running.</summary>
    public static IReadOnlyList<DateCalcLine> Run(string source)
    {
        using var parsed = Language.Parse(source, DateCalcModule.Program);
        var project = new Project(Language);
        project.Set("input.datecalc", parsed.Tree);
        var file = new ProjectSemantics(project)["input.datecalc"];
        var errors = new List<DateCalcLine>();
        foreach (var diagnostic in parsed.Diagnostics) errors.Add(Error(source, diagnostic.Span, parsed.FormatMessage(diagnostic)));
        errors.AddRange(project.Diagnostics("input.datecalc").Select(d => Error(source, d.Span, d.Message)));
        errors.AddRange(file.Diagnostics().Select(d => Error(source, d.Span, $"{d.Code}: {d.Message}")));
        if (errors.Count > 0) return errors;

        var lowered = HirLowering.LowerSelected(file, Profile.StatementKinds, Guid.NewGuid());
        if (lowered.Diagnostics.Count > 0)
            return lowered.Diagnostics.Select(d => Error(source, d.Origin.Span, $"{d.Code}: {d.Message}")).ToList();
        var registry = s_bound.Value.Registry;
        return lowered.Roots.Select(root =>
        {
            var result = HirProjector.Project(root, registry, Builtins(root));
            return result.Value is { } value
                ? new DateCalcLine(LineOf(source, root.Origins[0].Span.Start), Profile.Format(value))
                : Error(source, root.Origins[0].Span, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        }).ToList();
    }

    static IEnumerable<ProjectionHandler> Handlers(SemanticCatalog catalog)
    {
        ProjectionHandler Handler(string id, Func<object[], object> run)
        {
            var signature = catalog.Operations[id];
            return new(signature, arguments => new ProjectedValue(signature.Result, run(arguments.Select(a => a.Value).ToArray())));
        }

        return
        [
            Handler(DateCalcLanguage.ParseDate.Id, a => DateOnly.ParseExact((string)a[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Handler(DateCalcLanguage.Days.Id, a => TimeSpan.FromDays((float)a[0])),
            Handler(DateCalcLanguage.Weeks.Id, a => TimeSpan.FromDays(7 * (float)a[0])),
            Handler(DateCalcLanguage.InDays.Id, a => (float)((TimeSpan)a[0]).TotalDays),
            Handler("DateCalc.Add", a => (float)a[0] + (float)a[1]),
            Handler("DateCalc.Subtract", a => (float)a[0] - (float)a[1]),
            Handler("DateCalc.Multiply", a => (float)a[0] * (float)a[1]),
            Handler("DateCalc.Divide", a => (float)a[0] / (float)a[1]),
            Handler("DateCalc.Later", a => ((DateOnly)a[0]).AddDays((int)((TimeSpan)a[1]).TotalDays)),
            Handler("DateCalc.Earlier", a => ((DateOnly)a[0]).AddDays(-(int)((TimeSpan)a[1]).TotalDays)),
            Handler("DateCalc.Between", a => TimeSpan.FromDays(((DateOnly)a[0]).DayNumber - ((DateOnly)a[1]).DayNumber)),
            Handler("DateCalc.AddDurations", a => (TimeSpan)a[0] + (TimeSpan)a[1]),
            Handler("DateCalc.SubtractDurations", a => (TimeSpan)a[0] - (TimeSpan)a[1]),
            Handler("DateCalc.Scale", a => (TimeSpan)a[0] * (float)a[1]),
            Handler("DateCalc.Times", a => (float)a[0] * (TimeSpan)a[1]),
            Handler("DateCalc.Ratio", a => (float)((TimeSpan)a[0] / (TimeSpan)a[1])),
            .. DateCalcLanguage.AllFunctions.Select(f => Handler(f.Signature.Id, f.Run)),
        ];
    }

    /// <summary>The values of the builtin constants the root refers to.</summary>
    static Dictionary<Symbol, ProjectedValue> Builtins(HirNode root) => HirTraversal.PreOrder(root).OfType<HirSymbolRef>()
        .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
        .Select(s => (Symbol: s, Value: Profile.Builtin(s))).Where(p => p.Value is not null)
        .ToDictionary(p => p.Symbol, p => p.Value!);

    static string Show(object value) => value switch
    {
        float number => number.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
        TimeSpan span => $"{span.TotalDays.ToString(CultureInfo.InvariantCulture)} days",
        _ => value.ToString() ?? "",
    };

    static DateCalcLine Error(string source, TextSpan span, string message) => new(LineOf(source, span.Start), message, true);

    static int LineOf(string source, int offset) => source.AsSpan(0, Math.Min(offset, source.Length)).Count('\n') + 1;
}
```

In `examples/DateCalc/nitrogen.json`, change the `sources` line to:

```json
      "sources": ["DateCalcLanguage.cs", "MathModule.cs", "DateCalcEvaluator.cs"],
```

In `Nitrogen.Tests/Nitrogen.Tests.csproj`, add the evaluator to the files copied to `DateCalcLanguage/`:

```xml
    <None Include="..\examples\DateCalc\nitrogen.json;..\examples\DateCalc\DateCalc.ngr;..\examples\DateCalc\DateCalcLanguage.cs;..\examples\DateCalc\MathModule.cs;..\examples\DateCalc\DateCalcEvaluator.cs;..\examples\DateCalc\sample.datecalc"
          Link="DateCalcLanguage\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 4: Run the DateCalc tests and the workspace DateCalc tests and check they pass.** `LoweredLanguageTests` compiles the copied `nitrogen.json` in a `GrammarWorkspace` and asserts `DateCalc.ngr` has no diagnostics, so it confirms that `DateCalcEvaluator.cs` compiles as a helper source.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalc|FullyQualifiedName~LoweredLanguageTests|FullyQualifiedName~EmbeddedStringTests|FullyQualifiedName~WorkspaceLanguageSemanticsTests"`
Expected: all pass. If `LoweredLanguageTests` reports a C# error located on `DateCalc.ngr`, the workspace-generated names differ from the build generator's. Fix the reference in `DateCalcEvaluator.cs`, and don't drop the file from `sources`.

- [ ] **Step 5: Commit**

```bash
git add examples/DateCalc/DateCalcEvaluator.cs examples/DateCalc/nitrogen.json Nitrogen.Tests/Nitrogen.Tests.csproj Nitrogen.Tests/DateCalc/DateCalcTests.cs
git commit -m "Run DateCalc through an exported evaluation profile

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The workspace finds and binds the profile

**Files:**
- Modify: `Nitrogen.Workspace/GrammarWorkspace.cs` (`Compile`, `SemanticModules`)
- Modify: `Nitrogen.Workspace/WorkspaceSnapshot.cs`
- Test: `Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A workspace language takes its evaluation profile from its helper sources, bound to its catalog.</summary>
public class WorkspaceEvaluationTests
{
    const string Sum = """
        syntax module Sum
        {
          token Number = ['0'..'9']+;
          syntax Doc = Item:Add;
          syntax Add = "add" Left:Num Right:Num ";" lowers Sum.Add(Left, Right);
          syntax Num = Text:Number lowers literal Core.Scalar Text;
        }
        """;

    const string Semantics = """
        using Nitrogen.Semantic;

        public static class SumSemantics
        {
            public static readonly OperationSignature Add = new("Sum.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
            public static readonly SemanticModule Module = new("Sum", [], [], [Add]);
        }
        """;

    static string Profile(string type, string operation = "Sum.Add") => $$"""
        using Nitrogen.Semantic;
        using Nitrogen.Workspace.Grammar;

        public static class {{type}}
        {
            public static EvaluationProfile Profile { get; } = new(new HashSet<int> { SumKinds.Add },
                catalog => [new ProjectionHandler(catalog.Operations["{{operation}}"],
                    a => new ProjectedValue(SemanticTypes.Scalar, (float)a[0].Value + (float)a[1].Value))],
                _ => null, value => value.Value.ToString()!);
        }
        """;

    static WorkspaceSnapshot Compile(params string[] sources)
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("sum.ngr", Sum);
        workspace.Sources["SumSemantics.cs"] = Semantics;
        for (int i = 0; i < sources.Length; i++) workspace.Sources[$"Profile{i}.cs"] = sources[i];
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    [Fact]
    public void One_profile_is_bound_to_the_language_catalog_and_projects()
    {
        using var snapshot = Compile(Profile("SumProfile"));
        var evaluation = Assert.IsType<BoundEvaluation>(snapshot.Evaluation);
        Assert.Same(snapshot.Language!.SemanticCatalog, evaluation.Registry.Catalog);
        Assert.Empty(snapshot.Diagnostics);

        using var parsed = snapshot.Parse("add 1 2;", "Sum.Doc");
        var project = new Project(snapshot.Language!);
        project.Set("a.sum", parsed.Tree);
        var lowered = HirLowering.LowerSelected(new ProjectSemantics(project)["a.sum"], evaluation.Profile.StatementKinds, Guid.NewGuid());
        var result = HirProjector.Project(Assert.Single(lowered.Roots), evaluation.Registry);
        Assert.Equal("3", evaluation.Profile.Format(result.Value!));
    }

    [Fact]
    public void Without_a_profile_the_language_has_no_evaluation()
    {
        using var snapshot = Compile();
        Assert.Null(snapshot.Evaluation);
    }

    [Fact]
    public void Two_profiles_are_a_warning_and_neither_is_used()
    {
        using var snapshot = Compile(Profile("First"), Profile("Second"));
        Assert.Null(snapshot.Evaluation);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0003", warning.Code);
        Assert.False(warning.IsError);
    }

    [Fact]
    public void A_profile_that_does_not_bind_is_a_warning()
    {
        using var snapshot = Compile(Profile("SumProfile", "Sum.Missing"));
        Assert.Null(snapshot.Evaluation);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0002", warning.Code);
        Assert.False(warning.IsError);
        Assert.Contains("Sum.Missing", warning.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceEvaluationTests"`
Expected: build error `'WorkspaceSnapshot' does not contain a definition for 'Evaluation'`.

- [ ] **Step 3: Implement `WorkspaceSnapshot.Evaluation`.** In `Nitrogen.Workspace/WorkspaceSnapshot.cs`, add `using Nitrogen.Semantic;` and change the constructor and `Dispose`:

```csharp
    internal WorkspaceSnapshot(int version, IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        Language? language = null, WorkspaceLoadContext? context = null, SyntaxModule[]? modules = null,
        BoundEvaluation? evaluation = null)
    {
        Version = version;
        Diagnostics = diagnostics;
        Language = language;
        Evaluation = evaluation;
        _context = context;
        _modules = modules ?? [];
    }
```

```csharp
    /// <summary>The helper sources' evaluation profile bound to <see cref="Language"/>'s catalog; null when there is none, or it did not bind.</summary>
    public BoundEvaluation? Evaluation { get; private set; }
```

In `Dispose`, add `Evaluation = null;` after `Language = null;`.

- [ ] **Step 4: Implement discovery.** In `Nitrogen.Workspace/GrammarWorkspace.cs`:

Replace the snapshot construction in `Compile`:

```csharp
            var evaluation = Evaluation(types, language, diagnostics);
            var snapshot = new WorkspaceSnapshot(version, diagnostics, language, context, modules, evaluation);
```

Replace `SemanticModules` with a shared scan and add `Evaluation`:

```csharp
    /// <summary>
    /// The semantic modules of the public static <see cref="ModuleDescriptor"/> and <see cref="SemanticModule"/>
    /// fields and properties of <paramref name="types"/>, each once, ordered by type and member name.
    /// </summary>
    static List<SemanticModule> SemanticModules(IEnumerable<Type> types)
    {
        var found = new List<SemanticModule>();
        foreach (var value in StaticValues(types, t => t == typeof(ModuleDescriptor) || t == typeof(SemanticModule)))
            if ((value as SemanticModule ?? (value as ModuleDescriptor)?.Semantics) is { } module &&
                !found.Any(m => ReferenceEquals(m, module)))
                found.Add(module);
        return found;
    }

    /// <summary>
    /// The one public static <see cref="EvaluationProfile"/> of <paramref name="types"/>, bound to the language's
    /// catalog. Two profiles (NGR0003) or one that does not bind (NGR0002) are warnings, and the language has none.
    /// </summary>
    static BoundEvaluation? Evaluation(IEnumerable<Type> types, Language language, List<WorkspaceDiagnostic> diagnostics)
    {
        var profiles = StaticValues(types, t => t == typeof(EvaluationProfile)).OfType<EvaluationProfile>().Distinct().ToList();
        if (profiles.Count == 0) return null;
        if (profiles.Count > 1)
        {
            diagnostics.Add(new WorkspaceDiagnostic("", 0, 0, "NGR0003",
                $"{profiles.Count} evaluation profiles; a language has at most one, so none is used", IsError: false));
            return null;
        }
        try
        {
            return profiles[0].Bind(language.SemanticCatalog);
        }
        catch (Exception error)
        {
            diagnostics.Add(new WorkspaceDiagnostic("", 0, 0, "NGR0002",
                $"the evaluation profile does not bind: {error.GetBaseException().Message}", IsError: false));
            return null;
        }
    }

    /// <summary>The public static fields, then properties, of the public <paramref name="types"/> whose type is <paramref name="wanted"/>, ordered by type and member name.</summary>
    static IEnumerable<object?> StaticValues(IEnumerable<Type> types, Func<Type, bool> wanted) =>
        types.Where(t => t.IsPublic || t.IsNestedPublic).OrderBy(t => t.FullName, StringComparer.Ordinal).SelectMany(type =>
            type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => wanted(f.FieldType))
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.GetValue(null))
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(p => p.GetIndexParameters().Length == 0 && wanted(p.PropertyType))
                    .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.GetValue(null))));
```

Update the class summary's second sentence to read: "The helper sources' public static `ModuleDescriptor` and `SemanticModule` fields and properties supply the language's semantic modules, so its declarative typing and lowering run, and an `EvaluationProfile` among them says how an editor shows its values." Use `<see cref>` as the surrounding text does.

If the compiler reports that `language` may be null at `Evaluation(types, language, diagnostics)`, `TryBuild` is missing `[NotNullWhen(true)]`. Pass `language!` and don't change `TryBuild`.

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceEvaluationTests|FullyQualifiedName~WorkspaceSemanticsTests|FullyQualifiedName~WorkspaceTests|FullyQualifiedName~LoweredLanguageTests"`
Expected: all pass. `LoweredLanguageTests` now also binds DateCalc's profile; an NGR0002 there would fail its empty-diagnostics assertion.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Workspace/GrammarWorkspace.cs Nitrogen.Workspace/WorkspaceSnapshot.cs Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs
git commit -m "Find a workspace language's evaluation profile and bind it

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `ValueHints` in the language service

**Files:**
- Modify: `Nitrogen.LanguageService/LanguageRegistry.cs` (`LanguageEntry`)
- Modify: `Nitrogen.LanguageService/GrammarLanguages.cs` (`Compile`, `Reregister`, `Unregister`)
- Modify: `Nitrogen.LanguageService/ServiceTypes.cs`
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.cs` (`Close`)
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs`
- Test: `Nitrogen.Tests/LanguageService/ValueHintTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Each DateCalc statement's value, as the editor shows it after the statement.</summary>
public sealed class ValueHintTests : IDisposable
{
    static readonly DocumentRange Whole = new(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-hints-").FullName;

    public ValueHintTests()
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
        service.Open(Uri("DateCalc.ngr"), 1, File.ReadAllText(Path.Combine(_root, "DateCalc.ngr")));
        Assert.Empty(service.Diagnostics(Uri("DateCalc.ngr")));
        return service;
    }

    IReadOnlyList<ValueHint> Hints(NitrogenLanguageService service, string text, DocumentRange? range = null)
    {
        string uri = Uri("a.datecalc");
        service.Open(uri, 1, text);
        return service.ValueHints(uri, range ?? Whole);
    }

    [Fact]
    public void The_sample_shows_each_statement_value_at_its_end()
    {
        using var service = Service();
        string text = File.ReadAllText(Path.Combine(_root, "sample.datecalc"));
        var hints = Hints(service, text);

        Assert.Equal(
            ["= 2026-10-05 Mon", "= 14 days", "= 2026-10-19 Mon", "= Monday", "= 81", "= 14", "= 6.28", "= 2026-12-25 Fri"],
            hints.Select(h => h.Label));
        string[] lines = text.Split('\n');
        Assert.Equal(Enumerable.Range(1, 8).Select(line => new DocumentPosition(line, lines[line].TrimEnd().Length)),
            hints.Select(h => h.At));
        Assert.All(hints, h => Assert.False(h.IsError));
        Assert.All(hints, h => Assert.Null(h.Tooltip));
    }

    [Fact]
    public void Only_hints_within_the_range_are_returned()
    {
        using var service = Service();
        var hints = Hints(service, File.ReadAllText(Path.Combine(_root, "sample.datecalc")),
            new DocumentRange(new DocumentPosition(2, 0), new DocumentPosition(3, int.MaxValue)));

        Assert.Equal(["= 14 days", "= 2026-10-19 Mon"], hints.Select(h => h.Label));
    }

    [Fact]
    public void A_document_with_an_error_shows_no_values()
    {
        using var service = Service();
        Assert.Empty(Hints(service, "let a = 2026-10-05 + 2026-10-06;\n1 + 1;"));
    }

    [Fact]
    public void A_failing_statement_shows_a_warning_and_later_ones_still_run()
    {
        using var service = Service();
        var hints = Hints(service, "9999-12-31 + 1 days;\n1 + 1;");

        Assert.Equal(2, hints.Count);
        Assert.True(hints[0].IsError);
        Assert.Equal("= ⚠", hints[0].Label);
        Assert.StartsWith("NP0005: ", hints[0].Tooltip, StringComparison.Ordinal);
        Assert.Contains("DateCalc.Later", hints[0].Tooltip, StringComparison.Ordinal);
        Assert.Equal("= 2", hints[1].Label);
    }

    [Fact]
    public void The_budget_stops_after_the_first_statement_once_spent()
    {
        using var service = Service();
        service.EvaluationBudget = TimeSpan.Zero;

        var hint = Assert.Single(Hints(service, File.ReadAllText(Path.Combine(_root, "sample.datecalc"))));
        Assert.Equal("= 2026-10-05 Mon", hint.Label);
    }

    [Fact]
    public void An_edit_shows_the_new_values()
    {
        using var service = Service();
        Assert.Equal("= 2", Assert.Single(Hints(service, "1 + 1;")).Label);

        service.Change(Uri("a.datecalc"), 2, "2 + 2;");
        Assert.Equal("= 4", Assert.Single(service.ValueHints(Uri("a.datecalc"), Whole)).Label);
    }

    [Fact]
    public void A_language_without_a_profile_shows_no_values()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = 1; }");
        Assert.Empty(service.ValueHints("file:///w/a.scopes", Whole));
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ValueHintTests"`
Expected: build error `The type or namespace name 'ValueHint' could not be found`.

- [ ] **Step 3: Add `ValueHint`.** Append to `Nitrogen.LanguageService/ServiceTypes.cs`:

```csharp
/// <summary>A statement's value, shown after it: <paramref name="Label"/> at <paramref name="At"/>; a failure has a ⚠ label, the reason as its tooltip.</summary>
public sealed record ValueHint(DocumentPosition At, string Label, string? Tooltip, bool IsError);
```

- [ ] **Step 4: Carry the evaluation on `LanguageEntry`.** In `Nitrogen.LanguageService/LanguageRegistry.cs`, add `using Nitrogen.Semantic;`, a `<param name="evaluation">How its statements' values are shown; none when null.</param>` doc line, a constructor parameter and a property:

```csharp
public sealed class LanguageEntry(string name, Language language, IReadOnlyDictionary<string, Rule> starts, Presentation? presentation = null,
    ILanguageAssist? assist = null, BoundEvaluation? evaluation = null)
```

```csharp
    public BoundEvaluation? Evaluation { get; } = evaluation;
```

- [ ] **Step 5: Pass it on and count language changes.** In `Nitrogen.LanguageService/GrammarLanguages.cs`:

In `Compile`, replace the `LanguageEntry` construction:

```csharp
        var entry = new LanguageEntry(language.Name, snapshot.Language!, language.Extensions.ToDictionary(e => e, _ => rule), language.Presentation,
            evaluation: snapshot.Evaluation);
```

Add the counter next to `_root`:

```csharp
    /// <summary>Counts workspace languages served, replaced or dropped: the LSP server asks the editor for new inlay hints when it moves.</summary>
    public int LanguagesVersion { get; private set; }
```

Make `LanguagesVersion++;` the first statement of `Reregister`. In `Unregister`, add it right after the `if (language.Entry is not { } entry) return [];` line.

- [ ] **Step 6: Implement `ValueHints`.** Create `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs`:

```csharp
using System.Diagnostics;
using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>
/// Statement values as inlay hints: a language whose entry has an evaluation lowers its statements and
/// projects each through the profile's handlers. Nothing is shown while the document has an error, and
/// evaluation stops starting statements once its budget is spent.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    sealed record HintCacheEntry(int DocumentVersion, int ProjectVersion, LanguageEntry Language, IReadOnlyList<ValueHint> Hints);

    readonly Dictionary<string, HintCacheEntry> _hints = new(StringComparer.Ordinal);

    /// <summary>How long one document's statements may run; the first always runs, later ones start only within it.</summary>
    internal TimeSpan EvaluationBudget { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The values of the document's statements within <paramref name="range"/>; empty when its language shows none.</summary>
    public IReadOnlyList<ValueHint> ValueHints(string uri, DocumentRange range)
    {
        if (!_documents.TryGetValue(uri, out var document) || document.Language.Evaluation is not { } evaluation) return [];
        var project = _projects[document.Language];
        if (!_hints.TryGetValue(uri, out var hit) || hit.DocumentVersion != document.Version ||
            hit.ProjectVersion != project.Version || !ReferenceEquals(hit.Language, document.Language))
        {
            hit = new HintCacheEntry(document.Version, project.Version, document.Language, Evaluate(document, evaluation));
            _hints[uri] = hit;
        }
        return hit.Hints.Where(h => Within(h.At, range)).ToList();
    }

    IReadOnlyList<ValueHint> Evaluate(Document document, BoundEvaluation evaluation)
    {
        if (Diagnostics(document.Uri).Any(d => d.Severity == ServiceSeverity.Error)) return [];
        var profile = evaluation.Profile;
        var file = SemanticsOf(document.Language)[document.Uri];
        var lowered = HirLowering.LowerSelected(file, profile.StatementKinds, Guid.NewGuid());
        if (lowered.Diagnostics.Count > 0) return [];

        var hints = new List<ValueHint>();
        var clock = Stopwatch.StartNew();
        foreach (var root in lowered.Roots)
        {
            if (hints.Count > 0 && clock.Elapsed >= EvaluationBudget) break;
            var at = document.Lines.PositionOf(StatementEnd(file.Tree, root.Origins[0], profile.StatementKinds, document.Text));
            hints.Add(Hint(at, root, evaluation));
        }
        return hints;
    }

    static ValueHint Hint(DocumentPosition at, HirNode root, BoundEvaluation evaluation)
    {
        try
        {
            var inputs = HirTraversal.PreOrder(root).OfType<HirSymbolRef>()
                .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
                .Select(s => (Symbol: s, Value: evaluation.Profile.Builtin(s))).Where(p => p.Value is not null)
                .ToDictionary(p => p.Symbol, p => p.Value!);
            var result = HirProjector.Project(root, evaluation.Registry, inputs);
            return result.Value is { } value
                ? new ValueHint(at, "= " + evaluation.Profile.Format(value), null, false)
                : Failure(at, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }
        catch (Exception error) // a profile's Builtin or Format threw; handlers' own exceptions are NP0005 diagnostics
        {
            return Failure(at, $"{error.GetType().Name}: {error.Message}");
        }
    }

    static ValueHint Failure(DocumentPosition at, string reason) => new(at, "= ⚠", reason, true);

    /// <summary>The end of the statement the origin lies in, before its trailing whitespace: where its value is shown.</summary>
    static int StatementEnd(SyntaxTree tree, SourceOrigin origin, IReadOnlySet<int> statementKinds, string text)
    {
        int node = origin.Node;
        while (node >= 0 && !statementKinds.Contains(tree.Kind(node))) node = tree.Parent(node);
        var span = node >= 0 ? tree.Span(node) : origin.Span;
        int end = span.End;
        while (end > span.Start && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    static bool Within(DocumentPosition at, DocumentRange range) =>
        (at.Line, at.Character).CompareTo((range.Start.Line, range.Start.Character)) >= 0 &&
        (at.Line, at.Character).CompareTo((range.End.Line, range.End.Character)) <= 0;
}
```

In `Nitrogen.LanguageService/NitrogenLanguageService.cs`, add `_hints.Remove(uri);` in `Close`, right after `_inspection.Remove(uri);`.

If `SyntaxTree` is ambiguous in this file (it lives in namespace `Nitrogen`), qualify it as `Nitrogen.SyntaxTree`. If `Symbol` is ambiguous, the code above never names it, so no change is needed there.

- [ ] **Step 7: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~ValueHintTests|FullyQualifiedName~LanguageService"`
Expected: all pass. If `The_sample_shows_each_statement_value_at_its_end` fails on positions only, print `hints.Select(h => h.At)`. A character past the `;` means the statement span includes trailing trivia that isn't whitespace (a comment). Change `StatementEnd` to clamp to the last token, and don't loosen the test.

- [ ] **Step 8: Commit**

```bash
git add Nitrogen.LanguageService Nitrogen.Tests/LanguageService/ValueHintTests.cs
git commit -m "Show each statement's value through the language's evaluation profile

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `textDocument/inlayHint` and refresh

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/InlayHintLspTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Statement values over LSP: the inlay hint request, and a refresh when the language is recompiled.</summary>
public sealed class InlayHintLspTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-inlay-").FullName;

    public InlayHintLspTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    string Initialize(string capabilities) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri
        + "\",\"capabilities\":" + capabilities + "}}";

    const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";
    const string Shutdown = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""";
    const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";

    async Task<List<JsonElement>> Session(params string[] bodies)
    {
        using var service = new Nitrogen.LanguageService.NitrogenLanguageService(Nitrogen.Cli.LspCommand.Registry());
        return (await LspServerTests.Session(service, bodies)).Messages;
    }

    [Fact]
    public async Task The_server_advertises_and_answers_inlay_hints_within_the_range()
    {
        string sample = Uri("sample.datecalc");
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + sample
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":" + JsonSerializer.Serialize(File.ReadAllText(Path.Combine(_root, "sample.datecalc"))) + "}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/inlayHint\",\"params\":{\"textDocument\":{\"uri\":\"" + sample
            + "\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":2,\"character\":100}}}}";

        var messages = await Session(Initialize("{}"), Initialized, open, request, Shutdown, Exit);

        Assert.True(messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("inlayHintProvider").GetBoolean());
        var hints = messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5)
            .GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(2, hints.Count);
        Assert.Equal("= 2026-10-05 Mon", hints[0].GetProperty("label").GetString());
        Assert.Equal(1, hints[0].GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal("let start = 2026-10-05;".Length, hints[0].GetProperty("position").GetProperty("character").GetInt32());
        Assert.True(hints[0].GetProperty("paddingLeft").GetBoolean());
        Assert.False(hints[0].TryGetProperty("tooltip", out _));
        Assert.Equal("= 14 days", hints[1].GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("""{"workspace":{"inlayHint":{"refreshSupport":true}}}""", 1)]
    [InlineData("{}", 0)]
    public async Task A_recompiled_language_refreshes_hints_when_the_client_supports_it(string capabilities, int refreshes)
    {
        string watched = "{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{\"changes\":[{\"uri\":\""
            + Uri("DateCalcEvaluator.cs") + "\",\"type\":2}]}}";

        var messages = await Session(Initialize(capabilities), Initialized, watched, Shutdown, Exit);

        Assert.Equal(refreshes, messages.Count(m => m.TryGetProperty("method", out var method) && method.GetString() == "workspace/inlayHint/refresh"));
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~InlayHintLspTests"`
Expected: FAIL. `inlayHintProvider` is missing (a `KeyNotFoundException`), and the request gets a `-32601` error.

- [ ] **Step 3: Add the messages.** In `Nitrogen.LanguageService/Lsp/LspMessages.cs`:

Add the last parameter to `ServerCapabilities`:

```csharp
    CompletionOptions? CompletionProvider = null,
    bool? InlayHintProvider = null);
```

Replace `WorkspaceClientCapabilities` and add the client capability:

```csharp
public sealed record InlayHintWorkspaceClientCapabilities(bool? RefreshSupport);

public sealed record WorkspaceClientCapabilities(DidChangeWatchedFilesClientCapabilities? DidChangeWatchedFiles,
    InlayHintWorkspaceClientCapabilities? InlayHint = null);
```

Add the request and result:

```csharp
public sealed record InlayHintParams(TextDocumentIdentifier TextDocument, LspRange Range);

/// <summary>An inlay hint with a plain-text label; no kind, since a value is neither a type nor a parameter name.</summary>
public sealed record LspInlayHint(LspPosition Position, string Label, bool? PaddingLeft = null, string? Tooltip = null);
```

Register them on `LspJson`:

```csharp
[JsonSerializable(typeof(InlayHintParams))]
[JsonSerializable(typeof(LspInlayHint[]))]
```

- [ ] **Step 4: Serve them.** In `Nitrogen.LanguageService/Lsp/LspServer.cs`:

Add the fields next to `_watchDynamically`:

```csharp
    bool _refreshInlayHints;  // the client takes workspace/inlayHint/refresh
    int _languagesSeen;       // service.LanguagesVersion when the client last had current hints
    int _refreshes;
```

In `initialize`, after `_watchDynamically = ...`:

```csharp
                    _refreshInlayHints = initialize?.Capabilities?.Workspace?.InlayHint?.RefreshSupport == true;
```

Add `InlayHintProvider: true` to the `ServerCapabilities` in the `initialize` response, after `CompletionProvider: new CompletionOptions(["."])`.

Add the request case before `default:`:

```csharp
            case "textDocument/inlayHint":
            {
                var request = Params(parameters, LspJson.Default.InlayHintParams);
                var range = new DocumentRange(Position(request.Range.Start), Position(request.Range.End));
                var hints = service.ValueHints(request.TextDocument.Uri, range)
                    .Select(h => new LspInlayHint(new LspPosition(h.At.Line, h.At.Character), h.Label, PaddingLeft: true, h.Tooltip))
                    .ToArray();
                await RespondAsync(id, hints, LspJson.Default.LspInlayHintArray, cancel);
                break;
            }
```

In `HandleNotificationAsync`, the `initialized` case: after the `ConfigureWorkspace` and `IndexWorkspace` lines, add `_languagesSeen = service.LanguagesVersion;`. The client hasn't asked for hints yet, so nothing needs refreshing.

In `RunAsync`, after the notification is handled (`else await HandleNotificationAsync(method, parameters, cancel);`), make it:

```csharp
                    else
                    {
                        await HandleNotificationAsync(method, parameters, cancel);
                        await RefreshInlayHintsAsync(cancel);
                    }
```

Add the method next to `RegisterWatchersAsync`:

```csharp
    /// <summary>Asks the client to request inlay hints again once a language was recompiled, if it can (<c>workspace/inlayHint/refresh</c>).</summary>
    Task RefreshInlayHintsAsync(CancellationToken cancel)
    {
        if (!_refreshInlayHints || service.LanguagesVersion == _languagesSeen) return Task.CompletedTask;
        _languagesSeen = service.LanguagesVersion;
        int number = ++_refreshes;
        return connection.WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("id", $"nitrogen-inlay-refresh-{number}");
            w.WriteString("method", "workspace/inlayHint/refresh");
            w.WriteEndObject();
        }, cancel);
    }
```

The client's response to this request has an `id` and no `method`, so the existing `if (isRequest && method.Length == 0) continue;` skips it.

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~InlayHintLspTests|FullyQualifiedName~LspServerTests|FullyQualifiedName~GrammarLoopTests|FullyQualifiedName~WorkspaceIndexTests"`
Expected: all pass. Existing sessions send `{}` capabilities, so they see no refresh and their message counts don't change.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp Nitrogen.Tests/LanguageService/InlayHintLspTests.cs
git commit -m "Serve statement values as LSP inlay hints

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Rider asks for inlay hints

VS Code needs no change: `vscode-languageclient` 9 registers an inlay hint provider whenever the server advertises `inlayHintProvider`. Rider's platform client checks `LspInlayHintSupport.shouldAskServerForInlayHints(file)` first. `NitrogenHighlighting.kt` is embedded verbatim in generated plugins (`Nitrogen.Cli.csproj`, `rider/NitrogenHighlighting.kt`).

**Files:**
- Modify: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenHighlighting.kt`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs` (`Rendered_build_uses_the_template_plugins_and_platform_api`)

- [ ] **Step 1: Write the failing assertion.** At the end of `Rendered_build_uses_the_template_plugins_and_platform_api`, add:

```csharp
        // Rider asks a server for inlay hints (statement values) only where a client opts in.
        Assert.Contains("override val inlayHintCustomizer: LspInlayHintCustomizer = NitrogenInlayHints",
            Read("src/main/kotlin/org/nitrogen/rider/NitrogenHighlighting.kt"), StringComparison.Ordinal);
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests"`
Expected: FAIL with `Assert.Contains() Failure`.

- [ ] **Step 3: Implement.** Change `NitrogenHighlighting.kt` to:

```kotlin
package org.nitrogen.rider

import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.customization.LspCustomization
import com.intellij.platform.lsp.api.customization.LspInlayHintCustomizer
import com.intellij.platform.lsp.api.customization.LspInlayHintSupport
import com.intellij.platform.lsp.api.customization.LspSemanticTokensCustomizer
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport
import com.intellij.psi.PsiFile

/**
 * Semantic tokens for every file the client serves. The platform asks a server for them only in plain
 * text and TextMate files by default, which leaves a Nitrogen language (it has its own file type) and
 * C# strings uncoloured.
 */
object NitrogenSemanticTokens : LspSemanticTokensSupport() {
    override fun shouldAskServerForSemanticTokens(psiFile: PsiFile): Boolean = true
}

/** Inlay hints for every file the client serves: a language with an evaluation profile shows each statement's value. */
object NitrogenInlayHints : LspInlayHintSupport() {
    override fun shouldAskServerForInlayHints(file: VirtualFile): Boolean = true
}

/** A language's own client: the platform's defaults, with semantic tokens and inlay hints everywhere. */
open class NitrogenCustomization : LspCustomization() {
    override val semanticTokensCustomizer: LspSemanticTokensCustomizer = NitrogenSemanticTokens
    override val inlayHintCustomizer: LspInlayHintCustomizer = NitrogenInlayHints
}
```

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests"`
Expected: all pass.

- [ ] **Step 5: Compile the Kotlin.** This needs a JDK 25. Check with `java -version`. If it's present, run:

```bash
cd editors/rider && gradle buildPlugin
```

Expected: `BUILD SUCCESSFUL`. If there's no JDK on this machine, say so in the task report: the Plugins workflow (`.github/workflows/plugins.yml`) compiles it on the pull request. Don't mark the Kotlin change as verified until then.

- [ ] **Step 6: Commit**

```bash
git add editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenHighlighting.kt Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "Ask the server for inlay hints in Rider plugins

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Docs and the full check

**Files:**
- Modify: `README.md` (both "Available editor features include …" sentences, near lines 120 and 282)
- Modify: `docs/superpowers/specs/2026-10-07-inlay-hint-evaluation-design.md`

- [ ] **Step 1: README.** In both places, change "Available editor features include diagnostics, semantic coloring, outline, go to definition, references, rename, hover, and completion;" to:

"Available editor features include diagnostics, semantic coloring, outline, go to definition, references, rename, hover, completion, and, for a language whose helper sources export an `EvaluationProfile` (as `examples/DateCalc` does), each statement's value as an inlay hint;"

Leave the rest of each sentence unchanged.

- [ ] **Step 2: Spec.** Change the status line to `Status: implemented (YYYY-MM-DD).`, using the date of this step. Under section 1, replace `public ProjectionRegistry Bind(SemanticCatalog catalog); // ...` with `public BoundEvaluation Bind(SemanticCatalog catalog); // (this, new ProjectionRegistry(catalog, handlers(catalog)))`, and add `public sealed record BoundEvaluation(EvaluationProfile Profile, ProjectionRegistry Registry);` after the class. Make these replacements in section 2:
  - The `WorkspaceSnapshot gains ...` bullet becomes: "`WorkspaceSnapshot` gains `BoundEvaluation? Evaluation`, cleared on `Dispose`."
  - The `LanguageEntry gains ...` bullet becomes: "`LanguageEntry` gains an optional `BoundEvaluation? Evaluation` …", with the rest of that bullet unchanged.
  - Add "Both diagnostics are warnings: the language still works without hints." to the `NGR0002`/`NGR0003` bullets.

  In section 3, step 3: replace "**Handler exception:** caught and treated as one projection diagnostic …" with "**Handler exception:** `HirProjector` reports it as `NP0005`, shown like any projection diagnostic; the service catches only exceptions from the profile's `Builtin` and `Format`. Later statements still evaluate." Also change "the statement node of `root.Origins[0].Node`" to "the nearest statement-kind ancestor of `root.Origins[0].Node`, or the node itself, before trailing whitespace".

- [ ] **Step 3: Full build and tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all tests pass. That's the previous count plus 4 + 1 + 4 + 7 + 3 new tests: `EvaluationProfileTests` 4, `DateCalcTests` 1, `WorkspaceEvaluationTests` 4, `ValueHintTests` 7, `InlayHintLspTests` 3 (one fact and a two-row theory).

- [ ] **Step 4: Try it in the editor (manual).** Build the CLI (`dotnet build Nitrogen.Cli -c Release`). Point the VS Code extension's `nitrogen.server.path` at `Nitrogen.Cli/bin/Release/net10.0/nitrogen` and open `examples/DateCalc` as the workspace folder. Open `sample.datecalc`: each statement line should end with `= …`. Type `2026-10-05 + 2026-10-06;` and check that every hint disappears and comes back when the line is fixed. Report what you saw. If it doesn't work, report that too, with the server log from the "Nitrogen" output channel.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/superpowers/specs/2026-10-07-inlay-hint-evaluation-design.md
git commit -m "Document inlay-hint evaluation

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
