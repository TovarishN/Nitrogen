# The Nitrogen grammar language

How `.ngr` grammars declare scopes, pass typed arguments to operations, repeat and expand typed templates, and how the editor uses what a document lowers to. For an overview of Nitrogen, see the [README](../README.md); for editor setup, [editor support](editor-support.md).

## Declaration scopes

A `scope` clause opens a local scope for a rule's children. A declaration normally belongs to its enclosing scope. Use `in file` to place selected declarations in the file scope:

```text
syntax Body = "body" Name:Identifier "{" Items:Item* "}" declares body Name scope;
syntax Let = "let" Name:Identifier "=" Value:Expr declares value Name;
syntax Part = "part" Name:Identifier declares part Name in file;
```

Here, values are local to each body, while parts are visible throughout the same file. `in file` also places dynamic-name openness and duplicate-name checks in the file scope. It does not export a symbol to other files; add `export` after `in file` when project visibility is required. The existing `type` clause can follow these modifiers.

Use `declares value Name sequential` for ordered declarations: a concrete name becomes visible after its declaring node ends, and later declarations in the same scope replace it. Its initializer can therefore use the previous value or an enclosing value. Self and forward references without such a value are not visible. Lexical completion uses the same ordering. Repeated names are allowed when all declarations in the group are sequential; mixing ordinary and sequential declarations still reports duplicates. Use `sequential` before `in file`, `export`, and `type`. Exported sequential names expose the final declaration from each document; names exported by multiple documents remain ambiguous.

## Inferred sequence arguments

Operation arguments may use `sequence inferred Field` when their element type depends on the selected operation. For example, `lowers operation Selected(sequence inferred Args)` takes the exact element type from the corresponding `Core.Sequence<T>` input of `Selected`. Every item must lower as that type; no casts or unit conversions are inserted. Empty lists keep the signature's element type, and separated lists skip separator nodes. A fixed non-sequence input fails composition; a computed non-sequence input fails semantic checking. The field must still be a repeated or separated list. `inferred` is special in this argument position; a fixed type with that name can be qualified with its module.

`optional text Field` passes an optional field's spelling as `Core.Optional<Core.Text>`: present, its tokens
without trivia; absent, an empty optional. It suits bare optional keywords (`Export:"export"?`). After
`optional`, `text` is reserved; a type of that name must be qualified with its module.

## Derived declarations

Names emitted by checked lowering can replace authored file-scope templates using `Project.SetDerivedDeclarations(path, kind, declarations)`. Each `DerivedDeclaration` carries the emitted name, a source node and name span in that document, and an optional export flag. The replacement is the complete index for that kind: it removes template declarations, seals dynamic file-scope openness, and retains local declarations and indexes for other kinds. An empty index means no names were emitted. Normal lookup, definition locations, completion, duplicate checks, and export visibility then use the emitted names.

For staged expansion, clear the template index before lowering the relevant subtrees, collect declarations from successful typed projection, then publish the complete index and validate the whole file again. Do not publish a partial result after projection fails. Updating an index invalidates binding resolution and project semantic caches; replacing or removing the document discards its derived indexes. Invalid node/span metadata is rejected before any project change. This API does not run domain expansion itself or automatically connect a language's editor service to its projector.

## Typed repetition

`lowers repeat ElementType CountField IteratorField TemplateListField` lowers a checked count and a typed template list into `HirRepeat`. For example:

```text
syntax Repeat = Count:Expr "as" Iterator:Iterator "{" Items:(Expr; ",")* "}" scope
                lowers repeat Core.Scalar Count Iterator Items;
syntax Iterator = Name:Identifier declares value Name sequential type Core.Scalar;
```

The count and declared iterator must be `Core.Scalar`; every template item must have `ElementType`. The result is `Core.Sequence<Core.Sequence<ElementType>>`, retaining one group per iteration. Use this value inside a host operation through normal declarative lowering, or obtain it with `HirLowering.LowerNested`.

`HirProjector` checks the template before running handlers, including for zero iterations. During projection, it binds the iterator to indices starting at zero, preserves enclosing bindings for nested loops, and restores them afterward. Positive fractional counts truncate toward zero, nonpositive counts yield no groups, and nonfinite counts or counts outside the supported signed 32-bit index range fail with source diagnostics. Host handlers receive typed projected groups and do not need to inspect source syntax. Numeric `HirEvaluator` is not the structured projection path.

Floating-point projection inputs must be finite; `NP0001` is reported at the reference during preflight before handlers run. Floating-point operation results must also be finite; a nonfinite computed result reports `NP0001` at the producing operation and stops evaluation before downstream handlers. Result validation runs during projection because a handler's computed value is not available to structural preflight. Other domain constraints still require domain checks.

### Selected declarative roots

`HirLowering.LowerSelected(file, syntaxKinds, snapshotId)` lowers selected declarative syntax kinds in source order without collecting registered roots from other language regions. It performs the same recovery, binding, and semantic checks as nested lowering. A selected node with no supported lowering reports `NH0005` instead of disappearing. Consumers can select their language's domain roots without implementing a syntax traversal; callers decide whether any diagnostic invalidates the complete result.

