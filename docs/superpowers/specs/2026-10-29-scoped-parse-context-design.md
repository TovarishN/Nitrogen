# Scoped grammar parsing context for CSharp5

Status: user approved the concrete production design and instructed implementation on2026-10-29. Independent builder check found no important unanswered planning questions. Separate code review found no blocking issues for draft publication; experiment evidence and deferred minor are recorded in prototypes/nitrogen-context/README.md. Internal throwaway runtime experiment authorized. The additive contracts below are approved for implementation; breaking API or tree-owner changes remain subject to a separate concrete decision.

## Purpose and boundaries

Make grammar recognition depend on an explicitly scoped Boolean environment without changing the syntax tree it builds. The immediate consumer is CSharp5: ordinary await(M()) must retain InvocationExpression, while an async owner gives AwaitExpression with operand (M()). Preserve existing generated Parameters/Body/signature types and original UTF16 coordinates. Source input is never rewritten. Roslyn is observation evidence only, never the implementation.

The user selected the scoped Nitrogen design/prototype route in 2026-10-23-csharp5-await-context-decision.md, confirmed 2026-10-29. Unicode4 scalar continuity is already approved. Production CSharp5 remains partial, and caller substitution, task/awaiter typing, lowering and execution remain separate work.

The existing Nitrogen tree-pattern ancestor-context draft is unrelated: it inspects an already parsed tree and cannot correct a parse-time Invocation/Await owner. Preserve that draft and the Nitride draft in the original checkout.

## Runtime contract

Every parse invocation starts with all context flags false. Context state belongs to ParserState, never static/thread-local/global state. Context declarations are module-qualified Boolean names, assigned collision-free bits by the composed Language. Reject more than64 flags in the initial implementation; never silently alias bits. Same-language snapshots keep stable identities; reuse across different Language snapshots remains prohibited.

A scoped operation takes a mask, replacement values and a rule function. It replaces only those bits, invokes the rule, and restores the complete caller value on success, failure or exception. Nested scopes may override or inherit bits. Predicates read bits without consuming input or producing tree nodes. Scope wrappers likewise contribute no node, token or capture slot. Generated rules retain their existing public shapes. Generated callers must use scoped operations, never expose writable context state. Existing public ParseMark constructor/deconstruction/equality must not change; lexical restoration is guaranteed by the scoped call's finally, including speculative lookahead and failed alternatives.

Memo lookup includes the full environment with rule/position/precedence/recovery-call flags. Failed entries and successful entries are isolated alike. Hash collisions must compare the complete identity. A result computed under true must never satisfy a false lookup at the same source position. This may enlarge entries from24 to32 bytes; measure the cost before production and keep default-context performance visible.

Incremental reuse records include the full **incoming** environment for the rule call. Find requires rule, mapped position and environment equality before creating a graft; text examined-range checks still apply. Preserve each descendant's own environment when carrying records through grafts, not the enclosing graft's environment. A scope's deterministic internal changes are part of the rule implementation, so unchanged parent results remain safe when their incoming environment and examined text match. A rule's context must be balanced on return, including exceptions. Unchanged records at other contexts remain reusable; do not clear all memo/reuse data as a substitute for correctness.

## Grammar/compiler contract (proposed surface)

Add module-qualified Boolean context declarations and three transparent grammar expression operations: test a flag, parse a child with one or more flags true, and parse a child with one or more flags false. The exact keyword spelling and diagnostics are left to implementation; these are grammar operations, not C# callbacks or semantic property evaluation. No user-authored side effects, capture-dependent assignments, stack introspection or arbitrary predicates.

Binding resolves names against explicitly selected modules. Context-only operations are nullable and zero-width: they must not disguise non-consuming recursion/repetition. Existing FIRST-set, commit/recovery, token-versus-syntax and capture analyses must see through scope wrappers. Preserve typed child identity through the wrapper. Generated incremental entry/exit calls record the incoming environment. Grammar source spans and navigation for context declarations/references need the same binding identity as runtime composition. No IDE language is introduced by the runtime-only experiment.

## CSharp integration direction

Use one lexical flag for recognition of raw await. Async method/lambda/anonymous-method entry enables it for that parse branch; nested ordinary anonymous functions inherit lexical recognition. Source-local nearest-function legality is checked separately: the approved reference witness has AwaitExpression inside a non-async delegate nested in an async method, even though source4431 prohibits that use. Do not reset the lexical flag at each ordinary anonymous owner.

Use transparent async-entry branches around existing method/lambda/anonymous rules, guarded by their authored async header. Do not add context-setting side effects to optional modifier parsing: speculative alternatives must not affect each other. Whether these branches can retain existing generated owners as well as Parameters/Body must be proved against the real30-module CSharp4 baseline before production; if they cannot, return for a concrete tree/API decision. The runtime spike does not establish this grammar integration.

Separate raw keyword spelling from the existing generic Contextual helper. The reference keeps @await and Unicode-escaped await invocations as identifiers even within async functions. Async spelling variants, formatting characters, raw await identifier rejection, suffix boundaries, query restrictions and every entry scope require source/reference witnesses before production. Outside lexical async context ordinary await identifiers remain available. Function/use legality, catch/finally/lock/unsafe and ref/out restrictions belong to separately tested source-local policy, never a whole-file switch.

Standalone parsing/quotation starts with false flags. Parsing a non-default category requires an explicitly scoped start rule authored in its grammar; this slice adds no ambient context parameter to Language.Parse/CompileQuote. Do not claim detached await quotes work until that entry rule exists.

## Authorized throwaway experiment

Use an isolated clone of current Nitrogen ed69ac8a; the primary sibling contains unrelated untracked drafts. The app worktree tool does not support this sibling as sourceRoot, so the clone is under /tmp. Add internal scoped runtime operations and environment-aware memo/reuse records only. A small generic test grammar with a marker selecting a context and an identical payload exercises real ExtensibleRules, Language.ParseIncremental and Language.Reparse. This synthetic grammar is not a C# parser and cannot establish CSharp5 acceptance. No public API, grammar parser, generator, installed tool or dependency pin changes.

Retain a reproducible patch and results with the CSNitrogen design. The experiment may prove a mechanism worth planning, but it is not production implementation and must not be merged into Nitrogen as such.

## Acceptance before production

- Same-position context-sensitive positive and negative memo entries, nested enable/disable/inheritance, failed alternative rollback, lookahead and exception restoration.
- Full versus incremental trees/diagnostics/spans across context-changing header edits, including equal-length replacements, multiple reparses, moved grafts and carried descendant records.
- Strict/recovery passes and concurrent parses show no state leak; malformed input cannot strand a scope.
- Actual CSharp5 all eight paired owners and exact operand (M()), escaped/verbatim spellings and supplementary/escaped suffixes. Preserve original UTF16 spans and all original typed fields, including independently omitted modules (the two deferred proof gaps).
- Grammar extraction/binding/generation, invalid flag names/capacity, nullable/recursion/FIRST/recovery and selected-factory module omission fail closed.
- Repinned dependency source and all CSharp1–4 admission checks pass, with full solution tests. No version/profile admission from the experiment.
- Measure default-context and active-context allocation/throughput/memo/reuse on representative CSharp and existing Nitrogen grammars before accepting the entry-size cost. No performance claim without measurement.

## Remaining production approval

The user approved this concrete additive design after the experiment and builder check. Execute the production implementation plan. Any public property-type change, altered tree owner contract or breaking core API returns separately for approval. There is no approval request for the already authorized internal throwaway experiment.
