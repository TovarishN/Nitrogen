# A `today` builtin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DateCalc gains `today`, the host's local date. Hints and hover read it from the server's injectable clock, the hint cache is keyed by date, and the LSP server refreshes hints at midnight.

**Architecture:**
- An `EvaluationContext(Now)` is passed to a profile's `Builtin` callback, and a profile declares `ReadsClock`.
- The language service owns a `TimeProvider Clock`. It keys clock-dependent hint caches by date, and tells the server whether any language reads the clock and when the day next changes.
- The server's read loop also waits on a delay to the next day change, armed before each read. It sends `workspace/inlayHint/refresh` when that delay fires.

**Tech Stack:** C# / .NET 10 (`TimeProvider`, `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-07-today-builtin-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `today-builtin`.

**Timing in the server test (Task 4).** `JsonRpcConnection` reads headers one byte at a time and never reads ahead. So `GrammarLoopTests.BlockingInput`'s `between` callback runs exactly when the server starts reading the first message of the second batch. Two rules make the refresh deterministic:
1. **Arm first:** the server creates the day-change delay *before* it starts that read.
2. **Check the delay first:** when both have completed, the delay wins. `Task.WhenAny` returns the first completed task in argument order, so the delay goes first.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.Runtime/Semantic/EvaluationProfile.cs` | `EvaluationContext`; second constructor; `ReadsClock` | 1 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` | `Clock`; context through projections; date-keyed cache; `ReadsClock`; `NextDayChange` | 1, 3 |
| `examples/DateCalc/DateCalcEvaluator.cs` | context in `Run`; `today` in the profile; `Run(source, today?)` | 1, 2 |
| `examples/DateCalc/DateCalcLanguage.cs` | `today` is a `Date` | 2 |
| `examples/DateCalc/DateCalc.ngr` | `today` builtin value | 2 |
| `examples/DateCalc/countdown.datecalc` | example | 5 |
| `Nitrogen.LanguageService/Lsp/LspServer.cs` | day-change refresh | 4 |
| `Nitrogen.Tests/ManualClock.cs` | test `TimeProvider` | 3 |
| `Nitrogen.Tests/LanguageService/GrammarLoopTests.cs` | `BlockingInput` becomes `internal` | 4 |
| tests | per task | 1–4 |

---

### Task 1: `EvaluationContext` and the second constructor

**Files:**
- Modify: `Nitrogen.Runtime/Semantic/EvaluationProfile.cs` (whole file below)
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` (`Project`, its callers, `Clock`)
- Modify: `examples/DateCalc/DateCalcEvaluator.cs` (`Run`, `Builtins`)
- Test: `Nitrogen.Tests/Semantic/EvaluationProfileTests.cs`

- [ ] **Step 1: Write the failing tests.** Add to `EvaluationProfileTests`:

```csharp
    [Fact]
    public void The_original_constructor_does_not_read_the_clock()
    {
        Assert.False(Profile(_ => []).ReadsClock);
    }

    [Fact]
    public void A_profile_can_declare_that_it_reads_the_clock()
    {
        var profile = new EvaluationProfile(new HashSet<int> { 7 }, _ => [],
            (_, context) => new ProjectedValue(SemanticTypes.Text, context.Today.ToString("yyyy-MM-dd")), value => (string)value.Value,
            readsClock: true);

        Assert.True(profile.ReadsClock);
        Assert.Equal(new DateOnly(2026, 10, 7),
            new EvaluationContext(new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.FromHours(3))).Today);
    }
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EvaluationProfileTests"`
Expected: build error `'EvaluationProfile' does not contain a definition for 'ReadsClock'`.

- [ ] **Step 3: Replace `Nitrogen.Runtime/Semantic/EvaluationProfile.cs`**

