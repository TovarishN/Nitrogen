# Nitrogen for VS Code

Colours, outline, go to definition, references, rename, hover and completion for `.ngr` files
and for any language a workspace `nitrogen.json` declares, all
served by `nitrogen lsp` (issue 238). The same works in C# files inside string literals tagged with a
language (`/*lang=calc*/ "1 + 2;"` or a `// language=calc` comment before the statement); their colors
are painted as decorations so the C# extension keeps its own. Strings of a language that an installed
generated extension (`nitrogen package`) carries are left to that extension.

The `.vsix` of a release carries a portable Nitrogen server and runs it with `dotnet` (.NET 10), found
through `DOTNET_ROOT`, the standard install locations and `PATH`; `nitrogen.dotnetPath` names another
`dotnet`, and `nitrogen.server.path` replaces the bundled server.

Build: `npm install`, `npm run compile`, `npm run package` gives `nitrogen-0.9.1.vsix`; install it with
`code --install-extension nitrogen-0.9.1.vsix`. A `.vsix` built this way carries no server: point
`nitrogen.server.path` at the nitrogen executable, for example `Nitrogen.Cli/bin/Release/net10.0/nitrogen`
after `dotnet build Nitrogen.slnx -c Release`.

A workspace grammar language:

```json
{ "languages": [ { "name": "calc", "extensions": [".calc"],
                   "grammars": ["Nitrogen.Tests/Grammars/Calc.ngr"],
                   "start": "Calc.Program" } ] }
```

For a single language, `nitrogen package` builds a self-contained extension instead; see [installable plugins for a language](https://github.com/TovarishN/Nitrogen/blob/main/docs/editor-support.md#installable-plugins-for-a-language).

Signed grammars (`#!nitrogen grammar`) and programs (`#!nitrogen language "./Calc.syntax"`) are recognized at startup and as you edit, regardless of filename extension. No configuration or generated plugin is required. Removing a primary signature restores the previous language mode. The main extension owns signatures when generated Nitrogen extensions are also installed.

Use `// #!nitrogen augment "./Rules.syntax"` in C#, or `<!-- #!nitrogen augment "./Rules.syntax" regions -->` in Razor. Augmentation retains the native language and adds Nitrogen diagnostics only. Explicit block-comment regions use `#!nitrogen region "./Rules.syntax"` and `#!nitrogen end` on their own lines. `nitrogen.json` can apply augmentation through workspace globs. Grammar/helper/configuration saves recheck without restart; `NGR0405` marks last-good analysis after a broken edit. See [signature examples](https://github.com/TovarishN/Nitrogen/blob/main/examples/Signatures/README.md) for complete syntax and exclusions.

Bare names such as `#!nitrogen language DateCalc` and `// #!nitrogen augment SourceRules` resolve across open workspace folders. Names are case-sensitive and must be unique; ambiguity diagnostics list candidate paths. Definition add/rename/delete and workspace-folder changes recheck without restart. Explicit paths remain deterministic. Build and manifest-based CLI checks use registered project inputs rather than the editor workspace.
