# Typed quasiquote helpers and Extract Quote Helper

Status: written spec approved in conversation on 2026-10-14.
This document defines the implementation scope. No feature implementation is included yet.

Editor integration depends on the persistent review UX already verified in local commit `d4e0444`
on `codex/extraction-review-ux`. Integrate that change before the helper-refactoring editor stage;
reuse its controls and feedback rather than creating a second review implementation.

## Intent

Reduce boilerplate when several grammar alternatives construct the same syntax. A grammar author
can name a typed quasiquote, reuse it through module imports, and extract an existing inline rewrite
into that helper. Calls construct syntax trees through Nitrogen's existing quotation/rewrite core.
They preserve caller expressions, precedence, binding, typing, lowering, and useful source locations.

The user approved grammar-defined helpers callable from `rewrites`, scalar/list parameters,
shared-module imports, navigation, and an Extract Quote Helper refactoring. Structural pattern
matching follows as a separate feature.

## Existing capabilities

`Language.CompileQuote` compiles reusable templates with `QuoteHole.Node` and `QuoteHole.List`.
`QuoteTemplate.Instantiate` copies syntax into independently owned storage, checks argument shape,
category, language snapshot and source validity, and does not substitute source text.

Inline grammar rewrites already generate a `RewriteSpec` containing template text, return category,
and mappings from named holes to syntax children. `FileSemantics.Instance` expands these through
`QuoteInstantiator` with proxies to the caller's children. The grammar generator and live workspace
both consume the grammar model. There is no grammar helper declaration or helper-call resolver yet.

## Declaration and call syntax

```ngr
syntax module DateCalc
{
  syntax function FortnightDuration(Count: Expr) : Expr =
    <[ $Count * 2 weeks ]>;

  extensible syntax Expr
  {
    | Fortnights = Count:Expr "fortnights" precedence 30
      rewrites FortnightDuration(Count)
  }
}
```

A declaration has a name, ordered parameters, a scalar syntax return category, one quoted body,
and a terminating semicolon. A parameter is `Name: Category` or `Name: Category*`. Categories are
qualified or unqualified syntax/extensible-rule names, resolved in the declaration's module.
Token rules, alternatives, semantic types, optionals, tuples, and list return types are excluded.
A zero-parameter helper is valid. Parameters have no defaults or overloads.

A list parameter is spliced with `..$Name`; a scalar with `$Name`. For example, in a language whose
`f` call accepts a comma-separated expression list:

```ngr
syntax function CallF(Args: Expr*) : Expr = <[ f(..$Args) ]>;
// In an alternative with an Args field such as Args:(Expr; ",")*:
// rewrites CallF(Args)
```

Arguments are positional references to labeled fields of the calling rule/alternative. A list
argument is written `Args`, without a splice marker at the call site; the parameter determines its
shape. Arbitrary C# expressions, literals, nested calls, and `this` arguments are excluded in v1.
The old `rewrites <[ ... ]>` form remains valid and keeps its behavior.

`function` becomes contextual after `syntax`: an existing rule named `function`, such as
`syntax function = "x";`, remains a rule. Helper syntax is distinguished by the declaration name
and following parameter list. A helper is not a parser production, extension point, or syntax kind.

## Names, imports, and type checks

Helpers belong to syntax modules and are exported automatically. The same module and explicitly
imported modules participate in lookup; qualified `Shared.FortnightDuration(Count)` is supported.
Use the existing rule-lookup policy for local precedence and ambiguity. More than one imported
candidate is an error, never a first-match choice. Duplicate helper names, parameter names, and
helper/rule name collisions within a module are errors. No overload resolution is introduced.

Each argument must be a supported direct syntax field, with the parameter's scalar/list shape and
the same resolved category identity. Qualified and unqualified spellings of the same declaration
are equivalent. Do not introduce implicit conversion between distinct syntax categories. Direct
repeated syntax fields and existing supported literal-separated lists can supply list parameters;
optional fields, tokens, tuple elements and nonliteral separators cannot.

The helper's return category must equal the rewrite site's resolved category. An extensible
alternative uses its extension-point category, as inline rewrites do today. The helper's quote is
parsed against its declared category in the complete composed language. Its keywords/operators may
come from that composition, while category/helper name lookup follows explicit module imports.
Every helper is checked, including unused declarations. A bad unused helper cannot be silently
accepted merely because no rewrite invokes it.

