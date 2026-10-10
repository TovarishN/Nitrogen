# Quasiquoted rewrite rules

Status: approved (2026-10-10).

## Problem

Nitrogen languages can add syntax only by writing its lowering: a `lowers` clause naming a catalog
operation, a template the user declares (`lowers template` / `lowers expand`), or C#. A language
author who wants a convenience form (`3 fortnights`, `tomorrow(d)`, `unless c b`) has to give it its own
operation, types and handlers, even when it means nothing more than code the language already has.

Nemerle solves this with quasiquotation: `<[ … ]>` quotes code as a syntax tree, `$x` splices a tree
in, and a macro is a rule whose meaning is its expansion. This spec brings the core of that to
Nitrogen: parsing quoted syntax with typed holes (part 1), and grammar rules that define their meaning
by a quoted expansion (part 2).

## Goal

A grammar rule can say `rewrites <[ template ]>`: its uses mean the template, in the language's own
syntax, with each `$Field` hole filled by the use's child. Uses are checked, typed and lowered as their
expansion, while the user's document, its binding and every editor feature stay as they are.

Out of scope (later steps):
- list splices (`..$Items`) and holes standing for tokens;
- templates that declare names (and hygiene for them);
- expanding before binding (an expanded document with source mapping);
- quotation in C# helper code and inside Nitrogen languages.

## 1. Grammar syntax

```
extensible syntax Expr
{
  | Fortnights = Count:Expr "fortnights" precedence 30
      rewrites <[ $Count * 2 weeks ]>
}
syntax Tomorrow = "tomorrow" "(" Date:Expr ")" rewrites <[ $Date + 1 days ]>;
```

- **The clause.** `rewrites <[ text ]>` stands where a `lowers …` clause stands, after the rule's
  `declares`/`references` clauses; a rule has one of `lowers` and `rewrites`, not both. The text runs
  from after `<[` to the first `]>` that closes it, may span lines, and is kept verbatim.
- **The template** is written in the language being defined. It must parse as the rule's **category**:
  for an alternative of an extensible rule, that rule (an `Expr`); for a plain rule, the rule itself.
- **Holes.** `$Name` is a hole for the rule's field `Name`. The field must be a syntax rule or an
  extensible rule (a nonterminal); the hole stands for one node of that nonterminal. Each field may
  appear any number of times, including not at all.
- **Both `.ngr` parsers** accept the clause: the compiler's (`Nitrogen.Grammar`) and the self-hosted
  grammar (`Nitrogen.Ngr/Nitrogen.ngr`) that serves `.ngr` files in editors, which colors the template
  as a string-like region.

## 2. Parsing with holes

- **API.** `Language.ParseTemplate(string text, Rule start, IReadOnlyDictionary<string, int> holes)`
  parses `text` as `start`, where each `$Name` in `holes` stands for a node of the given nonterminal kind.
  The result is a `ParseResult` whose tree has a **`Hole`** node (a new built-in kind,
  `SyntaxKinds.Hole`) for each hole: a leaf spanning `$Name`.
- **Matching.** At the entry of a nonterminal (a generated plain rule, and `ParseExtensible` for an
  extensible one), when a template parse is in progress and the input at the position (after trivia) is
  `$` followed by a name in `holes` whose kind is that nonterminal, the call succeeds with a `Hole` node
  consuming `$Name`. A hole of another kind doesn't match there, so the parse tries other alternatives
  as usual.
- **Atomic.** A hole is a complete operand: for an extensible rule it is a prefix result, and the Pratt
  loop may extend past it (`$Count * 2`) but never into it. `!$Cond` with `$Cond` standing for `a + b`
  means `!(a + b)`, because the hole is one node.
- **Cost.** The check is guarded by a field of `ParserState` that is set only by `ParseTemplate`; a
  normal parse never reads further than that field.
- **Generated code.** Each generated plain rule's parse function gains one first statement,
  `if (s.Holes is not null && s.TryHole(Kind)) return true;`, emitted by the grammar code writer.

## 3. Grammar compile

When a language compiles (`GrammarWorkspace`, and the generator for compiled grammars), for each rule
with `rewrites`:

1. Its fields are resolved; each hole name must be a field of the rule whose type is a nonterminal.
   **NGR0135**: an unknown hole (`'$Name' is not a field of Fortnights`). **NGR0136**: a hole for a field
   that isn't a nonterminal.
2. The template is parsed with `ParseTemplate` as the rule's category. **NGR0137**: it doesn't parse
   (the parser's message, at the template). The parse must consume the whole template.
3. The template is bound in a project of its own, with the language's built-ins. **NGR0138**: it
   declares a name. References that don't resolve are left to each use (they report there).
4. The parsed template is kept on the language: `Language.RewriteFor(int kind)` returns a
   `RewriteTemplate` (the rule kind, the text, the frozen tree, and each hole node's field name) or null.

The diagnostics are reported in the grammar file at the template, like other NGR diagnostics.

## 4. A use: binding, types, checks and lowering

- **Binding.** The user's document is bound as today. The macro rule's own `declares`, `references`
  and `scope` apply to the use; the template is not bound into the user's project.
- **Types.** A macro use's type is the type of its template's root, computed in a **per-use
  instantiation** of the template: the template's tree with its semantics, where each `Hole` node's type
  is the type of the use's child for that field. An untyped child leaves the hole untyped, as an
  untyped argument does today.
- **Checks.** The template's semantic checks run in the instantiation. A diagnostic from it is reported
  at the use, with the template's message prefixed: `in the expansion of Fortnights: …`, code unchanged.
  A child's own diagnostics stay where they are.
- **Lowering.** Lowering a use lowers its instantiation, with each `Hole` node lowering to the use's
  child, lowered in the user's document. HIR produced by template nodes carries the use's origin first;
  HIR from a hole is the child's own, with its origins. So hover, inspection and statement values point
  at the user's text.
- **Nesting.** A template may use other rules with `rewrites`; their uses inside the template expand in
  turn. A cycle (a rewrite reaching its own rule again) reports **NH0008** at the use and blocks lowering.
- **Cost.** An instantiation is made when a use is typed or lowered, and kept with the use's
  `FileSemantics` for that version.

## 5. Editors

No editor change: coloring, folding, outline, navigation and diagnostics run on the user's document. One
addition: hover on a macro use (the node's own text, outside its children) appends
`rewrites <[ template ]>`.

## 6. Tests

- **Holes (part 1):** a hole parses as its nonterminal and as nothing else; `$Count * 2` extends past a
  hole; with `-$X` / `!$X` a hole is one operand; an unknown `$Name` is plain text (and fails as the
  grammar decides); a normal parse never matches `$Name`.
- **Compile:** NGR0135 to NGR0138 each at the template; a valid template is kept (`RewriteFor`).
- **Uses**, in a test grammar extending DateCalc's syntax with `fortnights` and `tomorrow(…)`:
  - `3 fortnights` lowers to the same HIR as `3 * 2 weeks`, and has type `Duration`;
  - `tomorrow(2026-10-05)` has the value `2026-10-06 Tue` (statement values work);
  - `tomorrow(3)` reports the template's type error at the use, prefixed `in the expansion of Tomorrow`;
  - a child's error stays at the child;
  - a rewrite using another rewrite expands both; a cycle reports NH0008;
  - hover on a use shows its type and `rewrites <[ … ]>`.
- **Editor-facing:** the existing suites pass unchanged (no behavior change without `rewrites`).

Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`.
