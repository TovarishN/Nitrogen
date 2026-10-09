# Diagnostics for closed files

Status: approved (2026-10-09). Part of the goal of first-class language support.

## Problem

The server publishes diagnostics only for open documents (and C# hosts). A workspace's other files
are already read, parsed and bound by the workspace index, so references into them resolve, but their
errors appear only once each file is opened. Closed grammars are compiled with their language, and
each compile reports errors with file paths for every grammar, open or not, yet nothing publishes them
for a closed grammar. Mature tooling lists the whole workspace's problems.

Typing in an open file can change a closed file's errors (renaming a name it references), so closed
files must be re-checked after edits, without bringing back the per-keystroke cost the coalescing work
removed.

## Goal

The Problems panel lists the errors of every indexed file and every grammar of the workspace, open or
closed, kept current after edits by checking closed files only while the server has nothing else to do.

Out of scope:
- a setting to turn closed-file diagnostics off;
- closed C# files (they aren't indexed: their tagged strings are served while open);
- pull diagnostics (`workspace/diagnostic`).

## 1. Service

- **`Diagnostics(uri)`** also answers for:
  - **a closed indexed file** (`_closed`): the same diagnostics as for an open document (parse,
    binding, semantic, lowering, grammar-language hook and assist diagnostics), computed on the closed
    document;
  - **a closed grammar** of a `nitrogen.json` language: the compile diagnostics of its path
    (`GrammarDiagnostics`), as an open grammar gets them.
  For any other URI that isn't open, it returns an empty list, as today.
- **`ClosedDiagnosticFiles()`** returns the URIs whose diagnostics the server publishes while they are
  closed: every closed indexed file, and every grammar file (`GrammarLanguage.Files()`) of the
  configured languages, minus every open document; ordered by URI (ordinal), without duplicates.

## 2. Server: the idle pass

- **Pending files.** The server keeps a list of closed files still to check and a cursor into it.
- **Restart.** A batch whose steps affect any document (a change, open, close, watched-file event, or
  `initialized`) restarts the pass: the list becomes `ClosedDiagnosticFiles()` and the cursor goes to
  its start. A batch that affects nothing (a hover, completion, cancellation) leaves the pass as it was.
- **Running.** After a batch, while the queue is empty and files are pending, the server checks the
  next pending file: computes `Diagnostics(uri)` and publishes them (with no version) if they differ
  from what it last published for that URI. Between files it looks at the queue again: when a message
  is waiting, the pass stops and the batch is handled, and the pass then continues from its cursor.
- **What it last published.** The server remembers, per closed URI, the diagnostics it last
  published for it, and compares by value (`ServiceDiagnostic` equality, in order). An empty list
  counts: a file that becomes clean gets one empty publish.
- **Leaving.** When a pass restarts, every URI it published for that is no longer in the list and isn't
  open (deleted, dropped from the index, or out of `nitrogen.json`) gets one empty publish and is
  forgotten. A URI that is now open is forgotten without a publish: its diagnostics come from the open
  path, with its version.
- **Closing a file** still publishes its empty diagnostics at once, as today; the pass then
  republishes its errors, if it has any, as a closed file. The server forgets what it last published
  for it, so the pass always publishes it again.
- **Idle.** `LspServer.Idle` is set only when the queue is empty and no files are pending, so a test
  feeding input in lockstep sees each pass finished.
- **Exit.** A batch ending the session ends the pass.

## 3. Editors

- **VS Code:** no change; the Problems panel shows diagnostics for closed files.
- **Rider:** no change in the plugin. Whether it shows diagnostics for files not open in an editor is
  checked by hand and recorded in the feature matrix.
- **Feature matrix:** `docs/editor-support.md` gains a *Diagnostics in closed files* row: ✓ in VS Code;
  Rider as checked; — for C# strings (closed C# files aren't indexed).

## 4. Tests

- **Service:**
  - a closed indexed file using an undeclared name reports it (`NB0001`);
  - a closed grammar with a syntax error reports its compile diagnostic;
  - `ClosedDiagnosticFiles()` lists closed indexed files and closed grammars, and not open documents.
- **Server, in lockstep (one message at a time):**
  - after `initialized`, a closed file with an error is published with it (no version);
  - declaring the missing name in an open file republishes the closed file, now empty;
  - a batch with only a hover publishes nothing for closed files;
  - deleting the closed file on disk (a watched-file event) publishes it empty;
  - opening the closed file publishes it with its version, and fixing it there leaves no later
    closed-file publish for it.
- **Server, burst:** after a burst of edits, the last publish for each closed file matches its final
  diagnostics.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
