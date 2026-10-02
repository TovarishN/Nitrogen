# Declarative templates: `lowers template` and `lowers expand`

Status: in implementation (2026-10-02). Replaces Geometry's hand-written `GeometryDefinitionExpander`.

## Problem

A language with named, parameterized definitions (Geometry's `def cube(w: Scalar, …) = box w h d;`
and `make cube(1, 2, 3);`) needs C# to lower a call: find the definition across files, lower its body,
substitute the call's arguments for parameter references, keep both origins, and stop cycles. Every such
language would repeat this code, and a `nitrogen.json` plugin language would have to ship it as source.

## Clauses

```ngr
syntax Definition = "def" Name:Identifier "(" Params:(Parameter; ",")* ")" "=" Body:Shape
  declares shape Name export scope lowers template Body(Params);
syntax Parameter  = Name:Identifier ":" Type:Identifier declares parameter Name type Type;
syntax Make       = "make" Name:Identifier "(" Args:(Dimension; ",")* ")" ";"
  references shape Name lowers expand Name(Args);
```

- `lowers template Body(Params)` marks a declaring rule as a template. `Body` labels one element;
  `Params` labels a repeated or separated list whose items each declare a typed symbol. The body is
  never lowered as a root; it is lowered only through an expansion.
- `lowers expand Name(Args)` lowers a call. `Name` is a field the same rule `references`; the symbol it
  resolves to must be declared by a template rule. `Args` labels a repeated or separated list.

## Semantics

- **Type.** An expansion has its template body's type. A cycle in typing yields `Core.Error` (no cascade).
- **Checks** (semantic diagnostics, in the calling file): `NT0007` the callee is not a template, at the
  name; `NT0008` argument count differs from the parameter count, at the name; `NT0001`/`NT0004` an
  argument's type differs from its parameter's declared type, or it has none. A parameter without a
  declared type leaves its argument unchecked.
- **Lowering.** Arguments are lowered in the caller. The body is lowered in the template's file with each
  parameter symbol bound to its argument; a parameter reference becomes the argument, rewritten with the
  reference's origin first (`HirTraversal.Rewrite`). The result's origins start with the call's origin.
  Parameters are matched by identity and position, never by name.
- **Blocked expansion** reports in the template's file, within the template only (a broken sibling does
  not block): `NH0001` recovered syntax, `NH0002` an unresolved name, `NH0003` a binding or semantic
  error. `NH0007` a cyclic expansion, at the name of the call that closes the cycle.
- Templates of other files are read through `FileSemantics.RelatedFile`, so project edits invalidate
  expansions as before.

## Geometry

`Geometry.ngr` uses the two clauses, and `TemplateBox` (whose dimensions may be parameter references)
lowers to `Geometry.BoxMesh` like the top-level `Box`, which keeps taking numbers only.
`GeometryDefinitionExpander` is deleted. `GD0001` (box parameters must be
Scalar) stays; `GD0002` → `NH0002`, `GD0003` → `NT0008`, `GD0004` → `NH0007`, all at the same spans.
Gravity's geometry tests that pin those three codes change accordingly.

## Not included

Overloads, default or named arguments, recursion with a base case, and templates whose body is not a
single element.