```csharp
using Nitrogen.Binding;

namespace Nitrogen.Semantic;

/// <summary>What an evaluation may read besides its source: the host's current time.</summary>
public sealed record EvaluationContext(DateTimeOffset Now)
{
    /// <summary>The local date of <see cref="Now"/>.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}

/// <summary>
/// How an editor shows the values of a language's statements: which syntax kinds are statements, the
/// host handlers that project their HIR, the values of builtin symbols, and how a value reads as text.
/// A workspace language's helper source exports one as a public static field or property; the
/// workspace binds it to the language's composed catalog.
/// </summary>
public sealed class EvaluationProfile
{
    readonly Func<SemanticCatalog, IEnumerable<ProjectionHandler>> _handlers;
    readonly Func<Symbol, EvaluationContext, ProjectedValue?> _builtins;
    readonly Func<ProjectedValue, string> _format;

    /// <summary>A profile whose values never depend on the clock.</summary>
    /// <param name="handlers">The handlers, built from the catalog the profile is bound to (so their signatures are the catalog's own).</param>
    /// <param name="builtins">The value of a builtin symbol, or null when it has none.</param>
    public EvaluationProfile(
        IReadOnlySet<int> statementKinds,
        Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
        Func<Symbol, ProjectedValue?> builtins,
        Func<ProjectedValue, string> format)
        : this(statementKinds, handlers, Ignoring(builtins ?? throw new ArgumentNullException(nameof(builtins))), format, readsClock: false)
    {
    }

    /// <param name="builtins">The value of a builtin symbol in a context, or null when it has none.</param>
    /// <param name="readsClock">Whether values may depend on the context's time, so a host must not reuse them across days.</param>
    public EvaluationProfile(
        IReadOnlySet<int> statementKinds,
        Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
        Func<Symbol, EvaluationContext, ProjectedValue?> builtins,
        Func<ProjectedValue, string> format,
        bool readsClock)
    {
        StatementKinds = statementKinds ?? throw new ArgumentNullException(nameof(statementKinds));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _builtins = builtins ?? throw new ArgumentNullException(nameof(builtins));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        ReadsClock = readsClock;
    }

    /// <summary>The syntax kinds whose nodes are lowered and shown, one value each.</summary>
    public IReadOnlySet<int> StatementKinds { get; }

    /// <summary>Whether values may depend on the context's time.</summary>
    public bool ReadsClock { get; }

    /// <summary>The profile with its handlers checked against <paramref name="catalog"/>; throws when a handler differs from it or repeats.</summary>
    public BoundEvaluation Bind(SemanticCatalog catalog) => new(this, new ProjectionRegistry(catalog, _handlers(catalog)));

    public ProjectedValue? Builtin(Symbol symbol, EvaluationContext context) => _builtins(symbol, context);

    public string Format(ProjectedValue value) => _format(value);

    static Func<Symbol, EvaluationContext, ProjectedValue?> Ignoring(Func<Symbol, ProjectedValue?> builtins) =>
        (symbol, _) => builtins(symbol);
}

/// <summary>A profile bound to one language's catalog: what an editor projects that language's statements with.</summary>
public sealed record BoundEvaluation(EvaluationProfile Profile, ProjectionRegistry Registry);
```

- [ ] **Step 4: Pass a context at the call sites.**

In `NitrogenLanguageService.Evaluation.cs`:

- Add after `EvaluationBudget`:

```csharp
    /// <summary>The clock evaluations read (<see cref="EvaluationContext.Now"/>); tests set it.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    EvaluationContext Context() => new(Clock.GetLocalNow());
```

- Change `Project`'s signature to `static (string? Value, string? Failure) Project(HirNode node, BoundEvaluation evaluation, EvaluationContext context)`, and inside it `evaluation.Profile.Builtin(s)` to `evaluation.Profile.Builtin(s, context)`.
- `Hint` becomes `static ValueHint Hint(DocumentPosition at, HirNode root, BoundEvaluation evaluation, EvaluationContext context)` and calls `Project(root, evaluation, context)`.
- `Evaluate` becomes `IReadOnlyList<ValueHint> Evaluate(Document document, BoundEvaluation evaluation, EvaluationContext context)` and calls `Hint(at, root, evaluation, context)`.
- In `ValueHints`, the cache-miss line passes `Evaluate(document, evaluation, Context())`.
- `HoverValue` loses `static` and calls `Project(node, evaluation, Context())`.

In `examples/DateCalc/DateCalcEvaluator.cs`:

- `Builtins` becomes:

```csharp
    /// <summary>The values of the builtins the root refers to, in <paramref name="context"/>.</summary>
    static Dictionary<Symbol, ProjectedValue> Builtins(HirNode root, EvaluationContext context) => HirTraversal.PreOrder(root).OfType<HirSymbolRef>()
        .Select(r => r.Symbol.Binding).Where(s => s.IsBuiltin).Distinct()
        .Select(s => (Symbol: s, Value: Profile.Builtin(s, context))).Where(p => p.Value is not null)
        .ToDictionary(p => p.Symbol, p => p.Value!);
```

- In `Run`, add `var context = new EvaluationContext(DateTimeOffset.Now);` before `var registry = …`, and pass `Builtins(root, context)`.

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EvaluationProfileTests|FullyQualifiedName~DateCalc|FullyQualifiedName~ValueHintTests|FullyQualifiedName~HoverValueTests|FullyQualifiedName~WorkspaceEvaluationTests"`
Expected: all pass. The workspace test's profile source uses the original constructor, which still compiles.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Runtime/Semantic/EvaluationProfile.cs Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs examples/DateCalc/DateCalcEvaluator.cs Nitrogen.Tests/Semantic/EvaluationProfileTests.cs
git commit -m "Pass the host's time to evaluation profiles

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `today` in DateCalc

**Files:**
- Modify: `examples/DateCalc/DateCalc.ngr` (`builtin value`)
- Modify: `examples/DateCalc/DateCalcLanguage.cs` (`Builtin`)
- Modify: `examples/DateCalc/DateCalcEvaluator.cs` (`Profile`, `Run`)
- Test: `Nitrogen.Tests/DateCalc/DateCalcTests.cs`

- [ ] **Step 1: Write the failing tests.** Add to `DateCalcTests`:

```csharp
    [Theory]
    [InlineData("today;", "2026-10-07 Wed")]
    [InlineData("(2026-12-25 - today) in days;", "79")]
    [InlineData("today + 6 weeks;", "2026-11-18 Wed")]
    public void Today_is_the_date_the_caller_gives(string source, string expected) =>
        Assert.Equal(expected, Assert.Single(DateCalcEvaluator.Run(source, new DateOnly(2026, 10, 7))).Text);

    [Fact]
    public void The_profile_reads_the_clock()
    {
        Assert.True(DateCalcEvaluator.Profile.ReadsClock);
    }
```

In `The_grammar_declares_every_function_and_constant_of_the_modules`, change the constants assertion's expected set to `MathModule.Constants.Select(c => c.Name).Append("today").ToHashSet()`.

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalcTests"`
Expected: a build error about `Run` taking no second argument.

- [ ] **Step 3: Implement.**

In `DateCalc.ngr`, change the builtin values line to `builtin value { pi e tau today }`.

In `DateCalcLanguage.cs`, replace `Builtin`:

```csharp
    /// <summary>The type of a built-in value: <c>today</c> is a date; the Math constants are numbers.</summary>
    public static SemanticType? Builtin(string name) =>
        name == "today" ? Date : MathModule.Constants.Any(c => c.Name == name) ? Number : null;
```

In `DateCalcEvaluator.cs`, replace `Profile`:

```csharp
    /// <summary>Lets and shown expressions, each projected to a number, date, duration or text; <c>today</c> is the context's date.</summary>
    public static readonly EvaluationProfile Profile = new(
        new HashSet<int> { DateCalcKinds.Let, DateCalcKinds.Show },
        Handlers,
        (symbol, context) => symbol.Name == "today"
            ? new ProjectedValue(DateCalcLanguage.Date, context.Today)
            : MathModule.Constants.Where(c => c.Name == symbol.Name)
                .Select(c => new ProjectedValue(DateCalcLanguage.Number, c.Value)).FirstOrDefault(),
        value => Show(value.Value),
        readsClock: true);
```

Change `Run`'s signature and summary:

```csharp
    /// <summary>The value of each statement, in order, or the diagnostics that stop it from running; <c>today</c> is <paramref name="today"/>, else the local date.</summary>
    public static IReadOnlyList<DateCalcLine> Run(string source, DateOnly? today = null)
```

And its context line:

```csharp
        var context = new EvaluationContext(today is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)) : DateTimeOffset.Now);
```

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalc|FullyQualifiedName~LoweredLanguageTests|FullyQualifiedName~ValueHintTests|FullyQualifiedName~HoverValueTests|FullyQualifiedName~EmbeddedStringTests"`
Expected: all pass. `LoweredLanguageTests` recompiles the changed grammar in the workspace.