Whole-file semantic checking remains the default. An optional fourth argument, `SemanticCheckScope.SubtreeAndAncestors`, uses `FileSemantics.DiagnosticsForSubtree(node)`: checks in the subtree and its ancestors run once, including ancestor checks that report at a child. Property dependencies still evaluate lazily; missing syntax and unresolved bindings still prevent lowering. Unrelated checks remain pending for `Diagnostics()`, which completes validation without repeating checked nodes. Both modes keep the first reading of ambiguous syntax.

Use scoped checking only when unrelated sibling checks cannot report errors at the selected subtree's source span. It is a partial validation API, not whole-file approval. Consumers must run complete diagnostics before admitting an output or invoking effects. Gravity uses scoped body preflight with pure rig projection, then publishes the generated-part index and checks the complete file before returning any rig.

### Deferred projection arguments

Use `ProjectionHandler.Deferred(signature, get => ...)` for synchronous operations that select which arguments to evaluate. `get(index)` evaluates that typed argument on demand and caches its result for the current invocation. It returns null after an argument failure; the operation cannot hide that failure by returning a value. Argument access expires when the handler returns. The registry requires exactly one eager or deferred implementation per handler, with the same exact catalog signature checks.

Whole-tree preflight still checks all branches, including unselected ones, before any handler runs. Deferred evaluation skips unrequested computed results and their handlers; it does not bypass missing bindings, invalid constant/input values, or signature checks. The runtime supplies argument access, while the host implements its domain's condition and truth conventions. Returned types and finite numeric values use the same validation as eager handlers.

## Templates

A declaring rule with `lowers template Body(Params)` is a template, and a referencing rule with
`lowers expand Name(Args)` expands the template its `Name` resolves to, in any file of the project:

```text
syntax Definition = "def" Name:Identifier "(" Params:(Parameter; ",")* ")" "=" Body:Shape
                    declares shape Name export scope lowers template Body(Params);
syntax Parameter  = Name:Identifier ":" Type:Identifier declares parameter Name type Type;
syntax Make       = "make" Name:Identifier "(" Args:(Dimension; ",")* ")" ";"
                    references shape Name lowers expand Name(Args);
```

A template body is never a lowering root. An expansion has its body's type; it lowers the body with
each parameter (by position) replaced by the argument lowered at the call, keeping the parameter
reference's origin, and its result's origins start with the call's. The call is checked for a template
callee (`NT0007`), the argument count (`NT0008`, at the name) and each argument's type against its
parameter's declared type (`NT0001`). An expansion is blocked by errors within the template, reported
there (`NH0001`–`NH0003`), and a cycle reports `NH0007` at the name of the call that closes it.
Geometry's `def`/`make` use these clauses; see the [design](superpowers/specs/2026-10-02-declarative-templates-design.md).

## Editor support for lowered languages

The language server uses what a document lowers to as well as its syntax:

- **In `.ngr` grammars**, the lowering clauses form a small language of their own. Operation names in
  `lowers Op(...)` are colored as functions, semantic types (`Core.Scalar`, the type after `declares … type`)
  as types, and the fields a clause passes as parameters, like the labels that declare them
  (`Width:Dimension`). Completion after `lowers` offers the operations of the semantic catalog with their
  signatures, after `literal`, `text`, `sequence`, `repeat` or `value` its types, and inside the
  argument list the rule's fields. Hover shows an operation's signature. An operation or type missing
  from the catalog (`NM0008`, `NM0009`), or a call with the wrong number of arguments (`NM0010`), is
  reported at the clause. The catalog is the last good one of the `nitrogen.json` language the grammar
  belongs to; other grammars see only the built-in types.
  `Nitrogen.ngr` itself lowers every grammar to typed HIR over the `Grammar` semantic module
  (`GrammarSemantics`): hover over a rule, expression or clause shows the `Grammar` operation it lowers
  to. Its colours stay those of the grammar's syntax (`Presentation.ColorFromLowering` is off for `.ngr`).
  `NgrParser.Parse` builds its `GrammarModel` from that HIR (`NgrProjector`), with no separate syntax walk.
- **In a language's documents**, a word that spells an operation (`weekday`, `days`, `box`) is colored
  as a function, and a token that lowers to a value is colored by its type: a number, or a string for
  `Core.Text`. A language's `nitrogen.json` entry can map types to token types, for example
  `"types": { "DateCalc.Date": "enumMember" }`. Completion details show each name's type, and the names
  whose type the enclosing operation expects come first (`DeclarativeTypes.ExpectedTypeOf`). Hover shows
  what the expression lowers to.

`ILanguageAssist` is the hook behind the `.ngr` support: a `LanguageEntry` can add colors, completions,
hovers and diagnostics for text that its binding and lowering do not describe.
