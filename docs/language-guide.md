# The Nitrogen grammar language

How `.ngr` grammars declare scopes, pass typed arguments to operations, repeat and expand typed templates, and how the editor uses what a document lowers to. For an overview of Nitrogen, see the [README](../README.md); for editor setup, [editor support](editor-support.md).

## Structural diagnostic matches

A semantics block can report a diagnostic when a typed syntax pattern matches the current node:

```ngr
syntax Paired = "<" Open:Name Attributes:Attribute* ">" Children:Node* "</" Close:Name ">" {
  match (Attrs: Attribute*, PasswordAttrs: Attribute*, Content: Node*)
    <[ <credentials ..$Attrs><password ..$PasswordAttrs>..$Content</password></credentials> ]>
    => warning XML1001 : "Use a secret reference instead of an embedded password.";
}
```

Use `warning` or `error` after `=>`. A match reports at the current node; a mismatch produces no diagnostic.
The capture list is optional for a literal pattern. Captures have syntax categories, with `*` for lists,
and use the same structural matching rules as syntax-function match arms: exact token spelling and
nesting, and trivia ignored. Multiple variable-length segments per list are supported. These statements
do not construct replacements and need no fallback. Message expressions can use the containing rule's
semantic fields and that match's named captures. Scalar captures are syntax views: `.Text` gives source
text, and categories with semantic properties expose those properties. List captures are immutable
`IReadOnlyList<TView>` values with `Count`, indexing and enumeration. Captures are local to their own
message; names cannot shadow children, properties or reserved semantic view members. Wildcards create
no variables. Messages run only after a successful match, using the same capture result.

For the XML grammar, a password containing one text node can be reported with its captured text:

```ngr
match (pass: Text)
  <[ <credentials ..$_>..$_<password ..$_>$pass</password>..$_</credentials> ]>
  => warning XML1001 :
    $"A password is stored directly inside credentials; use a secret-ref instead. {pass.Text}";
```

`Text` here is a syntax category, not a string type. `{pass}` also uses the view's existing text
conversion. To accept empty or mixed element/text contents, capture `(content: Node*)` using
`..$content` and format its items explicitly; a list does not have an implicit combined `.Text` value.

All diagnostic patterns are validated when the language is composed, including ones no input reaches.
Warnings are preserved by the CLI and language service and do not block lowering; errors do. See the
[XML example](../examples/XmlPatterns/README.md) for the complete grammar, configuration and input.

## Sequence patterns and wildcards

Patterns can select several elements anywhere in an ordered list:

```text
f(..$_, $X, ..$_, $Y, ..$_)
f(..$Before, $X, ..$Between, $Y, ..$After)
f(..$_, $X, $_, $Y, ..$_)
```

