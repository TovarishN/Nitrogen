# Extract grammar definitions to a reusable file

Status: implemented and verified, including live VS Code application and undo. Prepared for the extraction PR and 0.12.0 release.

## Intent and agreed scope

Select complete grammar rules or alternatives and move them into a separate `.ngr`
file for reuse. Move selected definitions only; leave their dependencies in place.
Preserve grammar behavior, editor navigation, binding, checks and lowering. Quote
functions, syntax-pattern matching, arbitrary expression fragments, and automatic
movement of dependencies are separate future work.

## Existing implementation

Nitrogen already composes syntax modules, resolves qualified rule references and
supports `extend syntax Module.Rule`. Grammar declarations carry source spans.
The LSP currently advertises only `quickfix`; its workspace edits contain text changes
and do not yet describe file creation. VS Code needs a small client command to collect
an extraction destination. The quotation API builds trees but is not needed here.

## Selected approach and alternatives

Create a new syntax module in a new file using existing composition and extension
mechanisms. Whole rules become definitions in that module; moved alternatives become
an `extend syntax` block targeting their original extensible rule. Rewrite affected
resolved grammar references, preserving their referents through qualification.

A textual include could preserve the original module ownership, but would introduce
new language and build semantics. Splitting a module across files likewise needs an
explicit partial-module contract. Neither is assumed or introduced in this version.
Moving into an existing file is deferred to keep collision and insertion handling
bounded. Existing modules can reference the extracted module once composed into the
language; extraction does not itself publish a standalone package.

## User flow

1. Select one or more contiguous, complete definitions of the same extraction shape:
   top-level rules in one module, or sibling alternatives in one extensible/extend block.
2. Invoke **Extract Block…**, exposed as `refactor.extract` for grammar files.
3. Choose a new workspace `.ngr` path and a unique module name, with editable defaults.
4. Review source removal, destination creation, resolved reference updates and required
   configuration changes in a refactoring preview.
5. Choose **Apply all changes** in the review notification to apply the complete workspace edit; normal workspace invalidation refreshes language services.

The source module remains, even if it becomes an empty shell, when retaining it is
needed for composition references. Shared attributes and extension-point declarations
remain with their original owner. V1 does not operate inside embedded strings or quotes.

## Selection and source preservation

Use parsed declarations and exact source ranges, not regular expressions. Reject
partial rules, mixed rule/alternative selections, selections crossing module boundaries,
and parse-recovered declarations. Extend-block alternatives are supported as well as
alternatives declared directly on an extensible rule.

Keep declaration text, semantic blocks, lowering clauses, precedence, associativity,
line endings and internal comments. Include a directly attached leading comment with
its definition; leave ambiguous inter-definition comments outside the move. Never
consume unrelated surrounding text. Validate destination text after wrapping the moved
selection in its new module or extension block.

## Dependencies, identity and safety

Resolve each dependency before producing edits: grammar rules and tokens, imported
names, symbol kinds and properties, semantic attributes, helper types and operations.
Use imports and qualified references to the existing definitions. Do not duplicate or
move dependencies implicitly. Require the destination to participate in each affected
language composition; update explicit grammar file lists without redundant additions
when existing discovery/globs already cover it.

Moving definitions changes module ownership and potentially generated C# type/kind
names. Do not promise binary or generated API identity preservation. Analyze supported
references and compile the proposed workspace in memory, including configured helper
sources. If generated C# consumers cannot be updated reliably, disable extraction with
an explanation instead of silently breaking them. External consumers outside the known
workspace cannot be proven safe: list that limit in the preview.

Reject name collisions, unresolved dependencies, unsupported composition cycles,
invalid start-rule changes, broken helper compilation and changes to extension semantics.
If whole-rule movement requires changing a configured start reference, include that edit.
Keep the selected alternatives' relative order and preserve the ordering constraints of
composed alternatives; block extraction if existing composition cannot express them.
Do not run evaluation handlers or arbitrary user operations during validation.

## Edit architecture

The language service owns selection analysis and constructs an extraction proposal from
current open buffers plus the workspace snapshot. It returns stable symbol identities,
dependencies, proposed files and edits, and unavailable reasons. VS Code collects names
and paths; language semantics stay in the server so another client can reuse the API.

Add refactor-aware code actions and an explicit prepare/resolve command flow for user
input. Extend LSP workspace edits to versioned document edits and `CreateFile`; respect
client support. Destination creation must fail rather than overwrite an existing file.
Revalidate document versions and destination existence immediately before producing the
final edit. A stale proposal is recomputed or refused. Use the editor's read-only changes view followed by complete workspace-edit
application and undo facilities; do not write files directly from the server.

Clients that cannot preview/apply the required file operations receive an unavailable
reason. Applying an edit is not claimed to be transactional across arbitrary clients;
verify VS Code's application and undo behavior as part of acceptance.

## Validation and acceptance

- Extract a token, ordinary syntax rule, whole extensible rule, or multiple complete rules.
- Extract alternatives from both extensible declarations and extension blocks.
- Test qualified/unqualified and cross-file references, semantic attributes, lowering,
  symbol properties, helper references, and configured language start rules.
- Test multi-language grammar lists and discovery without accidentally enabling new rules
  in an unrelated language composition.
- Compare representative programs before/after: parse structure modulo expected owner
  renaming, binding targets, diagnostic meaning, precedence and lowered operations.
- Preserve original definition locations through updated navigation; references from
  other files must reach the new file. Cover unsaved buffers and source spans.
- Negative cases: partial selection, recovery, collisions, unsupported dependency cycles,
  helper API breakage, stale buffers, existing destination and unsupported client features.
- Verify complete source/config/destination preview, application, undo and live language
  refresh in VS Code. No LSP restart is required after applying extraction.
- Run focused service/compiler/LSP/client tests, then repository build and full tests.

## Future extension

Parameterized syntax functions can later extract quote bodies with typed scalar/list
inputs. Structural quasiquote patterns can support matching and replacing equivalent
occurrences. Neither is a prerequisite for this refactoring. Declaration-producing
functions would additionally require an explicit hygiene and binding contract.

## Implementation boundary

Implement only after this written design is reviewed and an implementation plan is
approved. Keep the uncommitted Units demo and unrelated DateCalc editor settings outside
any extraction-specific commit or PR.

## Verified implementation boundaries

Native review initially allowed individually deselecting changes. The implemented flow
uses the documented `vscode.changes` view with immutable virtual documents, then an
explicit **Apply all changes** notification. The server revalidates its entire snapshot
after review, and the client checks edited document text/versions immediately before
application. Destination creation still refuses overwrite; arbitrary filesystem failure
is reported, not claimed to be transactional.

Ordinary rewrites whose inferred quotation category changes are refused. Qualified
rule references participate in definition/reference navigation; rename is refused when
qualified uses exist until a complete grammar rename implementation is available.
Configured helpers are compiled without execution, and unsupported generated API
consumers are refused. Quote functions and structural matching remain future work.
