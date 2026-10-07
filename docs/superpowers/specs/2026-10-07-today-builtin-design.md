# A `today` builtin: the first clock-dependent value

Status: implemented (2026-10-07). Builds on
[inlay-hint evaluation](2026-10-07-inlay-hint-evaluation-design.md) and
[values on hover](2026-10-07-hover-values-design.md).

## Problem

DateCalc can't say "now". Countdowns (`(2026-12-25 - today) in days`), ages and "6 weeks from
today" all need the current date. Every value Nitrogen evaluates today is pure: the same source always
gives the same value. So the hint cache is keyed only by document and project versions, and nothing
in Nitrogen reads the clock.

The module admission gate allows only pure capabilities (`CapabilityEffect { Pure }`), but it
isn't involved here: the language service and the CLI compile a workspace language's helper sources
directly, without admission. What this feature needs is narrower. The server must know which values
depend on the clock, so it never serves yesterday's values.

## Goal

DateCalc gains a `today` builtin of type `DateCalc.Date`. Its value is the host's current local date:
- in inlay hints and hover, the language server's clock;
- in `DateCalcEvaluator.Run`, the caller's date or the system's.

An editor left open past midnight updates by itself. Tests set the time.

Out of scope: a `now` with time of day, time zones beyond the host's local date, and extending
admission to effectful capabilities.

## 1. Runtime: the evaluation context

In `Nitrogen.Runtime/Semantic/EvaluationProfile.cs`:

```csharp
/// <summary>What an evaluation may read besides its source: the host's current time.</summary>
public sealed record EvaluationContext(DateTimeOffset Now)
{
    /// <summary>The local date of <see cref="Now"/>.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}
```

`EvaluationProfile` gains a second constructor:

```csharp
public EvaluationProfile(
    IReadOnlySet<int> statementKinds,
    Func<SemanticCatalog, IEnumerable<ProjectionHandler>> handlers,
    Func<Symbol, EvaluationContext, ProjectedValue?> builtins,
    Func<ProjectedValue, string> format,
    bool readsClock)
```

- The existing constructor stays, so the API published in 0.6.0 doesn't break. It forwards with
  `(symbol, _) => builtins(symbol)` and `readsClock: false`.
- `Builtin(Symbol)` becomes `Builtin(Symbol symbol, EvaluationContext context)`.
- New property: `bool ReadsClock`, true when the profile's values may depend on `context.Now`.

## 2. Service

- `NitrogenLanguageService` gets `TimeProvider Clock { get; set; }`, which defaults to
  `TimeProvider.System`. Tests set it.
- Every projection, for hints and hover, passes `new EvaluationContext(Clock.GetLocalNow())`.
- **Hint cache:** when the profile reads the clock, the key also includes the context's `Today`, so
  the first request on a new day recomputes. Hover isn't cached and is always current.
- New members for the server:
  - `bool ReadsClock`: true when any registered language's evaluation reads the clock;
  - `DateTimeOffset NextDayChange`: the next local midnight after `Clock.GetLocalNow()`.

## 3. LSP server: refresh at the day change

`RunAsync` reads one message at a time. When the client supports `workspace/inlayHint/refresh` and
`service.ReadsClock` is true, the loop waits on two things: the pending read, and
`Task.Delay(NextDayChange - now, service.Clock)`.

- **If the delay finishes first:** the server sends `workspace/inlayHint/refresh`, then waits again on
  the same pending read and a new delay to the next day change. No message is lost, and every write
  still comes from the loop.
- **Otherwise:** the loop is unchanged.
- **No delay** when the client lacks refresh support or no language reads the clock.

## 4. DateCalc

- **Grammar:** `builtin value { pi e tau today }`.
- **Type:** `DateCalcLanguage.Builtin("today")` returns `Date`; the Math constants are numbers as before.
- **Profile:** `DateCalcEvaluator.Profile` uses the new constructor with `readsClock: true`. Its
  callback returns `new ProjectedValue(Date, context.Today)` for `today`, and the Math constants as before.
- **`Run`:** becomes `Run(string source, DateOnly? today = null)`. It evaluates with `today`, or the
  system's local date, at midnight local time.
- **Example:** a new `examples/DateCalc/countdown.datecalc` shows `today` in use. `sample.datecalc` stays
  unchanged, because the tests assert its exact values.
- `The_grammar_declares_every_function_and_constant_of_the_modules` accounts for `today` as a builtin
  value that isn't a Math constant.

## 5. Tests

Time is fixed with a small hand-written `TimeProvider` in the test project: a set local time and
offset, and a timer that fires when the test advances the time. No new package is needed.

- `Run("today;", new DateOnly(2026, 10, 7))` → `2026-10-07 Wed`; `(2026-12-25 - today) in days`
  from that date → `79`.
- **Service:**
  - hints for `today;` at a set clock show that date;
  - advancing the clock past midnight changes the hint on the next request (cache keyed by date);
  - hovering `today` shows its date;
  - `ReadsClock` is true with DateCalc and false for a language without a clock-reading profile;
  - `NextDayChange` is the next local midnight.
- **LSP:**
  - with refresh support, advancing the server's clock past midnight sends exactly one
    `workspace/inlayHint/refresh`;
  - none for a client without refresh support;
  - messages sent before and after the day change are all handled.
- **`EvaluationProfile`:** the old constructor gives `ReadsClock == false` and ignores the context.
- Existing tests pass unchanged, except the grammar-builtins test noted above.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
