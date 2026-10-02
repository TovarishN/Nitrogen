# Workspace indexing in the language service — design

**Date:** 2026-09-30. **Status:** approved conversational design, pending review of this written spec.

## Problem

The language service binds only the documents the editor has open. A reference to a declaration in a closed file is `NB0001 unresolved`, go to definition cannot reach it, and rename misses it. In the Nitrogen.Concepts catalog, opening only `realizations/SemanticCatalog.CatalogTool.ncat` reports three unresolved capabilities that `validate` accepts; opening the three capability files clears them. The editor should agree with the command line without the user opening every file a record refers to.

## Goal

For every workspace grammar language (a language from a `nitrogen.json`), the service also binds the language's files on disk in the editor's workspace folder. Open documents keep their editor text; closed ones come from disk and follow changes on disk. Diagnostics are still published only for open documents.

Out of scope: `.ngr` grammar files (they are served by the built-in grammar language and already reload through the grammar hook), several workspace folders (the first one is indexed, as the server already uses the first), and publishing diagnostics for closed files.

## Design

### 1. Closed documents

`NitrogenLanguageService` keeps a second map, `_closed`, of `Document`s read from disk (version 0). A closed document is `Set` in its language's project exactly like an open one, so binding, semantics, completion, and HIR see it.

- **Open** of a file that is indexed moves it from `_closed` to `_documents`; the editor text replaces the disk text in the project.
- **Close** of a file inside the workspace whose extension belongs to an indexed language re-reads it from disk into `_closed` (the project keeps a copy); otherwise the file leaves the project, as today.
- Features that report locations in other files — definition, references, hover's "declared in … line", and rename edits — look documents up in `_documents`, then `_closed`. `IsOpen`, `VersionOf`, `Diagnostics`, semantic tokens, outline, and the other per-document features stay open-only.
- Rename edits closed files by URI; LSP `WorkspaceEdit` applies edits to unopened files.

### 2. Indexing the workspace

`IndexWorkspace(string root)` records the workspace root and loads every file under it whose extension belongs to a workspace grammar language, skipping directories named `bin`, `obj`, `node_modules`, or starting with `.`. Files are visited in ordinal path order; at most 10,000 files are indexed and files over 1 MiB are skipped, so a large tree cannot stall the server. It returns the open documents whose diagnostics may change.

Indexing follows the languages: when a grammar language compiles and is re-registered, its closed documents are re-parsed with the new language, as open ones already are; when a language is unregistered, its closed documents leave. When `ConfigureWorkspace` changes the languages, the service re-indexes.

### 3. The workspace root is separate from the configuration root

`LspServer` keeps two directories:

- the **workspace root**, from the client's `rootUri` (or first workspace folder), always; and
- the **configuration root**, which is `--config`'s directory when given, else the workspace root.

At `initialized` the server calls `ConfigureWorkspace(configurationRoot)`, then `IndexWorkspace(workspaceRoot)`. Without `--config` the two are the same directory, so the generic setup is unchanged apart from indexing.

### 4. Changes on disk

`FileChanged(path)` (from `workspace/didChangeWatchedFiles`) keeps its current handling of `nitrogen.json` and grammar files, and adds: for a file under the workspace root with an indexed language's extension that is not open, re-read it into `_closed` when it exists, or remove it from `_closed` and the project when it does not. It returns the open documents of that language.

Clients must be asked to report those files. When the client's `initialize` capabilities include `workspace.didChangeWatchedFiles.dynamicRegistration: true`, the server sends one `client/registerCapability` request after indexing, registering `workspace/didChangeWatchedFiles` with a `**/*<ext>` glob per indexed extension plus `**/nitrogen.json`. Clients without dynamic registration still get indexing at start and re-reads on close; their editors can report changes through their own file watchers, as the generic VS Code extension does.

### 5. Responses from the client

The server loop treats every message with an `id` as a request. A response to the server's own request (an `id` with no `method`) is now ignored instead of answered with `MethodNotFound`.

## Error handling

A file that cannot be read (permissions, removed during the scan) is skipped; indexing never fails the `initialized` notification. A closed file that no longer parses is bound with its recovered tree, as an open one is.

## Testing

Service tests with a temporary workspace and a `nitrogen.json` grammar language:

- a reference in an open file to a declaration in a closed file resolves; definition and references return the closed file's locations; rename edits it;
- open, edit, then close a file: the project goes back to the disk text;
- a file created, changed, or deleted on disk and reported through `FileChanged` is added, re-read, or removed;
- files under `bin/`, `obj/`, `node_modules/`, and `.git/`, and files of other extensions, are not indexed;
- a grammar edit that re-registers the language keeps closed files bound.

Protocol tests:

- with `--config` (a fixed configuration root), the client's workspace root is still indexed;
- a client advertising dynamic registration gets `client/registerCapability` with the extension globs; one that does not, gets none;
- a response message from the client produces no reply.

End to end: rebuild the catalog plugins and open only `SemanticCatalog.CatalogTool.ncat` over LSP with the catalog as the workspace; it has no diagnostics, and definition on `SemanticCatalog.ValidateRecords` returns the capability file.

## Follow-up

In Nitrogen.Concepts: bump `external/Nitrogen` and rebuild the plugins with `tools/editors/build.sh`.
