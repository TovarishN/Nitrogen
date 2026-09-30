# Nitrogen for Rider

This generic IntelliJ Platform plugin registers `.ngr` files and starts the
Nitrogen language server with `nitrogen lsp`. It is also the template used by
`nitrogen generate rider` for grammar-specific plugins.

Build from this directory with `gradle buildPlugin` (or add the Gradle wrapper
for a reproducible local build), then install the generated ZIP from Rider's
plugin settings.

The launcher defaults to `nitrogen` on `PATH`. A generated plugin can instead
be configured with an explicit executable or carry a locally supplied,
platform-specific executable. No server binaries are downloaded by the plugin
or generator.

`nitrogen generate rider --self-contained` bundles the language and a portable server run with `dotnet`; `nitrogen package` builds the ZIP.
