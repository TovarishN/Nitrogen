# 251 — Nitrogen declarative typing and lowering

Status: Implemented; awaiting review

Close roadmap gap 1: `.ngr` clauses type grammar nodes by catalog `SemanticType` and lower them to typed HIR, for trusted modules and admitted packages alike, without authored C#. The trusted Geometry module migrates to the clauses, and admission gains an end-to-end test in this repository.

Branch: `codex/251-declarative-typing-lowering` from standalone `main` at `c579dee`.

Spec: [2026-09-27-declarative-typing-lowering-design.md](../docs/superpowers/specs/2026-09-27-declarative-typing-lowering-design.md)

Plan: [2026-09-28-declarative-typing-lowering.md](../docs/superpowers/plans/2026-09-28-declarative-typing-lowering.md)

## Log

- 2026-09-27 — User chose to make typing available to admitted packages and to include declarative lowering to granted operations. Approved declarative `.ngr` clauses interpreted by a generic runtime component, the component split, diagnostics, Geometry migration, and test plan in chat. Written design prepared for review; implementation has not started.
- 2026-09-28 — User approved the spec and a ten-task test-first plan. The plan refined the spec, which was updated to match: the clauses are `BindingClause` kinds, the generated table is `SyntaxModule.DeclarativeRules`, a literal may read `this` (Geometry keeps its `Sign` field), and `ParameterRef`'s string check is removed because `Parameter`'s `GD0001` already rejects non-`Scalar` parameters.
- 2026-09-28 — User chose inline execution in a separate worktree. Implemented tasks 1–9: both parsers and the validator accept the clauses; the generator emits `DeclarativeRules`; `LanguageBuilder` resolves them (`NM0008`–`NM0010`) and registers lowerers; `DeclarativeTypes` reports `NT0001`–`NT0004` and lowers to HIR with origins; Geometry uses the clauses and `GeometryHirLowerer` is removed with unchanged HIR and hover; admission rejects a lowered operation that was not requested (`NA0006`). Each step started with a failing test except the documented characterization tests. Binding-table emission now ignores lowers-only clauses, which would otherwise have marked the kind dynamic.
- 2026-09-28 — Task 9 exposed a gap in the plan: the workspace, language server, and CLI `parse` build languages without semantic modules, so strict resolution rejected every `lowers` clause there. The user chose to leave clauses dormant when a language has no semantic modules; resolution stays strict once any semantic module is supplied. A regression test covers this.
- 2026-09-28 — Verification: `dotnet build Nitrogen.slnx -warnaserror --no-incremental` succeeded with no warnings; `Nitrogen.Tests` passed 677/677 (624 before this issue); `git diff --check c579dee` is clean. No merge or push has been performed.