All declared arguments are required even if their parameter is unused in the body. Unknown holes,
wrong scalar/list markers, unsupported list positions, malformed quotes, and template-authored name
declarations are diagnosed. Empty lists retain the quotation core's existing destination checks:
an empty list inserted into a required destination is an error, not a partially constructed tree.

## Expansion, semantics, and ownership

Resolve helper calls during grammar analysis/generation. Emit the helper's quoted body and a mapping
from helper parameter names to the caller's syntax children into the existing rewrite representation.
Do not render arguments to strings, insert text, or reparse the caller's argument expressions.
Do not add a second evaluator for helpers. The quotation is parsed as a template against the current
language snapshot; each rewrite use instantiates it structurally through the existing proxy path.

This makes `FortnightDuration(Count)` semantically equivalent to the original inline rewrite.
For example, an argument `1 + 2` remains one operand of multiplication, and `count` continues to
resolve through the calling program. Template-authored names retain existing inline-quote binding
behavior; helper declaration scope does not capture program variables. No new hygiene model is
claimed. Template-authored declarations remain rejected, while valid caller-supplied subtrees retain
the existing quotation core's binding and ownership rules.

Helper bodies contain one quote only. They cannot call other helpers or execute arbitrary code.
Existing rewrite-cycle detection still applies when quoted syntax reaches a rewrite recursively.
The helper feature must not bypass that detection or return a partial expansion on failure.

Existing quote compilation/result ownership rules remain intact. Editing a helper, its parameter
categories, imports, or an affecting grammar invalidates the language snapshot through the existing
development-mode pipeline. Templates from an older snapshot must not accept nodes from a new one.

## Model and implementation boundaries

Add explicit helper declaration, parameter, quote-body, and helper-call representations to the
bootstrap grammar model. Helpers must be kept distinct from parser-producing rules, so existing
kind numbering, start-rule selection, terminal generation, and rule iteration cannot accidentally
turn a helper into a production. Preserve source spans for names, categories, arguments and quote text.

Extend grammar analysis with helper lookup and call checking. Extend generation with helper metadata
needed to validate all declared bodies in the composed language, including unused helpers. Existing
modules expose an empty helper collection by default; additions to runtime metadata are compatible
with handwritten modules. Existing public constructors and inline rewrite APIs remain compatible.

The generator and `GrammarWorkspace` share the same resolution/emission path. Helper calls lower to
existing rewrite templates with mapped inputs. Only additive source metadata needed for precise
navigation/diagnostics may extend that representation. Compile-time and editor behavior for the same
registered inputs must agree. Do not discover additional files during generation.

A separate C# named-helper invocation API is outside this slice. Existing `CompileQuote` and
`Instantiate` remain supported. The metadata must leave room for a later named-helper API without
requiring a different construction or ownership model.

## Editor behavior

- Completion suggests visible helpers after `rewrites`, distinguishes them from operations/rules,
  and shows parameter categories plus the return category.
- Go to definition/declaration and references work for helper names and calls across imported files.
- Parameter declarations and their `$Name`/`..$Name` uses are navigable. Call arguments navigate to
  their labeled grammar fields. Category annotations navigate to their syntax rules.
- Quoted keywords/operators retain existing quote navigation and completion.
- Rename covers a helper and all resolved grammar calls, or a parameter and its body splices. If a
  known consumer cannot be updated safely, refuse with a precise reason; never perform a partial rename.
- Diagnostics point at the actual offending argument, category, helper name, or quote location. Runtime
  expansion failures identify the calling source use and retain the helper definition as related source
  information where the protocol supports it. Do not invent offsets for copied subtrees.

Reserve the following currently unused grammar diagnostic codes: NGR0141 for duplicate helper names
or helper/rule collisions, NGR0142 for duplicate parameters, NGR0143 for unknown helpers, NGR0144 for
ambiguous helpers, NGR0145 for argument-count mismatch, NGR0146 for unsupported argument/parameter
shape, and NGR0147 for parameter or return-category mismatch. Reuse existing quote diagnostics for
equivalent quote failures. Recheck these reservations before implementation if another branch has
allocated codes; no unrelated diagnostic is repurposed.

## Extract Quote Helper

Expose a distinct `refactor.extract` action, **Extract Quote Helper…**, for an existing valid inline
`rewrites <[ ... ]>` clause. A caret anywhere inside that quote selects the complete quotation; a
nonempty selection must cover the whole quote, allowing surrounding whitespace. Partial templates,
recovered/ambiguous grammar syntax, existing helper calls, and unsupported inputs are refused.