- [ ] **Step 5: Commit**

```bash
git add examples/DateCalc/DateCalc.ngr examples/DateCalc/DateCalcLanguage.cs examples/DateCalc/DateCalcEvaluator.cs Nitrogen.Tests/DateCalc/DateCalcTests.cs
git commit -m "Add today to DateCalc

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: A test clock, and date-keyed hints

**Files:**
- Create: `Nitrogen.Tests/ManualClock.cs`
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` (cache key, `ReadsClock`, `NextDayChange`)
- Create: `Nitrogen.Tests/LanguageService/TodayValueTests.cs`

- [ ] **Step 1: Add the test clock.** Create `Nitrogen.Tests/ManualClock.cs`:

```csharp
namespace Nitrogen.Tests;

/// <summary>A clock a test moves by hand: a set local time in a fixed-offset zone, and timers that fire when it passes them.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    readonly List<ManualTimer> _timers = [];
    readonly TimeZoneInfo _zone = TimeZoneInfo.CreateCustomTimeZone("Manual", start.Offset, "Manual", "Manual");
    DateTimeOffset _now = start.ToUniversalTime();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_timers) return _now;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    /// <summary>Moves the clock on and fires, outside the lock, every timer it passed.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_timers)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>A one-shot timer (the period is ignored: <see cref="Task.Delay(TimeSpan, TimeProvider)"/> uses none).</summary>
    sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                if (dueTime > TimeSpan.Zero)
                {
                    Due = clock._now + dueTime;
                    clock._timers.Add(this);
                    return true;
                }
            }
            Fire();
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Write the failing tests.** Create `Nitrogen.Tests/LanguageService/TodayValueTests.cs`:

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>DateCalc's today in the editor: the service's clock, hints that follow the date, and when the day next changes.</summary>
public sealed class TodayValueTests : IDisposable
{
    static readonly DocumentRange Whole = new(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));
    static readonly DateTimeOffset Evening = new(2026, 10, 7, 22, 0, 0, TimeSpan.FromHours(3));

    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-today-").FullName;

    public TodayValueTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service(ManualClock clock)
    {
        var service = new NitrogenLanguageService(LspCommand.Registry()) { Clock = clock };
        service.ConfigureWorkspace(_root);
        return service;
    }

    [Fact]
    public void Hints_show_the_clock_date_and_follow_it_past_midnight()
    {
        var clock = new ManualClock(Evening);
        using var service = Service(clock);
        string uri = Uri("a.datecalc");
        service.Open(uri, 1, "today;\n(2026-12-25 - today) in days;");

        Assert.Equal(["= 2026-10-07 Wed", "= 79"], service.ValueHints(uri, Whole).Select(h => h.Label));

        clock.Advance(TimeSpan.FromHours(3)); // 01:00 the next day; the document is unchanged
        Assert.Equal(["= 2026-10-08 Thu", "= 78"], service.ValueHints(uri, Whole).Select(h => h.Label));
    }

    [Fact]
    public void Hovering_today_shows_its_date()
    {
        using var service = Service(new ManualClock(Evening));
        string uri = Uri("a.datecalc");
        const string text = "today + 1 days;";
        service.Open(uri, 1, text);

        Assert.Contains("\n\n= 2026-10-07 Wed", service.Hover(uri, new DocumentPosition(0, 2))!.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_reads_the_clock_when_a_served_language_does()
    {
        using var service = Service(new ManualClock(Evening));
        Assert.True(service.ReadsClock);

        using var plain = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        Assert.False(plain.ReadsClock);
    }

    [Fact]
    public void The_next_day_change_is_the_next_local_midnight()
    {
        using var service = Service(new ManualClock(Evening));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(3)), service.NextDayChange);
    }
}
```

- [ ] **Step 3: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TodayValueTests"`
Expected: build error `'NitrogenLanguageService' does not contain a definition for 'ReadsClock'`.

- [ ] **Step 4: Implement.** In `NitrogenLanguageService.Evaluation.cs`:

- Change the cache record to `sealed record HintCacheEntry(int DocumentVersion, int ProjectVersion, LanguageEntry Language, DateOnly? Day, IReadOnlyList<ValueHint> Hints);`.
- Replace the body of `ValueHints` after its host line with:

```csharp
        if (!_documents.TryGetValue(uri, out var document) || document.Language.Evaluation is not { } evaluation) return [];
        var project = _projects[document.Language];
        var context = Context();
        DateOnly? day = evaluation.Profile.ReadsClock ? context.Today : null;
        if (!_hints.TryGetValue(uri, out var hit) || hit.DocumentVersion != document.Version ||
            hit.ProjectVersion != project.Version || !ReferenceEquals(hit.Language, document.Language) || hit.Day != day)
        {
            hit = new HintCacheEntry(document.Version, project.Version, document.Language, day, Evaluate(document, evaluation, context));
            _hints[uri] = hit;
        }
        return hit.Hints.Where(h => Within(h.At, range)).ToList();
```

- Add after `Context()`:

```csharp
    /// <summary>Whether a served language's values depend on the clock: the LSP server then refreshes hints when the day changes.</summary>
    public bool ReadsClock => Registry.Entries.Any(entry => entry.Evaluation?.Profile.ReadsClock == true);

    /// <summary>The next local midnight by <see cref="Clock"/>.</summary>
    public DateTimeOffset NextDayChange
    {
        get
        {
            var midnight = Clock.GetLocalNow().Date.AddDays(1);
            return new DateTimeOffset(midnight, Clock.LocalTimeZone.GetUtcOffset(midnight));
        }
    }
```

