# List splices and C# syntax quotation

Status: implemented and verified (2026-10-11). Build: zero warnings/errors; full suite: 1,307 passed. Independent review completed with no remaining material findings. Live Rider behavior has not been verified for this extension.

## Intent and delivery order

Extend Nitrogen's existing quasiquotation with both capabilities requested by the user:

1. List splices (`..$Args`) in grammar rewrite templates.
2. Quotation through a normal C# API, returning reusable syntax trees that callers can bind,
   check, and lower through Nitrogen.

Both use the existing generated parser and `Language.ParseTemplate` machinery. Deliver and
verify the list-splice slice before adding the C# API. Preserve the currently implemented
scalar holes, rewrite semantics, diagnostics, and grammar editor navigation/completion.

The user selected both capabilities, starting with lists, and selected a syntax-tree C# API.
The user approved the detailed contracts below on 2026-10-11.

## Existing constraints

- `ParserState.TryHole` recognizes a scalar hole at a nonterminal boundary.
- Repetitions and separated lists are emitted by `SyntaxCodeWriter`; separated list children
  alternate between items and separators. Generated views and declarative lowering rely on this.
- `RewriteHole` currently records a field, its child index, and its nonterminal category.
- `FileSemantics` forwards scalar-hole properties and lowering to the caller's original child.
- `SyntaxTree` owns pooled arrays and one text buffer. Node spans must index that buffer; arrays
  and node indices cannot simply be borrowed from another disposable tree.
- Rewrite templates currently reject declarations, and are bound independently of the caller.
  This extension preserves that policy.

## Approach and alternatives

Use typed template placeholders and structural instantiation. Extend the list parser sites to
recognize list placeholders, and use one instantiator for the resulting list structure and the
C# construction API. Ordinary parsing never enables placeholders.

Text replacement followed by reparsing would be simpler, but would lose atomic scalar-splice
semantics and original source identity. Maintaining an unrelated C# tree builder would duplicate
category checks, list/separator behavior, and parser rules. Neither alternative is selected.

## Stage 1: list splices

### Surface syntax and category rules

Example for a language with a `Call` expression:

```text
| Forward = "forward" "(" Args:(Expr; ",")* ")"
    rewrites <[ target(..$Args) ]>
```

`$Value` continues to mean one node. `..$Args` means the ordered elements of a list-valued field.
The marker is accepted only at a repeated-list item boundary whose element nonterminal matches
its declared category. It cannot stand for one expression outside a list.

Initial supported list fields and destinations:

- `Expr*` and `Expr+`, with a direct syntax/extensible-rule reference as the repeated element;
- `(Expr; ",")*` and `(Expr; ",")+`, with a literal separator;
- imported, qualified, and implicit extension-module category resolution, using the compiler's
  existing grammar resolver.

Optional fields, token elements, grouped tuple elements, and nonliteral separators receive a
specific unsupported-shape diagnostic. They are outside this first slice rather than silently
flattened. A scalar field cannot be used as a list splice, and a list field cannot be used as a
scalar hole.

Templates may contain literal elements, several splices, or the same splice repeatedly:

```text
target(1, ..$Args, 2)
target(..$Left, ..$Right)
target(..$Args, ..$Args)
```

### List structure and separators

A list marker is one placeholder in the parsed template, regardless of its eventual item count.
At instantiation, flatten each marker into zero or more item slots. Then rebuild the destination
list with exactly one destination separator between adjacent surviving items. Remove obsolete
boundary separators when a splice is empty. Separators from a source list are not transplanted
into a different destination list.

An empty result is valid for `*`; an empty result for `+` is an instantiation diagnostic. A `+`
destination containing fixed elements remains valid even when every splice is empty.

Within inserted item subtrees, preserve internal syntax and trivia. Destination separators and
inter-item spacing use the template's separator spelling and a deterministic formatting policy;
this feature does not promise byte-for-byte preservation of source list delimiter trivia.

### Runtime representation and rewrite semantics

Introduce explicit scalar/list hole metadata while preserving the existing scalar ParseTemplate
API as a compatibility overload. Add a distinct list-hole node kind. List matching is emitted at
list item sites, not as an alternative ordinary nonterminal parse.

The shared instantiator accepts typed node/list bindings and produces a fresh list structure.
For grammar rewrites, each inserted item remains a scalar semantic proxy to the original caller
node. Existing property forwarding, type checking, and HIR lowering then operate on individual
items. Generated list views and stride-based lowering must see normal list item/separator layout,
never an unexpanded list-hole node.

Repeated occurrences create separate proxy positions but refer to the same original source item;
existing lazy evaluation and child diagnostics must not be duplicated merely because a splice is
written twice. An inserted expression remains atomic, preserving its parsed precedence.

Keep declaration binding and the existing rewrite-cycle guard. List expansion cannot bypass the
cycle guard or recursively consume a list placeholder as ordinary program text.

### Errors and editor behavior

Validate unknown fields, wrong scalar/list shape, unsupported list shape, and category mismatches.
Grammar-only errors are compile errors at the marker; composed-template parse problems keep the
existing workspace warning / use-site NH0009 behavior. Empty `+` results are reported at the
rewrite use, without crashing or indexing nonexistent children. Assign new diagnostic codes in
the implementation plan after checking the current registry.

The grammar editor supports definition/declaration, hover, completion, and quote usages on
`..$Args`; the target is the enclosing `Args` field. Offer list-valued fields after `..$`, scalar
fields after `$`, and grammar keywords at their applicable template positions. String/comment
contents do not become holes. Edits use current buffers and correct source offsets.

## Stage 2: C# quotation API

### Proposed public surface

