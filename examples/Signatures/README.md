# Signed languages and native source checks

Open this folder with the main Nitrogen VS Code extension and the .NET 10 runtime. `sample.txt` gets DateCalc navigation, completion, hover and values from its signature. A generated extension and a language-specific filename extension are optional. The DateCalc declaration reuses the existing grammar and C# semantics through explicit relative paths; it does not copy an evaluator. The default generated namespace is `DateCalc.Syntax`.

From the repository root:

```sh
dotnet run --project Nitrogen.Cli -- parse examples/Signatures/sample.txt
dotnet run --project Nitrogen.Cli -- check examples/Signatures
```

The program works without `nitrogen.json`; the JSON here demonstrates additional glob rules for native files. A grammar starts with `#!nitrogen grammar`, followed by one `language` declaration and any syntax modules. `start Module.Rule;` is required. `grammars`, `sources`, `usings`, `extensions`, `tokens` and `types` are optional declaration settings. An explicit `namespace` overrides the `<LanguageName>.Syntax` default. `.ngr` and legacy JSON definitions remain supported.

`Host.cs` demonstrates whole-document augmentation and an independent region. `Page.razor` demonstrates a region inside an HTML comment. Replace `approved text` with `NITROGEN_FORBIDDEN` to see `NC1001` at the original native source location. Both retain their native editor language and providers. Nitrogen adds source-text diagnostics; it does not replace the C#/Razor compiler or provide native syntax/semantic models. A region has its own parse and binding scope.

Signatures may follow a BOM, blank lines or leading comments. Native files put signatures in `//`, `/* ... */` or `<!-- ... -->` comments. Region markers must occupy their own lines in a block comment, have a nonempty body and a matching `#!nitrogen end`, and cannot nest. Marker-like text in native strings does not select regions. UTF-16 locations and line endings are retained.

A primary `language` signature overrides extension selection. Augmentations are additive; identical language/input selections are checked once. Grammar signatures cannot mix with program/augmentation signatures, and duplicate primary signatures are errors. Explicit references are local paths relative to the containing file. JSON glob rules are relative to their configuration folder; excludes win and files outside that folder do not match.

For build integration, reference `Nitrogen.Build` once, as in `Signatures.csproj`. It supplies Runtime, Generator and a bundled checker: no globally installed CLI is needed. The package requires the .NET 10 runtime. It registers project `Compile`, `None`, `Content`, `AdditionalFiles` and explicit `NitrogenInput` items; dependencies outside the project must be registered, as the DateCalc grammar/helpers are here. Files merely present elsewhere on disk are unavailable to the build. Set `NitrogenEnabled=false` to disable this integration. Unconfigured projects do not launch the checker.

Before this package version is published, pack Runtime, Generator and Build into a local feed, then build this example with that feed:

```sh
for project in Nitrogen.Runtime Nitrogen.Generator Nitrogen.Build; do
  dotnet pack "$project" -c Release -o /tmp/nitrogen-feed
done
dotnet build examples/Signatures/Signatures.csproj --source /tmp/nitrogen-feed
```

Grammar, helper and configuration saves recheck without restarting LSP. Unsaved editor text takes precedence over disk. If an edit cannot compile, `NGR0405` identifies last-good analysis while current compilation errors remain visible. Fix those errors to resume current analysis. Removing a signature removes that analysis layer. Workspace scanning excludes hidden directories, `bin`, `obj`, `node_modules`, `artifacts` and `dist`, and is bounded to 2,000 files of at most 1 MiB each. Native build tools may impose further filename restrictions, such as Csc rejecting quotes in `Compile` paths.
