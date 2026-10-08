# Signature help

Status: approved design (2026-10-08). Part of the goal of first-class language support.

## Problem

Typing a call gets no parameter hints. `round(2.5, ` doesn't show that `round` takes `(x)` or
`(x, digits)`, and `min(2026-10-05, ` doesn't show that its date overload expects a second date. The
language server implements no `textDocument/signatureHelp`, and nothing tells it which signatures a
name has:

- **DateCalc's built-in functions:** the grammar only declares their names (`builtin function { … }`),
  and C# picks the overload by name and argument types. The catalog's operation signatures carry
  types only, no parameter names, and the server can't see which operations a name can select.
- **Templates (Geometry's `def`/`make`):** the parameters' names and types are in the source.
  Nothing in the editor uses them yet.

## Goal

When the cursor is inside the argument list of a `name(…)` call, the editor shows the callee's
overloads, with the current one and the current parameter highlighted. This works in language files
and in tagged C# strings (VS Code). It covers two sources:
- a language's built-in callables, declared in C# through a new hook;
- templates, read from their declarations with no C#.

Out of scope:
- **Rider's C# strings client:** it keeps signature help off, like quick fixes; 4a turns both on.
- **Calls written without `name(…)`,** such as Geometry's `box w h d`.
- **Grammar syntax:** declaring signatures in `.ngr`.

## 1. Runtime: `CallSignatures`

In `Nitrogen.Runtime/Semantic/CallSignatures.cs`:

```csharp
public sealed record CallParameter(string Name, SemanticType Type);

/// <summary>One overload of a callable name: its parameters, its result, and an optional one-line summary.</summary>
public sealed record CallSignature(IReadOnlyList<CallParameter> Parameters, SemanticType Result, string? Summary = null);

/// <summary>
/// A language's callable names and their overloads, for the editor's signature help. A workspace
/// language's helper source exports one as a public static field or property.
/// </summary>
public sealed class CallSignatures(IReadOnlyDictionary<string, IReadOnlyList<CallSignature>> byName)
{
    public IReadOnlyList<CallSignature> For(string name);   // empty for an unknown name
}
```

## 2. Discovery

- `GrammarWorkspace.Compile` finds at most one public static `CallSignatures`, with the same scan as
  `EvaluationProfile` and `DiagnosticFixes`.
- Two or more are warning `NGR0005`, and none is used.
- `WorkspaceSnapshot.Calls` and `LanguageEntry.Calls` carry it.

## 3. Service: `SignatureHelp(uri, position)`

```csharp
public sealed record ServiceSignature(string Label, IReadOnlyList<(int Start, int End)> Parameters, string? Summary);
public sealed record ServiceSignatureHelp(IReadOnlyList<ServiceSignature> Signatures, int ActiveSignature, int ActiveParameter);

public ServiceSignatureHelp? SignatureHelp(string uri, DocumentPosition position);
```

1. **The call.** In the document's syntax tree, take the leaf tokens before the cursor and walk back
   to the innermost `(` that isn't closed before the cursor. The token just before it must be a name;
   that is the callee. The active parameter is the number of `,` tokens after that `(` and before the
   cursor, at the same nesting depth (parentheses inside nested calls don't count). With no such `(`,
   or no name before it, there's no help.
2. **Its signatures.**
   - **Hook:** the language's `CallSignatures.For(name)`.
   - **Templates:** otherwise, if the callee resolves to a symbol whose declaring rule lowers a
     template (`lowers template Body(Params)`), one signature comes from its parameters: each
     parameter's declared name and type, in order. The result is the template body's type.
   - **None:** with no signatures, there's no help.
3. **The label** is `name(p1: T1, p2: T2) → R`, with types written as the catalog writes them
   (`Core.Scalar`, `DateCalc.Date`). Each parameter's range in the label is reported, so editors bold
   the active one.
4. **The active signature** is the first overload with more parameters than the active parameter.
   Among those, an overload whose types match the arguments already complete is preferred: each
   complete argument that lowers to a type must have its parameter's type. With none left, the first
   overload is active.
5. **Tagged C# strings:** for a C# host, the request goes to the tagged string under the position, as
   hover does.

## 4. LSP

- `ServerCapabilities` advertises `signatureHelpProvider: { triggerCharacters: ["(", ","] }`.
- `textDocument/signatureHelp` returns `SignatureHelp`: `signatures` (each with `label`,
  `parameters` as `[start, end]` label offsets, and optional `documentation`), `activeSignature` and
  `activeParameter`. It returns `null` when there's no help.

## 5. Editors

- **VS Code:** no change.
- **Rider:** language files use the platform's default. The C# client keeps `LspSignatureHelpDisabled`.
- `docs/editor-support.md`'s feature matrix gains a *Signature help* row: ✓ in language files, ✓ in
  VS Code C# strings, — in Rider C# strings.

## 6. DateCalc

- **Parameter names:** `Function` gains parameter names:
  - unary math functions use `x`;
  - `round(x, digits)`, `pow(base, exponent)`, `log(x, base)`, `atan2(y, x)`, `min(a, b)`,
    `max(a, b)` and `mod(x, y)`;
  - DateCalc's `weekday(date)`, `abs(duration)`, and `min(a, b)` / `max(a, b)` for dates and durations.
- **Hook:** `DateCalcLanguage.Calls` (a `CallSignatures`) groups `AllFunctions` by name. Each
  overload's parameters pair those names with the operation's input types, and its result is the
  operation's result.

## 7. Tests

- **Call finding:** the cursor right after `(`, after a `,`, inside a nested call
  (`round(min(1, 2), ` → `round`, parameter 1), and on an unfinished last line.
- **DateCalc:**
  - `round(2.5, ` → two overloads, `round(x: Core.Scalar, digits: Core.Scalar) → Core.Scalar`
    active, parameter `digits`;
  - `min(2026-10-05, ` → the date overload active;
  - `round(` → the one-parameter overload active.
- **Templates:** Geometry's `make` of a `def` with three `Scalar` parameters → one signature with
  those names, at the right parameter.
- **No help:** outside a call, after the closing `)`, and in a language without signatures.
- **Discovery:** one hook is found; two give `NGR0005`.
- **LSP:** the capability, and a `textDocument/signatureHelp` round trip.
- **Tagged C# string:** `round(2.5, ` inside one.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