The following is the proposed API shape, to be finalized by the implementation plan without
changing these contracts:

```csharp
using var compiled = language.CompileQuote(
    "Calls.Expr",
    """target(..$Args, $Value)""",
    new Dictionary<string, QuoteHole>
    {
        ["Args"] = QuoteHole.List("Calls.Expr"),
        ["Value"] = QuoteHole.Node("Calls.Expr"),
    });

if (!compiled.Success)
    return; // Report compiled.Diagnostics to the caller.

using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
{
    ["Args"] = QuoteArgument.List(argumentNodes),
    ["Value"] = QuoteArgument.Node(valueNode),
});

if (result.Success)
    project.Set("generated.calls", result.Tree!);
```

`CompileQuote` returns a disposable compilation result with `Success`, `Diagnostics`, and a
nullable `Template`. The successful result owns the parsed template storage; its Template remains
usable until that compilation result is disposed. Instantiate returns a separately disposable
result with `Success`, `Diagnostics`, a nullable `Tree`, and source origins. Instantiation results
remain valid after the template's compilation result is disposed.

A node argument identifies a source tree and node index, plus an optional source identity for
origin tracking. A list argument is an ordered sequence of such references, including an empty
sequence. Callers may obtain these from existing generated syntax views or a parsed tree.
No C# compiler preprocessing or special `<[ ... ]>` C# syntax is required.

### Validation and ownership

`CompileQuote` parses once against an explicit composed language and category. It reports malformed
templates, unknown holes, incompatible placement, and declarations in the template. A template
can be instantiated repeatedly with different arguments. Each instantiation validates missing,
extra, wrong-shaped, disposed, and incompatible-category arguments before producing a tree.
Invalid user input returns diagnostics and no partial tree; a failed compilation has no Template.
Standard argument exceptions remain
appropriate for programmer errors such as null arguments.

Category compatibility is checked from the language's grammar information, including aliases and
extensible alternatives. A caller-supplied category label alone is not proof that a node matches.
Trees from incompatible language snapshots are rejected; sharing the same module/rule spelling
is insufficient. Recovery/error nodes are rejected in this initial construction API.

The result owns a new immutable SyntaxTree and its origin map. It contains no unresolved hole
nodes and does not borrow pooled storage. Input trees must remain alive during Instantiate, but
may be disposed afterward without invalidating the result. Results are independently disposable;
disposing one does not invalidate its template, input trees, or another result.

The result owns a synthesized text buffer. Every node span is valid in that buffer and GetText
works for inserted subtrees. Copy syntax structurally rather than concatenating input expressions
and reparsing: inserted expression precedence and node structure are authoritative. Exact input
formatting and parse/print round trips are not part of this first API contract.

Keep separate origin metadata mapping constructed nodes to the template or original input node.
Synthetic buffer offsets must never be presented as locations in an input document. Provide an
explicit binding/lowering integration path that maps diagnostics and HIR origins through that
metadata; ordinary consumers using just result.Tree use the generated-document path and offsets.

### Binding and checking

C# quotation constructs syntax; it does not execute helpers or operations. The caller explicitly
adds the result to a Nitrogen binding project, then runs binding, semantic checks, and lowering.
Inserted references bind in that destination project. They do not retain hidden binding state from
an unrelated input project. Cross-project symbol capture and declaration hygiene are excluded.

Grammar rewrites continue using caller-node semantic proxies as described in Stage 1. The C#
result instead materializes ordinary syntax for fresh binding. Both modes share template parsing,
category validation, structural list expansion, and separator normalization.

Raw-string C# template IntelliSense is included through the language service's existing embedded
string support: identify the quote call's constant category/template and hole declarations, map
positions through the C# string, and serve grammar-token navigation and completion. Splice names
navigate to the corresponding hole declaration in the C# API call. Dynamic template expressions
continue to work at runtime but receive no speculative editor interpretation. No Rider-specific
parallel parser is introduced.

## Acceptance gates

### List-splice slice

- Zero, one, and many items in repeated and separated lists.
- Empty splices at the beginning, middle, and end; several and repeated splices.
- `*` versus `+` cardinality after expansion, with and without fixed items.
- Destination separator correctness, including different source separators.
- Wrong category, unknown field, wrong shape, and unsupported shape diagnostics.
- Imported/extended grammar categories and alias element rules.
- Types, semantic properties, checks, and HIR parity with explicit equivalent programs.
- Item diagnostics and origins remain at the original item; cyclic rewrites still terminate.
- Generated views and list lowerers observe valid ordinary list layout.
- List-splice navigation/completion and unsaved-editor offsets.

### C# API slice

- Template reuse; scalar/list/mixed insertion; nested operators remain atomic.
- Results bind, check, and lower equivalently to explicit syntax through the documented API.
- Wrong inputs yield diagnostics without partial trees or borrowed storage.
- Source trees can be disposed after construction; multiple results are independent.
- Synthesized spans, origin mapping, diagnostics, and HIR origins are internally consistent.
- C# raw-string quote navigation/completion, including multiline source mapping.
- Existing scalar quotes, ordinary parsing, recovery, and editor behavior retain their tests.

Run focused tests for each slice, then `dotnet build Nitrogen.slnx -warnaserror` and
`dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`. Report runtime/unit verification separately
from live Rider checks. Preserve unrelated checkout changes; do not commit or publish them as part
of design preparation.

## Explicit exclusions

Direct quotation syntax in the C# compiler; interpolation interpreted as tree splicing; token or
tuple-element splices; nonliteral list separators; declarations authored by templates and their
hygiene; before-binding document macro expansion; implicit execution; and a general syntax printer.