Infer parameters only from actual parsed scalar/list holes used by the quote, in first-occurrence
order. Repeated occurrences produce one parameter. Dollar text inside comments or opaque tokens is
not an input. Infer categories and list shape from the original labeled fields, using the same
restrictions as call checking. Infer the return category from the enclosing rewrite site.

Prompt for helper name, destination grammar file, and destination syntax module. The destination may
be the original file/module, another existing file/module in the configured language, or a new local
`.ngr` file/module. Default to the original module for a simple extraction. Never replace an existing
file wholesale or silently change a helper with the same name. A colliding new-file path must be
explicitly selected as an existing-file destination before proposing an insertion. Insert the helper declaration into the chosen module,
replace only the original rewrite clause with the helper call, and add/import/configure the destination
as needed. Imported categories in the new declaration are qualified when that avoids ambiguity.

Shared helpers must work without a qualifying module prefix at unambiguous call sites. If moving the
quote would introduce an ambiguous helper name or incompatible imports, qualify the call or refuse
with a concrete explanation. Quote text, including opaque tokens/comments, is preserved verbatim.

Use the existing complete-proposal review/application machinery: one preview containing every source,
helper/import and configuration edit; persistent Apply/Cancel; fresh revalidation before application;
completion listing changed files; one undo restoring the full extraction. There is no automatic
replacement of other similar quotes and no automatic Save All.

Before offering Apply, validate the original composition and the proposed complete composition,
including configured C# helpers. Existing consumers, language factory start rules, parse behavior,
binding, diagnostics, and lowering must remain valid. Destination checks retain workspace boundaries,
symlink protection, collision refusal and stale-input safeguards. Unsupported consumers cause refusal.
The proposal and review controls must not alter source files before Apply.

## Deliberate exclusions

Structural matching/rewrite patterns; automatic replace-all of equivalent quote bodies; nested helper
calls; arbitrary argument expressions; overloads/defaults; list return values; declaration-producing
quotes and new hygiene rules; executing helpers in the editor; a new C# named-helper invocation API;
and moving existing helper declarations through the original whole-definition Extract Block action.
That last action must diagnose unsupported helper-bearing selections explicitly, not omit helpers or
silently break their references. Helper extraction is provided by the new action described above.

## Acceptance and verification

1. Parser/model tests distinguish helper declarations and calls from old productions, including a rule
   literally named `function`. Existing grammars and inline rewrites continue to generate unchanged kinds.
2. Validate local/imported/qualified lookup, ambiguous imports, duplicate names, ordered and repeated
   inputs, missing/extra arguments, scalar/list mismatches, category/return mismatches and unused bad bodies.
3. A DateCalc helper rewrite preserves the original four results: `3`, `42 days`, `42 days`, and
   `2026-11-02 Mon`. Include caller-bound names and `(1 + 2)` to prove binding and structural precedence.
4. Test empty/nonempty lists, required destinations, opaque dollar tokens/comments, declaration rejection,
   rewrite cycles, ownership after source/template disposal, and language snapshot mismatch.
5. Generator and workspace tests compile identical configured inputs and compare behavior; modifying a
   helper in development mode refreshes samples without restarting the server.
6. Language-service/LSP tests cover completion, helper/category/parameter/argument navigation, references,
   cross-file rename or precise refusal, and source locations of template and invocation diagnostics.
7. Refactoring tests cover current/new/existing destination modules, imports/config changes, complete
   preview, cancellation, real stale edits, unchanged document reopen, collision/path protections, and undo.
8. Live VS Code verification: extract the full DateCalc quote into a shared helper file, review/apply,
   confirm sample values and navigation, edit the helper and observe refresh, undo that helper edit,
   then undo the complete extraction as one operation.
9. Run focused tests, the full solution build with warnings as errors, all .NET/client tests, and relevant
   repository link/build/input checks. Keep source tests, live editor verification and release CI distinct.

## Delivery sequence

First implement helper syntax/resolution and structural rewrite generation with end-to-end tests. Next
complete editor navigation/diagnostics and the extraction proposal. Finish with the native VS Code
review/apply/undo check and documentation. These are stages of this feature, not separate promises of
completion; the requested slice is complete only when all acceptance criteria pass. Write and review
the implementation plan before product-code changes. PR/version/release remain separate user requests.
