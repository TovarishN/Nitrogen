# Value hints in tagged C# strings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An open `.cs` file shows the values of its tagged strings' statements as inlay hints: after the closing quote for a one-line literal, at each statement's line end inside a multi-line one.

**Architecture:** `EmbeddedString` records its literal's end offset. `ValueHints` gets a host branch, after the pattern of `HostDiagnostics` and `HostTokens`: it collects each embedded document's hints and maps them into the host. The LSP layer and VS Code need no change. The Rider C# client turns inlay hints on.

**Tech Stack:** C# / .NET 10, xUnit, Kotlin (Rider platform LSP API 2026.2).

**Spec:** `docs/superpowers/specs/2026-10-07-csharp-string-value-hints-design.md`

**Conventions:**
- Build with `dotnet build Nitrogen.slnx -warnaserror`. Run focused tests with `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- The Rider plugin builds with the JDK 25 bundled in the Rider SDK in the Gradle cache:
  `JAVA_HOME=~/.gradle/caches/9.7.1/transforms/e04b7ac9512e2f9e23c20d0f83cb7767/transformed/JetBrains.Rider-2026.2-aarch64/jbr/Contents/Home gradle buildPlugin --offline` in `editors/rider`. If that path is gone, use any JDK 25.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `csharp-string-hints`.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `Nitrogen.LanguageService/EmbeddedStrings.cs` | `EmbeddedString.End` | 1 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Embedded.cs` | `HostValueHints`; drop cached hints on unembed | 2 |
| `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` | host branch in `ValueHints` | 2 |
| `Nitrogen.Tests/LanguageService/EmbeddedStringTests.cs` | `End` and host hint tests | 1, 2 |
| `Nitrogen.Tests/LanguageService/InlayHintLspTests.cs` | `.cs` inlay hint session | 3 |
| `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt` | C# client asks for inlay hints | 4 |
| `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs` | generated C# client asserts it | 4 |
| the spec | status | 5 |

---

### Task 1: `EmbeddedString.End`

**Files:**
- Modify: `Nitrogen.LanguageService/EmbeddedStrings.cs` (record and `Find`)
- Test: `Nitrogen.Tests/LanguageService/EmbeddedStringTests.cs` (`Tagged_literals_decode_their_value`, `A_raw_literal_drops_its_closing_indentation_and_maps_each_character_to_its_source`)

- [ ] **Step 1: Write the failing assertions.** At the end of `Tagged_literals_decode_their_value`, add:

```csharp
        Assert.Equal(source.LastIndexOf('"') + 1, found.End); // just past the closing quote
```

At the end of `A_raw_literal_drops_its_closing_indentation_and_maps_each_character_to_its_source`, add:

```csharp
        Assert.Equal(source.LastIndexOf("\"\"\"", StringComparison.Ordinal) + 3, found.End);
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EmbeddedStringTests"`
Expected: build error `'EmbeddedString' does not contain a definition for 'End'`.

- [ ] **Step 3: Implement.** In `EmbeddedStrings.cs`, change the record and its summary:

```csharp
/// <summary>
/// A C# string literal tagged with a language: its tag, its value, and where each value character
/// came from. <see cref="Map"/> has one source offset per value character plus one for the end, so
/// a value span [s, e) is the source span [Map[s], Map[e]). <see cref="End"/> is the source offset just
/// past the literal (its closing quote, and a <c>u8</c> suffix).
/// </summary>
internal sealed record EmbeddedString(string Tag, string Value, int[] Map, int End)
```

In `Find`, pass the literal's end:

```csharp
                if (tag is not null && literal.Value is { } value) found.Add(new EmbeddedString(tag, value.Text, value.Map, literal.End));
```

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EmbeddedStringTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/EmbeddedStrings.cs Nitrogen.Tests/LanguageService/EmbeddedStringTests.cs
git commit -m "Record where a tagged C# string literal ends

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Value hints on a C# host

