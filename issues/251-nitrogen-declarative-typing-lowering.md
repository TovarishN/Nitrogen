# 251 — Nitrogen declarative typing and lowering

Status: Design written; awaiting review

Close roadmap gap 1: `.ngr` clauses type grammar nodes by catalog `SemanticType` and lower them to typed HIR, for trusted modules and admitted packages alike, without authored C#. The trusted Geometry module migrates to the clauses, and admission gains an end-to-end test in this repository.

Branch: `codex/251-declarative-typing-lowering` from standalone `main` at `c579dee`.

Spec: [2026-09-27-declarative-typing-lowering-design.md](../docs/superpowers/specs/2026-09-27-declarative-typing-lowering-design.md)

## Log

- 2026-09-27 — User chose to make typing available to admitted packages and to include declarative lowering to granted operations. Approved declarative `.ngr` clauses interpreted by a generic runtime component, the component split, diagnostics, Geometry migration, and test plan in chat. Written design prepared for review; implementation has not started.
