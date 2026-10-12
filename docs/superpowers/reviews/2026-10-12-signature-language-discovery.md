# Signature discovery implementation review

Implemented on `codex/signature-language-design`, following the approved [design](../specs/2026-10-12-signature-language-discovery-design.md) and [13-task plan](../plans/2026-10-12-signature-language-discovery.md).

## Independent review

A fresh read-only whole-branch review covered `13d1136..b3bc22f`. It reported nine Important findings, no Critical or Minor findings, and no behavior declined for judgment. The implementation received one regression-driven fix pass; no second review was performed.

| Finding | Regression and resulting behavior |
| --- | --- |
| CLI/build cross-file references | Directory and registered-manifest checks share primary exports; packaged consumer verifies both a valid reference and an unresolved one. Regions remain independent. |
| External standalone region definitions | Host-aware region references load explicit local definitions and transitive helpers. Unregistered manifest inputs still fail. |
| Unsigned declared extensions | Open and closed documents receive checks and navigation. Generated client ownership remains respected. |
| Razor directive strings | Attribute/page strings, including multiline arguments, do not activate region analysis. |
| Distinct augmentation diagnostics | Identical diagnostics from independent languages retain both source identities. |
| Reload lifetime | Repeated reloads keep project/semantic caches bounded; retired collectible load contexts become unreachable. |
| Untitled primary documents | Navigation and checks work; relative paths need one workspace root or a save-to-establish-location diagnostic. |
| Client/server header recognition | Block-comment prose before signatures works with LF, CRLF and CR. |
| Cached compilation warnings | Warnings survive repeat analysis and unrelated editor changes. |

Each finding had a failing regression before its fix. Additional regressions protect generated ownership and invalid start diagnostics.

## Acceptance evidence

- Full .NET suite: **1,483 passed**, no failures or skipped tests.
- VS Code client: **22 tests passed**; main and freshly generated extensions compiled.
- Fresh local packages: legacy/runtime/tool consumer and signed native build consumer passed; cross-file exports, mapped checks, helper/config reload, registered-input boundaries, quoted analysis filenames and disable/unconfigured modes exercised.
- Live isolated VS Code: signed `.txt` navigation, unsigned declared-extension navigation, untitled navigation, prose-prefixed signatures, one set of inlay hints with main/generated clients, name add/rename/delete, multi-folder ambiguity add/remove, grammar/helper reload, signature removal, native language IDs, and native C# diagnostics alongside Nitrogen passed.
- Live isolated Razor project: `aspnetcorerazor` retained, with simultaneous mapped Nitrogen `NC1001` and native `CS1525` diagnostics.

All live fixtures and profiles were isolated under temporary directories. Unrelated DateCalc editor settings were preserved. This work does not publish packages, create a PR or merge the branch. Native syntax trees and semantic model providers remain future work; current augmentation analyzes source text.