**Files:**
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs` (`ValueHints`)
- Modify: `Nitrogen.LanguageService/NitrogenLanguageService.Embedded.cs` (`Unembed`, new `HostValueHints`)
- Test: `Nitrogen.Tests/LanguageService/EmbeddedStringTests.cs`

- [ ] **Step 1: Write the failing tests.** Add to `EmbeddedStringTests`:

```csharp
    static readonly DocumentRange Whole = new(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));

    const string Snippets = """"
        class C
        {
            object A = Run(/*lang=datecalc*/ """
                let christmas = 2026-12-25;
                weekday(christmas);
                """);
            const string D = /*lang=datecalc*/ "2026-10-05 + 6 weeks;";
        }
        """";

    [Fact]
    public void Values_show_inside_a_multiline_string_and_after_a_one_line_string()
    {
        using var service = Service();
        string uri = Uri("Snippets.cs");
        service.Open(uri, 1, Snippets);

        var hints = service.ValueHints(uri, Whole);

        Assert.Equal(["= 2026-12-25 Fri", "= Friday", "= 2026-11-16 Mon"], hints.Select(h => h.Label));
        Assert.Equal(
            [
                At(Snippets, "2026-12-25;", "2026-12-25;".Length),
                At(Snippets, "weekday(christmas);", "weekday(christmas);".Length),
                At(Snippets, "6 weeks;\"", "6 weeks;\"".Length),
            ],
            hints.Select(h => h.At));
    }

    [Fact]
    public void A_string_with_an_error_hides_only_its_own_values()
    {
        using var service = Service();
        string uri = Uri("Mixed.cs");
        service.Open(uri, 1, "class C\n{\n    const string A = /*lang=datecalc*/ \"2026-10-05 + 2026-10-06;\";\n    const string B = /*lang=datecalc*/ \"1 + 1;\";\n}\n");

        Assert.Equal("= 2", Assert.Single(service.ValueHints(uri, Whole)).Label);
    }

    [Fact]
    public void Host_values_are_filtered_by_host_range()
    {
        using var service = Service();
        string uri = Uri("Snippets.cs");
        service.Open(uri, 1, Snippets);
        int line = At(Snippets, "const string D").Line;

        var hint = Assert.Single(service.ValueHints(uri,
            new DocumentRange(new DocumentPosition(line, 0), new DocumentPosition(line, int.MaxValue))));
        Assert.Equal("= 2026-11-16 Mon", hint.Label);
    }

    [Fact]
    public void An_edit_to_the_host_shows_the_new_values()
    {
        using var service = Service();
        string uri = Uri("Edit.cs");
        service.Open(uri, 1, "const string A = /*lang=datecalc*/ \"1 + 1;\";");
        Assert.Equal("= 2", Assert.Single(service.ValueHints(uri, Whole)).Label);

        service.Change(uri, 2, "const string A = /*lang=datecalc*/ \"2 + 2;\";");
        Assert.Equal("= 4", Assert.Single(service.ValueHints(uri, Whole)).Label);
    }

    [Fact]
    public void A_skipped_language_shows_no_values_in_strings()
    {
        using var service = Service();
        service.SkipEmbedded(["datecalc"]);
        string uri = Uri("Snippets.cs");
        service.Open(uri, 1, Snippets);

        Assert.Empty(service.ValueHints(uri, Whole));
    }
```

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EmbeddedStringTests"`
Expected: FAIL. The first four new tests get no hints (`ValueHints` returns `[]` for a host). `A_skipped_language_shows_no_values_in_strings` already passes.

- [ ] **Step 3: Implement the host branch.** In `NitrogenLanguageService.Evaluation.cs`, make the first line of `ValueHints`:

```csharp
        if (_hosts.TryGetValue(uri, out var host)) return HostValueHints(host).Where(h => Within(h.At, range)).ToList();
```

Update its summary to: `/// <summary>The values of the document's statements within <paramref name="range"/>, or of a C# host's tagged strings at host positions; empty when its language shows none.</summary>`

In `NitrogenLanguageService.Embedded.cs`, add after `HostTokens`:

```csharp
    /// <summary>
    /// The values of the host's strings at host positions: every value of a literal on one line goes after
    /// its closing quote, so it doesn't read as string content; in a multi-line literal, each goes at its
    /// statement's end.
    /// </summary>
    IEnumerable<ValueHint> HostValueHints(Host host)
    {
        var whole = new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));
        foreach (string uri in host.Embedded)
        {
            var source = _embedded[uri].Source;
            var end = host.Lines.PositionOf(source.End);
            bool oneLine = host.Lines.PositionOf(source.Map[0]).Line == end.Line;
            foreach (var hint in ValueHints(uri, whole))
                yield return hint with { At = oneLine ? end : OutOf(uri, new DocumentRange(hint.At, hint.At)).Start };
        }
    }
```

In `Unembed`, add `_hints.Remove(uri);` after `_inspection.Remove(uri);`.

Update the class summary's second sentence ("Requests on the host go to the virtual document under the position, …") by appending: "Diagnostics, tokens and values are gathered from every string and mapped back the same way."

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~EmbeddedStringTests|FullyQualifiedName~ValueHintTests"`
Expected: all pass. If `Values_show_inside_a_multiline_string_and_after_a_one_line_string` fails on the multi-line positions only, print `hints.Select(h => h.At)`. A position one line too far means `OutOf` mapped the statement end to the string's next line start. Map `hint.At` one character back (the `;`), then add one to the host character, and don't loosen the test.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/NitrogenLanguageService.Embedded.cs Nitrogen.LanguageService/NitrogenLanguageService.Evaluation.cs Nitrogen.Tests/LanguageService/EmbeddedStringTests.cs
git commit -m "Show values of tagged strings in their C# host

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Over LSP

No server change is expected. This task proves that `textDocument/inlayHint` on a `.cs` URI returns host positions.

**Files:**
- Test: `Nitrogen.Tests/LanguageService/InlayHintLspTests.cs`

- [ ] **Step 1: Write the test.** Add to `InlayHintLspTests`:

```csharp
    [Fact]
    public async Task A_csharp_file_gets_the_values_of_its_tagged_strings()
    {
        string host = Uri("Host.cs");
        const string text = "class C { const string D = /*lang=datecalc*/ \"1 + 1;\"; }";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + host
            + "\",\"languageId\":\"csharp\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/inlayHint\",\"params\":{\"textDocument\":{\"uri\":\"" + host
            + "\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":1,\"character\":0}}}}";

        var messages = await Session(Initialize("{}"), Initialized, open, request, Shutdown, Exit);

        var hint = Assert.Single(messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5)
            .GetProperty("result").EnumerateArray());
        Assert.Equal("= 2", hint.GetProperty("label").GetString());
        Assert.Equal(0, hint.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(text.IndexOf("\"1 + 1;\"", StringComparison.Ordinal) + "\"1 + 1;\"".Length,
            hint.GetProperty("position").GetProperty("character").GetInt32());
    }
```

- [ ] **Step 2: Run it and check it passes**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~InlayHintLspTests"`
Expected: all pass. If the new test fails with an empty result, stop and report: it would mean the server doesn't route `.cs` URIs to `ValueHints` as the spec assumes.

- [ ] **Step 3: Commit**

```bash
git add Nitrogen.Tests/LanguageService/InlayHintLspTests.cs
git commit -m "Cover tagged-string values over LSP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Rider's C# client asks for inlay hints

**Files:**
- Modify: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs` (`Rendered_build_uses_the_template_plugins_and_platform_api`)

- [ ] **Step 1: Write the failing assertion.** At the end of `Rendered_build_uses_the_template_plugins_and_platform_api`, add:

```csharp
        // The C# client shows the values of tagged strings too.
        string csharpClient = Read("src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt");
        Assert.Contains("override val inlayHintCustomizer: LspInlayHintCustomizer = NitrogenInlayHints", csharpClient, StringComparison.Ordinal);
        Assert.DoesNotContain("LspInlayHintDisabled", csharpClient, StringComparison.Ordinal);
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests"`
Expected: FAIL with `Assert.Contains() Failure`.

