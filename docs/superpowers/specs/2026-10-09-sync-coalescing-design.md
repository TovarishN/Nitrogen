# Incremental sync, coalescing and cancellation

Status: implemented (2026-10-09). Part of the goal of first-class language support.

## Problem

The server handles one message at a time, in order, and re-checks a document on every
`didChange`. Its file is reparsed and rebound, then the diagnostics of every affected document are
published. Measured with the Release server over stdio on a DateCalc file:

| File | One hover | 20 quick changes, then `semanticTokens/full` |
| --- | --- | --- |
| 2,000 lines | 5 ms | answered after 0.8 s |
| 10,000 lines | 7 ms | answered after 6.5 s |

After: the same 20 changes, sent as ranged edits, then `semanticTokens/full`, answered after 0.2 s
(2,000 lines) and 1.0 s (10,000 lines). The first change arrives alone and is re-checked while the
other 19 queue; they then cost one re-check, before the request itself.

Requests are cheap. A change costs about 325 ms on 10,000 lines, and a burst of keystrokes makes the
next request wait for every intermediate re-check, though only the last text matters. The server
ignores `$/cancelRequest`, and with full sync the editor resends the whole file on every keystroke.

## Goal

A burst of changes costs one re-check, a request the editor cancels while it waits isn't computed,
and the editor sends only what changed. The service stays single-threaded and unchanged apart from
one accessor.

Out of scope:
- **Stopping work midway:** a request or re-check already running finishes. Cancelling it would
  need a thread-safe service and cancellation through parsing and binding.
- **Incremental parsing:** reusing unchanged syntax would cut the 325 ms itself. It is runtime work
  for its own design.

## 1. Read-ahead

- A reader task reads messages from the connection (`JsonRpcConnection.ReadAsync`) into an unbounded
  `Channel`, as fast as they arrive, and completes the channel at end of input. Broken framing
  completes it too, recording the error.
- The loop waits for the first message (or for the midnight hint refresh, as today), then takes every
  message queued at that moment: a **batch**.
- End of input, or broken framing (logged as today), ends the session with exit code 1 once the
  messages read before it are handled.
- Only the loop writes to the connection.

## 2. Planning a batch: `LspBatch.Plan`

A pure function from a batch's messages to steps, in the messages' order, with two rewrites:

1. **Coalescing.** A run of consecutive `textDocument/didChange` notifications becomes one change step
   per document, in order of each document's first change in the run. A step holds all that
   document's content changes in order and the last version. A change is never merged across any other
   message (a request, `didOpen`, `didClose`, `didChangeWatchedFiles`), so every request sees the
   text the editor had when it sent it.
2. **Cancellation.** A request whose id a `$/cancelRequest` in the same batch names becomes a
   cancelled step. A cancel whose request isn't in the batch (already answered, or unknown) does
   nothing. The `$/cancelRequest` notifications themselves produce no steps.

Every other message is a step of its own, unchanged. `exit` keeps its place, and the steps after it
are not run.

## 3. Incremental sync

- `ServerCapabilities.textDocumentSync` is `2` (incremental).
- A `didChange` content change has an optional `range`:
  - **With a range:** it replaces that range of the current text. Positions are LSP's (0-based line,
    UTF-16 column), resolved through `LineMap`. A position past a line's end or the text's end is
    clamped to it.
  - **Without one:** it replaces the whole text.
- `TextEdits.Apply(string text, IReadOnlyList<TextChange> changes)` applies the changes in order;
  each one sees the text the previous ones produced.
- `NitrogenLanguageService.TextOf(uri)` returns the current text of an open document, C# host or
  open file of no served language; null otherwise.
- A change step computes the new text from `TextOf` and calls `Change(uri, version, text)` once. A
  change to a document that isn't open is logged and skipped.

## 4. Per batch

- The documents whose diagnostics the batch's changes, opens, closes and watched-file events affect
  are collected and published once each at the end of the batch. A closed document still gets its
  empty diagnostics when its `didClose` step runs, as today. A batch that reaches `exit` ends the session there, without
  publishing.
- Requests are answered at their place in the batch, after the steps before them.
- The inlay-hint refresh check (`workspace/inlayHint/refresh` when the languages change) runs once at
  the end of the batch, instead of after each notification.
- A cancelled step is answered with error `-32800` (RequestCancelled), message `"cancelled"`.

## 5. Editors

No change. VS Code and Rider both support sync kind 2 and send `$/cancelRequest`.

## 6. Tests

- **`TextEdits.Apply`:** an insert, a delete, a multi-line replace, several changes in order, a
  change without a range, a surrogate pair (UTF-16 columns), and a range past the end (clamped).
- **`LspBatch.Plan`:**
  - adjacent changes to one document merge, keeping every content change and the last version;
  - adjacent changes to two documents give one step each, in first-change order;
  - a request between two changes keeps them apart;
  - a request and its cancel give one cancelled step and no step for the cancel;
  - a cancel without its request gives no steps;
  - steps after `exit` are dropped.
- **Server, over stdio:**
  - `textDocumentSync` is 2 (the existing assertion of 1 changes);
  - ranged changes give the diagnostics of the edited text;
  - after a burst of changes, the last diagnostics published match the final text;
  - every request gets exactly one response: a result, or error `-32800`.
- **Service:** `TextOf` for an open document, a C# host, and a file that isn't open (null).

The measurement in Problem is rerun with the Release server; before and after go in the PR.

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
