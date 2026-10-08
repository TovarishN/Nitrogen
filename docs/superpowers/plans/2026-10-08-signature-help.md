# Signature help Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inside a `name(…)` call, the editor shows the callee's overloads with the active one and the active parameter highlighted. Signatures come from a language's `CallSignatures` hook (DateCalc's functions) or from a template's declaration (Geometry's `def`/`make`).

**Architecture:**
- A runtime `CallSignatures` type is discovered in helper sources like `EvaluationProfile`.
- `DeclarativeTypes.TemplateSignature` reads a template's parameters and result from its declaration.
- The service finds the call with a text scan that treats real non-punctuation tokens as opaque.
- `LspServer` answers `textDocument/signatureHelp`.

**Tech Stack:** C# / .NET 10, xUnit, LSP 3.17 signature help.

**Spec:** `docs/superpowers/specs/2026-10-08-signature-help-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `signature-help`.

**Findings from a probe while planning** (a throwaway test, since deleted):
1. **Commas vanish in unfinished calls.** In `round(2.5, `, error recovery skips the `,`. The syntax tree's leaves are `round`, `(`, `2.5` and empty inserted tokens, so a tree-only walk would miscount the active parameter while typing. The plan scans the **text** back from the cursor instead. Characters inside real leaf tokens other than `(`, `)` and `,` (names, numbers, strings) are opaque. Everything else (whitespace, skipped input, punctuation) is read as text.
   - **Statement boundaries:** the scan stops with no help at a `;`, `{` or `}` at nesting depth 0, so a broken earlier call doesn't leak into later statements.
   - **Remaining risk:** a comment containing `(` or `,` inside an unfinished call can confuse the count. Task 6 records this in the spec.
2. **Built-ins don't resolve in unfinished calls.** In `round(2.5, `, hover and go to definition on `round` find nothing. The hook is therefore looked up by the callee's text.
3. **Template callees do resolve.** In `make slab(1, `, `slab` has hover and go to definition, so `NameAt` finds the template's symbol even in unfinished code. Its parameters' types are known: hover on `w` shows `Core.Scalar`.
4. **Existing helpers:** `DeclarativeTypes` has `TypeOf(int node)` and `TypeOfSymbol(Symbol)`, and a private `ParameterAt(int item)` and `SequenceItems(int list, int stride)`. `FileSemantics.RelatedFile(path)` gives another file's semantics.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.Runtime/Semantic/CallSignatures.cs` | `CallParameter`, `CallSignature`, `CallSignatures` | 1 |
| `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` | `TemplateSignature(Symbol)` | 1 |
| `Nitrogen.Workspace/…`, `Nitrogen.LanguageService/LanguageRegistry.cs`, `GrammarLanguages.cs` | discover and carry `CallSignatures`; `NGR0005` | 2 |
| `examples/DateCalc/MathModule.cs`, `DateCalcLanguage.cs` | parameter names; `Calls` | 3 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Signatures.cs`, `ServiceTypes.cs` | `SignatureHelp` | 4 |
| `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `LspServer.cs` | `textDocument/signatureHelp` | 5 |
| `docs/editor-support.md`, the spec | matrix row; status | 6 |

---

### Task 1: Runtime types and template signatures

**Files:**
- Create: `Nitrogen.Runtime/Semantic/CallSignatures.cs`
- Modify: `Nitrogen.Runtime/Semantic/DeclarativeTypes.cs` (new public method next to `TemplateOf`)
- Test: `Nitrogen.Tests/Semantic/CallSignaturesTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public class CallSignaturesTests
{
    [Fact]
    public void Overloads_are_found_by_name()
    {
        var round = new CallSignature([new CallParameter("x", SemanticTypes.Scalar)], SemanticTypes.Scalar);
        var calls = new CallSignatures(new Dictionary<string, IReadOnlyList<CallSignature>> { ["round"] = [round] });

        Assert.Same(round, Assert.Single(calls.For("round")));
        Assert.Empty(calls.For("floor"));
    }
}
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~CallSignaturesTests"`
Expected: build error, `CallSignature` not found.

- [ ] **Step 3: Implement the types.** Create `Nitrogen.Runtime/Semantic/CallSignatures.cs`:

```csharp
namespace Nitrogen.Semantic;

public sealed record CallParameter(string Name, SemanticType Type);

/// <summary>One overload of a callable name: its parameters, its result, and an optional one-line summary.</summary>
public sealed record CallSignature(IReadOnlyList<CallParameter> Parameters, SemanticType Result, string? Summary = null);

/// <summary>
/// A language's callable names and their overloads, for the editor's signature help. A workspace
/// language's helper source exports one as a public static field or property.
/// </summary>
public sealed class CallSignatures(IReadOnlyDictionary<string, IReadOnlyList<CallSignature>> byName)
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<CallSignature>> _byName = byName ?? throw new ArgumentNullException(nameof(byName));

    /// <summary>The overloads of <paramref name="name"/>, in the order given; empty for an unknown name.</summary>
    public IReadOnlyList<CallSignature> For(string name) => _byName.TryGetValue(name, out var overloads) ? overloads : [];
}
```

- [ ] **Step 4: Add `TemplateSignature`.** In `DeclarativeTypes.cs`, add after `TemplateOf`:

```csharp
    /// <summary>
    /// The signature of the template <paramref name="symbol"/> declares, read from its declaration: each
    /// parameter's name and declared type, in order, and the type of its body. Null when the symbol's
    /// declaring rule doesn't lower a template.
    /// </summary>
    public (IReadOnlyList<(string Name, SemanticType? Type)> Parameters, SemanticType? Result)? TemplateSignature(Symbol symbol)
    {
        if (symbol is not { IsBuiltin: false, Path: { } path }) return null;
        var types = path == _file.Path ? this : _file.RelatedFile(path).DeclarativeTypes;
        var tree = types._file.Tree;
        if (types._lowering.RuleFor(tree.Kind(symbol.Node)) is not { Rule: { Form: DeclarativeForm.Template } rule }) return null;
        var parameters = types.SequenceItems(tree.Child(symbol.Node, rule.Arguments[1]), rule.SequenceStride)
            .Select(types.ParameterAt).OfType<Symbol>()
            .Select(parameter => (parameter.Name, types.TypeOfSymbol(parameter))).ToArray();
        return (parameters, types.TypeOf(tree.Child(symbol.Node, rule.Arguments[0])));
    }
```

If `Symbol` lacks `IsBuiltin` or `Path` with these names, follow `TemplateOf`'s own pattern a few lines above; it uses the same properties.

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~CallSignaturesTests|FullyQualifiedName~Template"`
Expected: all pass. `TemplateSignature` is exercised by Task 4's Geometry test.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Runtime/Semantic/CallSignatures.cs Nitrogen.Runtime/Semantic/DeclarativeTypes.cs Nitrogen.Tests/Semantic/CallSignaturesTests.cs
git commit -m "Add call signatures, and read a template's signature from its declaration

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Discovery

**Files:**
- Modify: `Nitrogen.Workspace/WorkspaceSnapshot.cs`, `Nitrogen.Workspace/GrammarWorkspace.cs`
- Modify: `Nitrogen.LanguageService/LanguageRegistry.cs`, `Nitrogen.LanguageService/GrammarLanguages.cs`
- Test: `Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs`

- [ ] **Step 1: Write the failing tests.** Add to `WorkspaceEvaluationTests`:

```csharp
    static string CallsSource(string type) => $$"""
        using Nitrogen.Semantic;

        public static class {{type}}
        {
            public static CallSignatures Calls { get; } = new(new Dictionary<string, IReadOnlyList<CallSignature>>());
        }
        """;

    [Fact]
    public void One_call_signature_provider_is_discovered()
    {
        using var snapshot = Compile(CallsSource("SumCalls"));
        Assert.NotNull(snapshot.Calls);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void Two_call_signature_providers_are_a_warning_and_neither_is_used()
    {
        using var snapshot = Compile(CallsSource("FirstCalls"), CallsSource("SecondCalls"));
        Assert.Null(snapshot.Calls);
        var warning = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("NGR0005", warning.Code);
        Assert.False(warning.IsError);
    }
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceEvaluationTests"`
Expected: build error, `WorkspaceSnapshot` has no `Calls`.

- [ ] **Step 3: Implement**, following the `Fixes` pattern from #30 exactly:
  - **`WorkspaceSnapshot`:**
    - a constructor parameter `CallSignatures? calls = null` after `fixes`, with `Calls = calls;`;
    - the property `/// <summary>The helper sources' call signatures; null when there are none.</summary> public CallSignatures? Calls { get; private set; }`;
    - `Calls = null;` in `Dispose`.
  - **`GrammarWorkspace`:**
    - in `Compile`, add `var calls = Calls(types, diagnostics);` after `var fixes = …`, and pass `calls` as the snapshot's last argument;
    - add a method after `Fixes(...)`:

```csharp
    /// <summary>The one public static <see cref="CallSignatures"/> of <paramref name="types"/>; two are a warning (NGR0005), and the language has none.</summary>
    static CallSignatures? Calls(IEnumerable<Type> types, List<WorkspaceDiagnostic> diagnostics)
    {
        var found = StaticValues(types, t => t == typeof(CallSignatures)).OfType<CallSignatures>().Distinct().ToList();
        if (found.Count <= 1) return found.FirstOrDefault();
        diagnostics.Add(new WorkspaceDiagnostic("", 0, 0, "NGR0005",
            $"{found.Count} call signature providers; a language has at most one, so none is used", IsError: false));
        return null;
    }
```

  - **`LanguageEntry`:**
    - the doc line `/// <param name="calls">Its callable names' signatures; none when null.</param>`;
    - a parameter `CallSignatures? calls = null` after `fixes`;
    - `public CallSignatures? Calls { get; } = calls;`.
  - **`GrammarLanguages.Compile`:** the entry's last argument becomes `evaluation: snapshot.Evaluation, fixes: snapshot.Fixes, calls: snapshot.Calls);`.

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~WorkspaceEvaluationTests|FullyQualifiedName~LoweredLanguageTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Workspace Nitrogen.LanguageService/LanguageRegistry.cs Nitrogen.LanguageService/GrammarLanguages.cs Nitrogen.Tests/Workspace/WorkspaceEvaluationTests.cs
git commit -m "Find a workspace language's call signatures

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: DateCalc's signatures

**Files:**
- Modify: `examples/DateCalc/MathModule.cs` (`Function`, `Unary`, `Binary`, the `Binary` calls)
- Modify: `examples/DateCalc/DateCalcLanguage.cs` (`Functions`, new `Calls`)
- Test: `Nitrogen.Tests/DateCalc/DateCalcTests.cs`

- [ ] **Step 1: Write the failing test.** Add to `DateCalcTests`:

```csharp
    [Fact]
    public void Every_function_has_named_parameters_in_its_signatures()
    {
        Assert.Equal(["x", "digits"], DateCalcLanguage.Calls.For("round")[1].Parameters.Select(p => p.Name));
        Assert.Equal(["base", "exponent"], Assert.Single(DateCalcLanguage.Calls.For("pow")).Parameters.Select(p => p.Name));
        Assert.Equal(["date"], Assert.Single(DateCalcLanguage.Calls.For("weekday")).Parameters.Select(p => p.Name));
        Assert.Equal(3, DateCalcLanguage.Calls.For("min").Count);
        Assert.All(DateCalcLanguage.AllFunctions, f => Assert.Equal(f.Signature.Inputs.Count, f.Parameters.Count));
    }
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalcTests"`
Expected: build error, no `DateCalcLanguage.Calls` or `Function.Parameters`.

- [ ] **Step 3: Implement.** In `MathModule.cs`:
  - Change the record to `public sealed record Function(string Name, OperationSignature Signature, Func<object[], object> Run, IReadOnlyList<string> Parameters);` and update its summary to "…its name in source, the operation it lowers to, its parameters' names, and what it computes."
  - `Unary` becomes `new(name, new(…), a => run((float)a[0]), ["x"])`.
  - `Binary` gains a `string first, string second` pair after `id`, passing `[first, second]`. The calls become:
    - `Binary("round", "RoundTo", "x", "digits", …)`
    - `Binary("pow", "Pow", "base", "exponent", MathF.Pow)`
    - `Binary("log", "LogBase", "x", "base", MathF.Log)`
    - `Binary("atan2", "Atan2", "y", "x", MathF.Atan2)`
    - `Binary("min", "Min", "a", "b", MathF.Min)`
    - `Binary("max", "Max", "a", "b", MathF.Max)`
    - `Binary("mod", "Mod", "x", "y", …)`

  In `DateCalcLanguage.cs`:
  - Add parameter names to `Functions`: `weekday` gets `["date"]`, `abs` gets `["duration"]`, and the four `min`/`max` overloads get `["a", "b"]`.
  - Add after `Semantics`:

```csharp
    /// <summary>Every function's overloads with named parameters, for the editor's signature help.</summary>
    public static readonly CallSignatures Calls = new(AllFunctions.GroupBy(f => f.Name).ToDictionary(group => group.Key,
        group => (IReadOnlyList<CallSignature>)group.Select(f => new CallSignature(
            f.Signature.Inputs.Select((type, i) => new CallParameter(f.Parameters[i], type)).ToArray(), f.Signature.Result)).ToArray()));
```

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~DateCalc|FullyQualifiedName~LoweredLanguageTests|FullyQualifiedName~QuickFixTests"`
Expected: all pass. The workspace compiles the changed sources and discovers `Calls`.

- [ ] **Step 5: Commit**

```bash
git add examples/DateCalc/MathModule.cs examples/DateCalc/DateCalcLanguage.cs Nitrogen.Tests/DateCalc/DateCalcTests.cs
git commit -m "Name DateCalc's function parameters and export their signatures

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `SignatureHelp` in the service

**Files:**
- Modify: `Nitrogen.LanguageService/ServiceTypes.cs`
- Create: `Nitrogen.LanguageService/NitrogenLanguageService.Signatures.cs`
- Create: `Nitrogen.Tests/LanguageService/SignatureHelpTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Signature help: the overloads of the call around the cursor, from a language's hook or a template's declaration.</summary>
public sealed class SignatureHelpTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-signatures-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(string folder)
    {
        string root = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, folder)))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(root);
        return service;
    }

    /// <summary>Signature help with the cursor at the end of <paramref name="text"/>.</summary>
    ServiceSignatureHelp? HelpAtEnd(NitrogenLanguageService service, string folder, string name, string text)
    {
        string uri = new Uri(Path.Combine(_root, folder, name)).AbsoluteUri;
        service.Open(uri, 1, text);
        return service.SignatureHelp(uri, new LineMap(text).PositionOf(text.Length));
    }

    static string Active(ServiceSignatureHelp help) => help.Signatures[help.ActiveSignature].Label;

    static string ActiveParameter(ServiceSignatureHelp help)
    {
        var signature = help.Signatures[help.ActiveSignature];
        var (start, end) = signature.Parameters[help.ActiveParameter];
        return signature.Label[start..end];
    }

    [Fact]
    public void A_second_argument_highlights_the_two_parameter_overload()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "round(2.5, ")!;

        Assert.Equal(2, help.Signatures.Count);
        Assert.Equal("round(x: Core.Scalar, digits: Core.Scalar) → Core.Scalar", Active(help));
        Assert.Equal("digits: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void An_open_call_highlights_the_first_overload_that_fits()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "round(")!;

        Assert.Equal("round(x: Core.Scalar) → Core.Scalar", Active(help));
        Assert.Equal("x: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void The_types_already_typed_choose_the_overload()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "min(2026-10-05, ")!;

        Assert.Equal("min(a: DateCalc.Date, b: DateCalc.Date) → DateCalc.Date", Active(help));
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public void A_nested_call_belongs_to_its_own_arguments()
    {
        using var service = Service("DateCalcLanguage");
        var help = HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", "round(min(1, 2), ")!;

        Assert.StartsWith("round(", Active(help), StringComparison.Ordinal);
        Assert.Equal(1, help.ActiveParameter);
    }

    [Theory]
    [InlineData("round(2.5, 3)")]          // after the closing ')'
    [InlineData("1 + 2")]                   // no call
    [InlineData("round(2.5;\n1 + ")]        // a broken earlier statement doesn't leak
    [InlineData("frobnicate(")]             // no signatures for the name
    public void Outside_a_known_call_there_is_no_help(string text)
    {
        using var service = Service("DateCalcLanguage");
        Assert.Null(HelpAtEnd(service, "DateCalcLanguage", "a.datecalc", text));
    }

    [Fact]
    public void A_template_call_shows_the_parameters_its_definition_declares()
    {
        using var service = Service("GeometryLanguage");
        var help = HelpAtEnd(service, "GeometryLanguage", "a.geom", "def slab(w: Scalar, h: Scalar, d: Scalar) = box w h d;\nmake slab(1, ")!;

        var signature = Assert.Single(help.Signatures);
        Assert.StartsWith("slab(w: Core.Scalar, h: Core.Scalar, d: Core.Scalar)", signature.Label, StringComparison.Ordinal);
        Assert.Equal("h: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void A_tagged_csharp_string_gets_signature_help()
    {
        using var service = Service("DateCalcLanguage");
        const string host = "class C { const string D = /*lang=datecalc*/ \"round(2.5, ";
        var help = HelpAtEnd(service, "DateCalcLanguage", "C.cs", host)!;

        Assert.Equal("digits: Core.Scalar", ActiveParameter(help));
    }

    [Fact]
    public void A_language_without_signatures_gives_no_help()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { let y = f(");
        Assert.Null(service.SignatureHelp("file:///w/a.scopes", new DocumentPosition(0, 19)));
    }
}
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SignatureHelpTests"`
Expected: build error, `ServiceSignatureHelp` not found.

- [ ] **Step 3: Add the result types.** Append to `ServiceTypes.cs`:

```csharp
/// <summary>One overload in signature help: its label, each parameter's [start, end) range in the label, and an optional summary.</summary>
public sealed record ServiceSignature(string Label, IReadOnlyList<(int Start, int End)> Parameters, string? Summary);

/// <summary>The overloads of the call around a position, the active one, and the parameter the cursor is in.</summary>
public sealed record ServiceSignatureHelp(IReadOnlyList<ServiceSignature> Signatures, int ActiveSignature, int ActiveParameter);
```

- [ ] **Step 4: Implement.** Create `Nitrogen.LanguageService/NitrogenLanguageService.Signatures.cs`:

```csharp
using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>
/// Signature help: the overloads of the <c>name(…)</c> call around the cursor, from the language's
/// <see cref="CallSignatures"/> or, for a template, from its declaration. The call is found in the text,
/// because error recovery can drop a half-typed call's commas from the tree: characters inside real
/// tokens other than <c>(</c>, <c>)</c> and <c>,</c> are skipped, and the scan stops at <c>;</c>,
/// <c>{</c> or <c>}</c>. A comment holding parentheses inside an unfinished call can still confuse it.
/// </summary>
public sealed partial class NitrogenLanguageService
{
    public ServiceSignatureHelp? SignatureHelp(string uri, DocumentPosition position)
    {
        if (_hosts.ContainsKey(uri)) return Into(uri, position) is { } inner ? SignatureHelp(inner.Uri, inner.Position) : null;
        if (!_documents.TryGetValue(uri, out var document)) return null;
        int offset = document.Lines.OffsetOf(position);
        if (CallAt(document.Parsed.Tree, document.Text, offset) is not { } call) return null;

        var overloads = (document.Language.Calls?.For(call.Name) ?? []).ToList();
        if (overloads.Count == 0 && TemplateCallee(document, call.NameSpan) is { } template) overloads.Add(template);
        if (overloads.Count == 0) return null;

        var argumentTypes = ArgumentTypes(document, call.Open, call.Active);
        int active = overloads.FindIndex(o => o.Parameters.Count > call.Active &&
            argumentTypes.Select((type, i) => type is null || o.Parameters[i].Type.Equals(type)).All(fits => fits));
        if (active < 0) active = Math.Max(0, overloads.FindIndex(o => o.Parameters.Count > call.Active));
        return new ServiceSignatureHelp(overloads.Select(o => Label(call.Name, o)).ToList(), active, call.Active);
    }

    /// <summary>The call around <paramref name="offset"/>: its callee's name and span, its '(' offset, and the active parameter; null outside an unclosed name(…).</summary>
    static (string Name, TextSpan NameSpan, int Open, int Active)? CallAt(SyntaxTree tree, string text, int offset)
    {
        var opaque = new bool[text.Length];
        for (int node = 0; node < tree.NodeCount; node++)
        {
            var span = tree.Span(node);
            if (tree.ChildCount(node) != 0 || span.Length == 0) continue;
            if (text.AsSpan(span.Start, Math.Min(span.Length, text.Length - span.Start)) is "(" or ")" or ",") continue;
            for (int i = span.Start; i < span.End && i < text.Length; i++) opaque[i] = true;
        }
        int depth = 0, active = 0;
        for (int i = Math.Min(offset, text.Length) - 1; i >= 0; i--)
        {
            if (opaque[i]) continue;
            switch (text[i])
            {
                case ')': depth++; break;
                case ',' when depth == 0: active++; break;
                case ';' or '{' or '}' when depth == 0: return null;
                case '(' when depth > 0: depth--; break;
                case '(':
                {
                    int end = i;
                    while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
                    int start = end;
                    while (start > 0 && IsNamePart(text[start - 1])) start--;
                    if (start == end || char.IsDigit(text[start])) return null;
                    return (text[start..end], new TextSpan(start, end - start), i, active);
                }
            }
        }
        return null;
    }

    static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The signature of the template the callee names, read from its declaration; null when it names none.</summary>
    CallSignature? TemplateCallee(Document document, TextSpan name)
    {
        if (NameAt(document.Uri, document.Lines.PositionOf(name.Start)) is not { Symbols.Count: > 0 } found) return null;
        var types = SemanticsOf(document.Language)[document.Uri].DeclarativeTypes;
        foreach (var symbol in found.Symbols)
            if (types.TemplateSignature(symbol) is { } template)
                return new CallSignature(
                    template.Parameters.Select(p => new CallParameter(p.Name, p.Type ?? SemanticTypes.Error)).ToArray(),
                    template.Result ?? SemanticTypes.Error);
        return null;
    }

    /// <summary>The types of the complete arguments before the active one, by the syntax node each spans exactly; null where unknown.</summary>
    List<SemanticType?> ArgumentTypes(Document document, int open, int active)
    {
        var types = new List<SemanticType?>();
        if (active == 0) return types;
        var semantics = SemanticsOf(document.Language)[document.Uri];
        var tree = document.Parsed.Tree;
        int start = open + 1;
        for (int k = 0; k < active; k++)
        {
            int comma = NextTopLevelComma(document.Text, start);
            if (comma < 0) { types.Add(null); start = document.Text.Length; continue; }
            var (from, to) = Trim(document.Text, start, comma);
            SemanticType? type = null;
            for (int node = 0; node < tree.NodeCount && type is null; node++)
                if (tree.Span(node) is var span && span.Start == from && span.End == to) type = semantics.DeclarativeTypes.TypeOf(node);
            types.Add(type);
            start = comma + 1;
        }
        return types;
    }

    static int NextTopLevelComma(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') { if (depth == 0) return -1; depth--; }
            else if (text[i] == ',' && depth == 0) return i;
        }
        return -1;
    }

    static (int From, int To) Trim(string text, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        return (from, to);
    }

    static ServiceSignature Label(string name, CallSignature signature)
    {
        var label = new System.Text.StringBuilder(name).Append('(');
        var ranges = new List<(int, int)>();
        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            if (i > 0) label.Append(", ");
            int start = label.Length;
            label.Append(signature.Parameters[i].Name).Append(": ").Append(signature.Parameters[i].Type);
            ranges.Add((start, label.Length));
        }
        label.Append(") → ").Append(signature.Result);
        return new ServiceSignature(label.ToString(), ranges, signature.Summary);
    }
}
```

If `SemanticTypes.Error` doesn't exist, use `SemanticTypes.Scalar`'s sibling the codebase uses for an unknown type. `DeclarativeTypes` treats unresolved types as `SemanticTypes.Error` (see `CheckExpansion`), so search there.

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SignatureHelpTests"`
Expected: all pass. Two tests depend on probe-untested paths:
- **`The_types_already_typed_choose_the_overload`:** if it fails, print the type `ArgumentTypes` finds for `2026-10-05`. Null means DateCalc's literal types come from its semantics properties, not `DeclarativeTypes.TypeOf`. In that case, also read the language's hover property, as `SymbolTypeOf` does for symbols, then rerun. Don't drop the preference.
- **`A_template_call_shows_the_parameters_its_definition_declares`:** if it fails at `TemplateSignature`, stop and report.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/ServiceTypes.cs Nitrogen.LanguageService/NitrogenLanguageService.Signatures.cs Nitrogen.Tests/LanguageService/SignatureHelpTests.cs
git commit -m "Show the overloads of the call around the cursor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `textDocument/signatureHelp`

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs`
- Test: `Nitrogen.Tests/LanguageService/SignatureHelpTests.cs`

- [ ] **Step 1: Write the failing test.** Add to `SignatureHelpTests` (with `using System.Text.Json;`):

```csharp
    [Fact]
    public async Task The_server_answers_signature_help()
    {
        string root = Directory.CreateDirectory(Path.Combine(_root, "DateCalcLanguage")).FullName;
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        string doc = new Uri(Path.Combine(root, "a.datecalc")).AbsoluteUri;
        string initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new Uri(root).AbsoluteUri + "\",\"capabilities\":{}}}";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":\"round(2.5, \"}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":\"" + doc
            + "\"},\"position\":{\"line\":0,\"character\":11}}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service, initialize, """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            open, request, """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var triggers = messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("signatureHelpProvider").GetProperty("triggerCharacters");
        Assert.Equal(["(", ","], triggers.EnumerateArray().Select(t => t.GetString()));
        var help = messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5).GetProperty("result");
        Assert.Equal(2, help.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, help.GetProperty("activeParameter").GetInt32());
        var active = help.GetProperty("signatures")[help.GetProperty("activeSignature").GetInt32()];
        Assert.Equal("round(x: Core.Scalar, digits: Core.Scalar) → Core.Scalar", active.GetProperty("label").GetString());
        var digits = active.GetProperty("parameters")[1].GetProperty("label");
        Assert.Equal("digits: Core.Scalar", active.GetProperty("label").GetString()![digits[0].GetInt32()..digits[1].GetInt32()]);
    }
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SignatureHelpTests"`
Expected: the new test fails with `KeyNotFoundException` (no `signatureHelpProvider`).

- [ ] **Step 3: Messages.** In `LspMessages.cs`:
  - add the last `ServerCapabilities` parameter `SignatureHelpOptions? SignatureHelpProvider = null`;
  - add the records:

```csharp
public sealed record SignatureHelpOptions(string[] TriggerCharacters);

/// <summary>A parameter by its [start, end) offsets in the signature's label.</summary>
public sealed record LspParameterInformation(int[] Label);

public sealed record LspSignatureInformation(string Label, LspParameterInformation[] Parameters, string? Documentation = null);

public sealed record LspSignatureHelp(LspSignatureInformation[] Signatures, int ActiveSignature, int ActiveParameter);
```

  - register them: `[JsonSerializable(typeof(LspSignatureHelp))]`.

- [ ] **Step 4: Server.** In `LspServer.cs`:
  - add `SignatureHelpProvider: new SignatureHelpOptions(["(", ","])` to the `initialize` capabilities, after `CodeActionProvider: …`;
  - add the case before `default:`:

```csharp
            case "textDocument/signatureHelp":
            {
                var (uri, position) = At(parameters);
                if (service.SignatureHelp(uri, position) is { } help)
                    await RespondAsync(id, new LspSignatureHelp(
                        help.Signatures.Select(s => new LspSignatureInformation(s.Label,
                            s.Parameters.Select(p => new LspParameterInformation([p.Start, p.End])).ToArray(), s.Summary)).ToArray(),
                        help.ActiveSignature, help.ActiveParameter), LspJson.Default.LspSignatureHelp, cancel);
                else
                    await RespondNullAsync(id, cancel);
                break;
            }
```

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~SignatureHelpTests|FullyQualifiedName~LspServerTests|FullyQualifiedName~QuickFixTests|FullyQualifiedName~InlayHintLspTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.LanguageService/Lsp Nitrogen.Tests/LanguageService/SignatureHelpTests.cs
git commit -m "Serve signature help over LSP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Docs, full check, probe

**Files:**
- Modify: `docs/editor-support.md`, `docs/superpowers/specs/2026-10-08-signature-help-design.md`

- [ ] **Step 1: Feature matrix.** In `docs/editor-support.md`'s table, add after the *Completion* row:

```markdown
| Signature help³ | ✓ | ✓ | — (left to Rider) |
```

  and after the `²` footnote line:

```markdown
³ For `name(…)` calls, when the helper sources export `CallSignatures`, or for templates (`lowers template`).
```

  In *Helper sources*, append after the `DiagnosticFixes` paragraph:

```markdown
A public static `CallSignatures` lists a language's callable names and their overloads, with named
parameters, for signature help: inside a `name(…)` call the editor shows the callee's overloads and
highlights the parameter the cursor is in. A template call needs none; its parameters come from the
template's declaration. A language has at most one; two are warning `NGR0005`.
```

- [ ] **Step 2: Spec.** Change the status line to `Status: implemented (YYYY-MM-DD). Part of the goal of first-class language support.`, using the date of this step. In section 3, replace step 1 ("**The call.** In the document's syntax tree, take the leaf tokens…") with:

  "1. **The call.** Error recovery can drop a half-typed call's commas from the syntax tree, so the call is found in the text. Scanning back from the cursor, characters inside real leaf tokens other than `(`, `)` and `,` are skipped; the innermost `(` not closed before the cursor, with a name just before it, is the call. The active parameter is the number of top-level `,` after it. The scan stops with no help at a `;`, `{` or `}` at depth 0. A comment containing parentheses inside an unfinished call can still confuse it."

- [ ] **Step 3: Full build, tests and link check**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass: 1,090 plus 1 + 2 + 1 + 11 + 1 = 1,106.

Run: `python3 eng/check-links.py`
Expected: every link resolves.

- [ ] **Step 4: Probe the built server.** Build the CLI with `dotnet build Nitrogen.Cli -c Release`. Over stdio, as for earlier features, initialize with `examples/DateCalc` as the root, open a scratch `sig.datecalc` with the text `round(2.5, `, and request `textDocument/signatureHelp` at line 0, character 11. Expected: 2 signatures, the active one `round(x: Core.Scalar, digits: Core.Scalar) → Core.Scalar`, active parameter 1. Report the output.

- [ ] **Step 5: Commit**

```bash
git add docs/editor-support.md docs/superpowers/specs/2026-10-08-signature-help-design.md
git commit -m "Document signature help

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