- [ ] **Step 3: Implement.** In `NitrogenCSharpStrings.kt`:
  - Delete the line `import com.intellij.platform.lsp.api.customization.LspInlayHintDisabled`.
  - Replace `override val inlayHintCustomizer: LspInlayHintCustomizer = LspInlayHintDisabled` with `override val inlayHintCustomizer: LspInlayHintCustomizer = NitrogenInlayHints`.
  - In the class comment, change "its colours are added to Rider's, and diagnostics, completion, hover, go to definition and find usages work in the strings. Everything that would compete with Rider's C# support (rename, structure view, formatting, code actions, highlighting usages, hints) is left to Rider." to "its colours are added to Rider's, and diagnostics, completion, hover, go to definition, find usages and statement values work in the strings. Everything that would compete with Rider's C# support (rename, structure view, formatting, code actions, highlighting usages) is left to Rider."

- [ ] **Step 4: Run the tests and build the plugin**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests"`
Expected: all pass.

Run the Rider build from the Conventions in `editors/rider`.
Expected: `BUILD SUCCESSFUL`.

- [ ] **Step 5: Commit**

```bash
git add editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "Ask for statement values in Rider's C# strings client

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Full check, probe, docs

**Files:**
- Modify: `docs/superpowers/specs/2026-10-07-csharp-string-value-hints-design.md` (status line)

- [ ] **Step 1: Full build and tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all pass. That's the previous 1,043 plus 6 new tests (5 in `EmbeddedStringTests`, 1 in `InlayHintLspTests`).

- [ ] **Step 2: Probe the built server on `Snippets.cs`.** Build the CLI with `dotnet build Nitrogen.Cli -c Release`. Save this script to the session scratchpad as `snippets_probe.py`:

```python
import json, subprocess, sys, pathlib
root = pathlib.Path("examples/DateCalc").resolve(); host = root / "Snippets.cs"
p = subprocess.Popen(["Nitrogen.Cli/bin/Release/net10.0/nitrogen", "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
def send(m):
    b = json.dumps(m).encode(); p.stdin.write(b"Content-Length: %d\r\n\r\n" % len(b) + b); p.stdin.flush()
def read():
    n = 0
    while (line := p.stdout.readline().strip()):
        if line.lower().startswith(b"content-length"): n = int(line.split(b":")[1])
    return json.loads(p.stdout.read(n))
def until(i):
    while (m := read()).get("id") != i: pass
    return m
send({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"rootUri":root.as_uri(),"capabilities":{}}}); until(1)
send({"jsonrpc":"2.0","method":"initialized","params":{}})
text = host.read_text()
send({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":host.as_uri(),"languageId":"csharp","version":1,"text":text}}})
send({"jsonrpc":"2.0","id":2,"method":"textDocument/inlayHint","params":{"textDocument":{"uri":host.as_uri()},"range":{"start":{"line":0,"character":0},"end":{"line":1000,"character":0}}}})
lines = text.split("\n")
for h in until(2)["result"]:
    l, c = h["position"]["line"], h["position"]["character"]; print(f'{lines[l][:c].rstrip():<60} {h["label"]}')
send({"jsonrpc":"2.0","id":9,"method":"shutdown"}); until(9); send({"jsonrpc":"2.0","method":"exit"}); print("exit", p.wait(timeout=10))
```

Run it from the repo root with `python3 <scratchpad>/snippets_probe.py`.
Expected: `= 2026-12-25 Fri`, `= 81` and `= Friday` at the ends of the three raw-string lines; `= 2026-11-16 Mon` after `"2026-10-05 + 6 weeks;"`; `exit 0`. Report the output.

- [ ] **Step 3: Spec status.** Change the spec's first line after the title to `Status: implemented (YYYY-MM-DD). Builds on`, using the date of this step, and keep the link line that follows.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-10-07-csharp-string-value-hints-design.md
git commit -m "Mark tagged-string value hints implemented

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
