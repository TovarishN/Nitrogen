# Nitrogen for VS Code

Colours, outline, go to definition, references, rename, hover and completion for `.ngr` files
and for any language a workspace `nitrogen.json` declares, all
served by `nitrogen lsp` (issue 238). The same works in C# files inside string literals tagged with a
language (`/*lang=calc*/ "1 + 2;"` or a `// language=calc` comment before the statement); their colors
are painted as decorations so the C# extension keeps its own.

Build: `npm install`, `npm run compile`, `npm run package` gives `nitrogen-0.3.0.vsix`; install it with
`code --install-extension nitrogen-0.3.0.vsix`. Point `nitrogen.server.path` at the nitrogen executable,
for example `Nitrogen.Cli/bin/Release/net10.0/nitrogen` after `dotnet build Nitrogen.slnx -c Release`.

A workspace grammar language:

```json
{ "languages": [ { "name": "calc", "extensions": [".calc"],
                   "grammars": ["Nitrogen.Tests/Grammars/Calc.ngr"],
                   "start": "Calc.Program" } ] }
```

For a single language, `nitrogen package` builds a self-contained extension instead; see the repository README.