- Update the class summary's last sentence to: "Nothing is shown while the document has an error; evaluation stops starting statements once its budget is spent; and a language whose values read the clock has its hints recomputed on a new day."

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TodayValueTests|FullyQualifiedName~ValueHintTests|FullyQualifiedName~HoverValueTests|FullyQualifiedName~EmbeddedStringTests"`
Expected: all pass. If `Hovering_today_shows_its_date` gets no value line, print `service.Inspect(uri, new DocumentPosition(0, 2))!.Node`. A node that isn't a `HirSymbolRef` means `today` lowers differently from `pi`; stop and report.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Tests/ManualClock.cs Nitrogen.Tests/LanguageService/TodayValueTests.cs Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs
git commit -m "Key clock-dependent hints by date

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Refresh hints at midnight

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs` (`RunAsync`, `RefreshInlayHintsAsync`, new helpers)
- Modify: `Nitrogen.Tests/LanguageService/GrammarLoopTests.cs` (`BlockingInput` visibility)
- Test: `Nitrogen.Tests/LanguageService/TodayValueTests.cs`

- [ ] **Step 1: Make `BlockingInput` reusable.** In `GrammarLoopTests.cs`, change `sealed class BlockingInput(` to `internal sealed class BlockingInput(`.

- [ ] **Step 2: Write the failing test.** Add to `TodayValueTests` (and add `using System.Text.Json;` and `using Nitrogen.LanguageService.Lsp;` at the top):

```csharp
    [Theory]
    [InlineData("""{"workspace":{"inlayHint":{"refreshSupport":true}}}""", 1)]
    [InlineData("{}", 0)]
    public async Task The_server_refreshes_hints_once_when_the_day_changes(string capabilities, int refreshes)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 7, 23, 0, 0, TimeSpan.FromHours(3)));
        using var service = new NitrogenLanguageService(LspCommand.Registry()) { Clock = clock };
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri
            + "\",\"capabilities\":" + capabilities + "}}";
        var input = new GrammarLoopTests.BlockingInput(
            [initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}"""],
            () => clock.Advance(TimeSpan.FromHours(2)), // past midnight, while the server waits for the next message
            ["""{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}"""]);
        var output = new MemoryStream();

        int code = await new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null).RunAsync(CancellationToken.None);

        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        Assert.Equal(0, code);
        Assert.Equal(refreshes, messages.Count(m => m.TryGetProperty("method", out var method) && method.GetString() == "workspace/inlayHint/refresh"));
        Assert.Contains(messages, m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 99);
    }
```

- [ ] **Step 3: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TodayValueTests"`
Expected: the `refreshes: 1` row fails (0 refreshes); the `{}` row passes.

- [ ] **Step 4: Implement.** In `LspServer.cs`:

Replace the start of `RunAsync`'s loop, from `while (true)` through `if (message is null) return 1;`, with:

```csharp
        Task<JsonDocument?>? reading = null;
        Task? dayChange = null;
        while (true)
        {
            JsonDocument? message;
            try
            {
                // Armed before the read starts, and checked first, so a day change during the wait is never missed.
                dayChange ??= DayChange(cancel);
                reading ??= connection.ReadAsync(cancel);
                if (dayChange is not null && await Task.WhenAny(dayChange, reading) == dayChange && dayChange.IsCompletedSuccessfully)
                {
                    dayChange = null;
                    await SendInlayHintRefreshAsync(cancel);
                    continue;
                }
                message = await reading;
                reading = null;
            }
            catch (InvalidDataException error)
            {
                log.WriteLine($"nitrogen lsp: {error.Message}");
                return 1;
            }
            if (message is null) return 1;
```

Replace `RefreshInlayHintsAsync` with:

```csharp
    /// <summary>Asks the client to request inlay hints again once a language was recompiled, if it can.</summary>
    Task RefreshInlayHintsAsync(CancellationToken cancel)
    {
        if (!_refreshInlayHints || service.LanguagesVersion == _languagesSeen) return Task.CompletedTask;
        _languagesSeen = service.LanguagesVersion;
        return SendInlayHintRefreshAsync(cancel);
    }

    /// <summary>The wait for the next local midnight, when the client takes refreshes and a served language reads the clock; null otherwise.</summary>
    Task? DayChange(CancellationToken cancel)
    {
        if (!_refreshInlayHints || !service.ReadsClock) return null;
        var wait = service.NextDayChange - service.Clock.GetUtcNow();
        return Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, service.Clock, cancel);
    }

    /// <summary>Asks the client to request inlay hints again (<c>workspace/inlayHint/refresh</c>).</summary>
    Task SendInlayHintRefreshAsync(CancellationToken cancel)
    {
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

Add a sentence to the class summary: "When a served language's values read the clock, hints are refreshed at each local midnight."

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~TodayValueTests|FullyQualifiedName~InlayHintLspTests|FullyQualifiedName~LspServerTests|FullyQualifiedName~GrammarLoopTests|FullyQualifiedName~WorkspaceIndexTests"`
Expected: all pass. Run `TodayValueTests` five more times (`for i in 1 2 3 4 5; do …; done`) and confirm it never fails. That rules out a timing race; if it fails even once, stop and report.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.Tests/LanguageService/GrammarLoopTests.cs Nitrogen.Tests/LanguageService/TodayValueTests.cs
git commit -m "Refresh clock-dependent hints at midnight

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Example, spec, full check

**Files:**
- Create: `examples/DateCalc/countdown.datecalc`
- Modify: `docs/superpowers/specs/2026-10-07-today-builtin-design.md` (status)

- [ ] **Step 1: Add the example.** Create `examples/DateCalc/countdown.datecalc`:

```
// Countdowns from today: the editor shows each value, and updates them at midnight.
let christmas = 2026-12-25;
(christmas - today) in days;
weekday(christmas);
today + 6 weeks;
```

- [ ] **Step 2: Spec status.** Change the spec's status line to `Status: implemented (YYYY-MM-DD). Builds on`, using the date of this step.

- [ ] **Step 3: Full build and tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass: the previous 1,058 plus 12 new (2 `EvaluationProfileTests`, 4 `DateCalcTests` cases, 6 `TodayValueTests` cases).

- [ ] **Step 4: Probe the built server on the example.** Build the CLI (`dotnet build Nitrogen.Cli -c Release`). Ask the server for `countdown.datecalc`'s inlay hints with the stdio probe used for earlier features: initialize with `examples/DateCalc` as the root, open the file, request `textDocument/inlayHint` for the whole file, and print each line with its label. Expected: four values, with `(christmas - today) in days` matching `(2026-12-25 - <today's local date>)` in days. Report the output.

- [ ] **Step 5: Commit**

```bash
git add examples/DateCalc/countdown.datecalc docs/superpowers/specs/2026-10-07-today-builtin-design.md
git commit -m "Add a countdown example for today

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