The first two forms select distinct positions with `X` before `Y`; the last requires exactly one
intervening item. Declare named captures with their syntax categories (`X: Expr`, `Before: Expr*`
in grammar arms, or `QuoteHole.Node`/`QuoteHole.List` in C#). `$_` matches one syntax node and `..$_`
matches zero or more list items, without a declaration or binding. Each discard is independent and
gets its category from its grammar position. `_` is reserved in pattern capture declarations;
construction quotations retain their existing named-input behavior.

Matching covers the entire selected syntax node. Segments try shortest spans first, in pattern
source order, and return the first complete successful match. Thus on `f(1, 2, 3)`, the first pattern
captures `X = 1`, `Y = 2`; `f(..$A, ..$B)` captures empty `A` and all arguments as `B`. Later structure
or repeated captures can force backtracking, including into nested lists. Repeated named captures
must be structurally equal; repeated discards impose no equality. Separators are punctuation, not
items, and required-list cardinality still applies to the input.

`QuotePattern.Match(input)` uses a default limit of 1,000,000 work steps, including whole-input
validation, structural comparisons, candidate splits and item checks. Use `Match(input, new
QuoteMatchOptions { MaxWorkSteps = ..., CancellationToken = ... })` to supply a positive limit and
cancellation token. Exhaustion returns `NQP0006`, false, and empty captures; an ordinary mismatch
has no diagnostics. Cancellation throws `OperationCanceledException`. Grammar helpers stop on
match errors rather than trying another arm or fallback; lowering wraps quotation errors in its
existing `NH0009` diagnostic. Ignored input still must be valid syntax from the same language snapshot.

Captures borrow input syntax and preserve its origins. Keep the source tree alive while consuming
captures or instantiating a replacement. Matching performs no rewriting, lowering or implicit subtree
search. Unsupported token/list hole sites remain unsupported.

## Declaration scopes

A `scope` clause opens a local scope for a rule's children. A declaration normally belongs to its enclosing scope. Use `in file` to place selected declarations in the file scope:

```ngr
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

## Alias rules

A syntax rule whose body is one reference or literal, or a choice of references and literals, is an
alias: `syntax Mod = "public" / "static";`, `syntax TypeName = "int" / Name;`,
`syntax Statement = Assign / Call;`. An alias produces no node of its own. It parses whichever
alternative matches and leaves that alternative's node (a `Literal` leaf for a literal) where the alias
was used, so a tree has no wrapper around a choice of single tokens.

- The typed view of an alias is a `Token` when every alternative is a literal or a token rule, and a
  `SyntaxNode` otherwise. A rule that references an alias reads it through that view, and a view of the
  alias's own name does not exist.
- An alias is a category for templates and quotation holes: a hole of a leaf alias such as
  `Shapes.Mod` takes a `Literal` leaf whose text is one of its literals.
- A rule with `declares`, `lowers` or another binding clause, or with semantics, stays a node even when
  its body is a single literal (`syntax Terminator = ";" lowers Grammar.Terminator();`), because the
  clause needs a node to bind. An alias of references only with a clause is an error (NGR0117).
- A missing alternative in a recovered tree is a `Missing` `Literal` leaf for a leaf alias that lists a
  literal, and a `Missing` error node for other aliases.

## Transparent alternatives

An alternative of an extensible rule marked `transparent` produces no node of its own, the way an alias
does: `extensible syntax Stmt { | transparent Plain = Body:(Var / ";") }`. The node its element yields
(the `Var` node, or a `Literal` leaf for `";"`) stands in for it where the extension point was parsed.
The alternative still competes with the others as usual: longest match first, then precedence, and a tie
is an `Ambiguous` node whose children are the candidates' nodes, standing-in ones included.

- The body must yield exactly one node on every path: one literal, character class or rule reference,
  labeled or not, or a choice of them (NGR0160). A sequence, a repetition, a separated list, an optional
  or a predicate cannot be transparent, because it would leave a group, a `List` node, a null slot or
  nothing in the point's place.
- It must be a prefix alternative (NGR0161), and it cannot have a precedence, binding clauses or
  semantics (NGR0162), which all need a node.
- `transparent` is read as the marker only when a name (`Name =`) or an element follows it; otherwise it
  is a reference to a rule named `transparent`. Without a name, a single reference gives the alternative
  its name, as for other alternatives (`| transparent Var`).
- The alternative has no typed view and no visitor method. The extension point's union view reaches the
  standing-in node, so `SyntaxView.TryCast<VarNode>` on it works and a cast to another alternative's view
  fails. The standing-in node is also a member of the point's quotation category, so a hole of the point
  accepts it.

## Tree shapes

What a `SyntaxTree` holds for the grammar, for code that walks it without typed views:

- **Null slots.** An absent optional (`Type:(":" TypeName)?`) and an empty list (`Items:Name*`) have no
  node, but keep their child slot, so positional child indices are the same in every parse.
  `SyntaxTree.Child` returns `SyntaxTree.NoNode` (-1) there, and `ChildCount` counts the slot. Check for
  `NoNode` before passing a child to `Kind`, `Span` or `Flags` (they throw on it); the generated views'
  `Is`, `SyntaxView.TryCast` and `Optional<T>` already do. Dumps (`SyntaxDumper`) write a null slot as `_`.
- **One-item lists.** A list slot with one item holds the item itself, not a `List` node around it. Any
  node in a list slot that is not a `List` is a one-item list. Read list slots with `ListSlot.Count` and
  `ListSlot.Child` (or the typed `SyntaxList<T>`), and use `Language.IsListSlot(kind, slot)` to tell a
  list slot from a plain one. The generator keeps the `List` node when an item could itself be a list,
  as in `Lines:(Name+; ";")*`.
- **No wrappers.** Alias rules and transparent alternatives produce no node: the node of whichever
  alternative matched (a `Literal` leaf for a literal) sits where the alias or the extension point was
  parsed. Code that tests kinds must test the standing-in kinds, not the alias's or alternative's name,
  which has no kind.

A transparent alternative compiled in a separate module does not add its extension point's quotation
category to the rules it references when they belong to modules that were already generated: a hole of
the point then does not accept a node of such a rule standing in through that alternative.

## Typed repetition

`lowers repeat ElementType CountField IteratorField TemplateListField` lowers a checked count and a typed template list into `HirRepeat`. For example:

```ngr
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

```ngr
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

## Rewrite rules

A rule can mean code the language already has, written in the language itself: `rewrites <[ … ]>`
replaces a `lowers` clause, and each `$Field` in the template is filled by that field of the use.

```ngr
extensible syntax Expr
{
  | Fortnights = Count:Expr "fortnights" precedence 30  rewrites <[ $Count * 2 weeks ]>
}
```

`3 fortnights` then types, checks, lowers and evaluates exactly as `3 * 2 weeks`. A hole is one node:
`!$Cond` with `a + b` in the hole means `!(a + b)`. The template parses as the rule's category: the
extensible rule of an alternative, or the alias that lists a plain rule. Errors inside an expansion are
reported at the use (`in the expansion of Fortnights: …`), and hover on a use shows its template.

Templates may not declare names, and holes stand for syntax or extensible rules, not tokens. A template
that doesn't parse or declares a name is a warning in the grammar (NGR0137, NGR0138); its uses report
NH0009.

Limits of this version:

- Names in a template resolve only against the language's built-ins, not the declarations of the
  document that uses it.
- `in` properties a template's nodes would pass down are not forwarded into its holes.
- A rewrite that reaches itself, directly or through another rewrite, reports NH0008 at the use.
- A plain rule's template parses as the alias listing it only when exactly one alias of its module lists
  it; otherwise it parses as the rule itself, so it can only be a hole or a use of the same rule. List
  such a rule in one alias.

## Typed quote helpers

Declare reusable syntax with a typed helper, then call it from `rewrites`:

```ngr
syntax function FortnightDuration(Count: Expr) : Expr = <[ $Count * 2 weeks ]>;

| Fortnights = Count:Expr "fortnights" precedence 30
    rewrites FortnightDuration(Count)
```

Helpers produce syntax trees through the same structural rewrite pipeline. Parameters receive the
caller's syntax nodes, so precedence, source origins and caller-bound names are preserved. Helpers
are separate from parsing rules and introduce no new parser kinds or evaluator handlers.

A list parameter uses `*` and its body uses `..$`:

```ngr
syntax function Twice(Args: Expr*) : Expr = <[ f(..$Args, ..$Args) ]>;
| Forward = "twice" "(" Args:(Expr; ";")* ")" rewrites Twice(Args)
```

The destination list supplies separators and enforces its minimum item count. Empty and repeated
splices retain existing quotation ownership and snapshot checks.

Helpers can live in another composed grammar module. Add `using Shared;` to use its exports without
qualification, or call `Shared.FortnightDuration(Count)` explicitly. Local helpers take precedence;
ambiguous imported names require qualification. Declared parameter and return categories must match
exactly after resolving names; category types may be qualified, such as `DateCalc.Expr`.

Every helper body is checked against the composed parser, including unused helpers. Invalid helpers
prevent composition rather than failing only when called. Diagnostics from expansions identify the
call site and retain the helper body as related source.

This first version accepts positional caller fields, scalar syntax returns, and scalar/list syntax
parameters. Nested helper-call arguments, arbitrary C# arguments, overloads, defaults, list returns,
template-authored declarations are not supported. Typed pattern matching is supported through syntax-function match arms and the C# pattern API. The C# quotation
API below remains available; named `.ngr` helpers are currently called from grammar rewrites.

VS Code offers **Extract Quote Helper…** for a caret inside an inline quote or a whole-quote selection.
It infers parameters from actual parsed splices in first-use order and preserves the original body.
Choose the current module, an existing module, or a new module; review and apply all changes together.

## List splices and C# syntax quotation

A list-valued field is inserted with `..$Args`. For example, in a language whose `Expr` category has
`f(...)` calls:

```ngr
| Forward = "forward" "(" Args:(Expr; ";")* ")"
    rewrites <[ f(1, ..$Args, 2) ]>
```

`forward(3; 4)` expands to the syntax of `f(1, 3, 4, 2)`. The destination supplies the commas;
source separators are not copied. An empty splice disappears along with its boundary separator.
Several splices, including repeated uses of one field, are allowed. An empty `*` list is valid;
an empty `+` result reports NH0010 at the rewrite use. Fixed destination items can satisfy `+`
even when a splice is empty. Inserted items keep their original semantic evaluation and diagnostics.

Supported source fields and destinations are direct syntax references in `Expr*`, `Expr+`,
`(Expr; ",")*`, and `(Expr; ",")+`. Imported and alias categories work through the grammar resolver.
Optional fields, token elements, tuple elements, and nonliteral separators are excluded. A scalar
field used with `..$`, or a list field used with `$`, reports NGR0139; unsupported list fields report
NGR0140. Placeholder occurrences are confirmed by parsing the template, so dollar text inside
opaque tokens and comments remains ordinary text.

C# helpers use the same parser through `Language.CompileQuote`. No C# preprocessing is required.
The following assumes `language` contains the `QuoteLists.Expr` grammar used by the quotation tests:

```csharp
using Nitrogen;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

using var input = language.ParseTemplate("1 + 2", "QuoteLists.Expr",
    new Dictionary<string, string>());
using var compiled = language.CompileQuote("QuoteLists.Expr", """f(..$Args, $Value * 2)""",
    new Dictionary<string, QuoteHole>
    {
        ["Args"] = QuoteHole.List("QuoteLists.Expr"),
        ["Value"] = QuoteHole.Node("QuoteLists.Expr"),
    });
if (!input.Success || !compiled.Success)
    return; // Report the input/compilation diagnostics.

using var result = compiled.Template!.Instantiate(new Dictionary<string, QuoteArgument>
{
    ["Args"] = QuoteArgument.List(Array.Empty<QuoteNode>()),
    ["Value"] = QuoteArgument.Node(new QuoteNode(input.Tree, input.Tree.Root)),
});
if (!result.Success)
    return; // Report result.Diagnostics.

var project = new Project(language);
project.Set("generated.quote", result.Tree!);
using var semantics = new ProjectSemantics(project);
var lowered = HirLowering.Lower(semantics["generated.quote"], language.SemanticCatalog);
var mapped = result.Remap(lowered);
```

The inserted addition remains one operand of multiplication. Construction copies the tree structure;
it never substitutes text and reparses it. `Tree.Text` is a synthesized coordinate buffer, **not a
round-trip syntax printer**: an inserted addition's text need not gain parentheses even though its
structural precedence remains intact. Node spans index that buffer. Internal operand trivia is kept;
expanded lists use deterministic separators and spaces.

The compilation owns the parsed template until it is disposed. Each instantiation owns fresh storage
and survives disposal of the compilation and source trees. Keep a result alive while a destination
project uses its tree; dispose it afterwards. All declared hole arguments are required, even if a
particular template does not use one. Dictionaries and input lists are copied when consumed.
Invalid arguments return diagnostics and no partial tree: NQ0002 for names/shapes, NQ0003 for invalid,
disposed, recovered or ambiguous nodes, NQ0004 for category/language snapshot mismatches, and NQ0005
for empty required lists. Malformed templates/unsupported categories report NQ0001; template-authored
declarations report NQ0006. Handwritten modules must provide quotation category metadata.

Inserted names bind freshly through the destination `Project`; construction captures no symbols and
performs no automatic binding, checking or execution. A `QuoteNode` can carry an explicit `SourceOrigin`.
`result.Origins`, `MapOrigin`, `MapRange(generatedPath, span)` and `Remap(lowered)` expose those identities.
Descendants conservatively map to the supplied subtree identity; offsets and descendant identities
are not invented. Locations without supplied identities stay in the generated document. Use the
`QuoteOptions` overload for an explicit template identity. Default lowering uses the constructed
tree's identity; lowering with a custom snapshot ID uses `Remap(lowered, generatedPath, snapshotId)`.

Grammar quotes offer definition/declaration, hover, usages and completion on scalar/list fields and
syntax keywords. In C# helpers, the server recognizes the actual `Language.CompileQuote` method,
constant categories/templates, and inline `Dictionary<string, QuoteHole>` initializers using
`QuoteHole.Node`/`List`. Regular, verbatim and raw strings, named arguments and constant concatenations
are supported. Each editable range stays within one C# literal. Dynamic templates/dictionaries and
ambiguous workspace language categories receive no guessed interpretation.

The executable examples are covered by `QuoteApiTests`, `QuoteOwnershipTests`, `QuoteOriginTests`,
`TemplateListInstantiationTests`, and the grammar/C# quotation navigation tests.

## Grammar functions

A grammar function computes part of a rule when the grammar is compiled. A call is replaced by
ordinary classes, literals, references, sequences and choices, so the generated parser has nothing
new to run. Nitrogen ships no function library; a grammar declares the functions it needs.

Declare one with `function`, the same form as quote helpers. Its return type is `Grammar.Rule`, and
its body is a grammar expression rather than a `<[ ]>` template. This lexical module limits
`\uXXXX` escapes to identifier characters:

```ngr
syntax module CSharp.Lexical
{
  // The hex digit d, in either case.
  function HexDigit(d: Grammar.Int) : Grammar.Rule =
    if d < 10 then [char(48 + d)] else [char(55 + d) char(87 + d)];

  // Exactly `width` hex digits, in either case, whose value is a member of `values`.
  function HexIn(width: Grammar.Int, values: Grammar.Class) : Grammar.Rule =
    if width == 0 then ()
    else choice d in 0 .. 15 {
      let size = 16 ^ (width - 1);
      let low = d * size;
      let part = values & [low .. (low + size - 1)];
      where part != [] : HexDigit(d) HexIn(width - 1, part - low)
    };

  token IdStart      = ['A'..'Z' 'a'..'z' '_' '\u00c0'..'\u00d6'];
  token UnicodeStart = "\\u" HexIn(4, IdStart) / "\\U0000" HexIn(4, IdStart);
}
```

Here `\u0041` matches and `\u0030` does not. A call is a name immediately followed by `(`;
`Name (X)` is still a reference followed by a group. Calls resolve like rule references: the
function's own module, a qualified name, or exactly one `using`.

- **Parameter types:** `Grammar.Int`, `Grammar.Char`, `Grammar.Text`, `Grammar.Class` and `Grammar.Rule`.
  A rule name can be passed as a `Grammar.Rule`. A token rule that is a class, or a choice of class
  rules, can also be passed as a `Grammar.Class`.
- **Forms allowed only in a function body:** `if c then a else b`, `choice x in a .. b { ... }`,
  `seq x in a .. b { ... }` (or `in text` for each character), the empty sequence `()`, and class
  bounds computed from values. A block may start with `let x = value;` lines and a
  `where condition :` filter. The body itself may start with `let` lines.
- **Value expressions** appear after `let`, `if` and `where`, in ranges, in call arguments, and as
  parenthesized class bounds such as `[(low + 1) .. high]`. From loosest to tightest:
  - `or`, `and`, `not`
  - `== != < <= > >=`
  - `|` (union), `&` (intersection)
  - `+ -`: integers, a character plus an integer, a class shifted by an integer, or a class difference
  - `* / %`
  - `^` (right-associative)
  - unary `-` and `~` (class complement)

  `[]` is the empty class, and `{ ... }` is a grammar fragment used as a value. The built-ins are
  `char`, `code`, `upper`, `lower` and `length`.

Evaluation is pure and memoized, with limits on nesting depth and size. In a token rule, choices
in the expansion are factored: `[a] X / [b] X` becomes `[ab] X` when that cannot change which
alternative matches. Diagnostics are `NGR0150`–`NGR0159`. They cover type errors, unknown
functions, wrong argument counts and types, evaluation errors and limits, duplicate names, and a
warning for character-level calls in syntax rules. Errors found while evaluating are reported at
the call, with the chain of functions involved. Go to definition and usages work for functions,
their parameters, and rules named in arguments.

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
  as a function. Numeric HIR constants are numbers, and lexical string/number tokens keep their
  roles through AST, sequence and optional wrappers. An arbitrary semantic type is not assumed to
  be numeric, and an identifier lowered as `Core.Text` is not assumed to be a string literal.
  A language's `nitrogen.json` entry can explicitly map types to token types, for example
  `"types": { "DateCalc.Date": "enumMember" }`. Completion details show each name's type, and the names
  whose type the enclosing operation expects come first (`DeclarativeTypes.ExpectedTypeOf`). Hover shows
  what the expression lowers to.

`ILanguageAssist` is the hook behind the `.ngr` support: a `LanguageEntry` can add colors, completions,
hovers and diagnostics for text that its binding and lowering do not describe.

## Scoped parsing context

A syntax module may declare a Boolean grammar flag with `context Flag;`. Each parse starts with all flags false. `context(Flag)` is a zero-width predicate with no tree slot; use `!context(Flag)` for its inverse. `with(Flag = true) Child` and `with(Flag = false) Child` parse one unary child under a replacement value and restore the complete caller environment on success, failure or exception. Use parentheses to scope a sequence, and nest operations to replace several flags.

Flags resolve like grammar names: locally, through `using` modules, or as a module-qualified name. The selected Language must include every referenced flag owner, even if a particular entry rule does not execute that use. Composition rejects duplicate qualified identities and more than64 declared flags. Flag bits belong to the immutable Language snapshot, not to global process state.

Scopes retain the child's typed owner, captures and UTF16 source spans; they add no wrapper node. For example `Items:with(Flag = true) Expr*` keeps the ordinary typed list field. Both successful and failed memo entries, and incremental subtree reuse, distinguish the full incoming flag environment. A header edit can therefore change a nested parse's meaning even when its text is unchanged.

A root ordered choice can select the context while keeping common named fields. If its branches use context operations and have structurally identical material layouts (including labels, references, literals and repetition), the branches parse directly into the rule or alternative owner. Predicates do not occupy slots; scopes and unlabeled sequence parentheses are transparent in this common layout. For example `(&AsyncHeader with(Flag = true) (Parameters:Signature Body:Block)) / (!AsyncHeader (Parameters:Signature Body:Block))` retains `Parameters` and `Body`. Ordinary choices without context operations, choices with different layouts, and labeled nested groups retain their existing shapes. This is a syntax tree projection rule, not an assertion that the two branches have the same meaning.

Context operations are syntax operations. Token rules cannot read or change parser context; guard a token reference from syntax instead. Context-only nullable repetition and hidden left recursion are rejected. A scope around a postfix operand retains its precedence; a scoped recursive left operand must not conceal recursion.

Standalone parsing and quotation start with false flags. To parse a category in a non-default context, author an explicitly scoped entry rule. There is no ambient flag parameter on `Language.Parse` or `CompileQuote`. Parsing context does not implement binding, source-specific legality checks or execution.

See [ScopedWords](../examples/ScopedWords/README.md) for an IDE-configured example. Context declarations/references support definition, references, hover, safe rename and completion; extraction retains qualified flag identity.
