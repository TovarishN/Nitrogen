# Generated language composition factory

## Purpose

Remove the need for C# helpers to enumerate generated syntax modules. Extracting
rules into a new module must preserve a helper's syntax composition automatically
when the language configuration is updated. This is the user's selected option 3.

## Public API

A configured language named `DateCalc`, generated into `DateCalc.Syntax`, supplies:

```csharp
public static class DateCalcComposition
{
    public static LanguageBuilder CreateBuilder();
    public static Rule Start { get; }
}
```

`Rule` is the existing runtime type returned by generated module rule members;
no new start-rule handle type is needed.

`CreateBuilder()` returns a fresh builder containing every syntax module selected
by that language definition, exactly once, in the same deterministic order as the
editor workspace. It does not build or cache a Language. `Start` returns the
configured generated start-rule handle, so moving that rule does not require C#
callers to change a generated module qualifier.

For example, DateCalc's existing lazy factory becomes:

```csharp
new Lazy<Language>(() => DateCalcComposition.CreateBuilder()
    .AddSemantic(MathModule.Semantics)
    .AddSemantic(DateCalcLanguage.Semantics)
    .Build());
```

Its parse call uses `DateCalcComposition.Start`.

Semantic registrations and builder customization stay explicit. The generated
factory does not discover semantic providers through reflection, execute helper
initializers during compilation, or supply a cached global language. Existing
explicit module composition remains supported.

## Configuration and generation

Both a `language` declaration and a `nitrogen.json` language entry can define a
composition. The factory name is `<LanguageName>Composition` in that language's
configured generated namespace. Invalid C# identifiers, duplicate factory names,
and collisions with existing generated or helper types produce diagnostics;
there is no silent name mangling or arbitrary winner.

An ordinary build and live editor compilation use the same normalized composition
model and source emitter. Membership comes from the resolved grammar list for that
particular language, including its declared dependencies. It must not include every
unrelated grammar registered in the project. Multiple modules in one selected file
all belong to the composition.

The source generator consumes only registered AdditionalFiles and their metadata;
it must not read configuration or dependencies from unregistered filesystem paths.
Build integration must register the relevant configuration inputs, and generator-only
projects must explicitly register them. Configuration and grammar changes invalidate
generation. Plain grammar projects without a language definition keep their existing
module API and do not gain a guessed composition factory.

A configuration and declaration selecting the same language must follow the existing
language-definition precedence and conflict rules. If those rules cannot identify a
single composition unambiguously, report a diagnostic rather than emit competing
factories. Namespace resolution must agree with the existing generated module types.

## Live workspace and extraction

Workspace compilation emits the factory alongside generated syntax and helper code.
The factory's syntax modules match those actually composed by that language's editor
session. Existing semantic-provider discovery in editor sessions remains unchanged.

Extract Block updates language grammar membership and the start setting as it does
now. Helpers using the composition factory automatically see the extracted module.
The proposed C# is compiled before application without loading helpers or executing
initializers. A stale or invalid proposal is refused before any edits are applied.

The existing protection for helpers composing modules directly remains for unsupported
consumers. This feature does not silently rewrite arbitrary C# factories or weaken
checks for quotation category strings and moved generated API names.

## Initial migration and documentation

Migrate DateCalc's evaluator to the factory and stable start member. Ensure both its
project build and live editor configuration provide the same composition inputs.
Document the API, explicit semantic setup, generator-only input registration, live
refresh, and the limits of extraction. Preserve unrelated current documentation edits.

## Acceptance

- Generated factory and start member compile in a real source-generator consumer.
- A helper using the factory runs representative DateCalc programs before and after
  extraction, with equivalent parse, binding, typing, lowering, and displayed results.
- Extracting a start rule preserves the stable Start API if other extraction checks
  permit that move.
- Editing grammar membership changes the live factory without restarting the LSP.
- Multiple languages do not absorb each other's modules; dependency order is stable.
- Configuration errors, missing registered dependencies, and factory collisions yield
  actionable diagnostics without partial source emission for the affected composition.
- Existing module-only consumers and unsupported-helper refusal tests remain valid.
- Focused generator, workspace, extraction, and DateCalc tests pass, followed by the
  repository build and full .NET test suite. Verify the VS Code extraction interaction
  for the migrated DateCalc helper as well.

## Outside this change

Automatic semantic discovery for generated application factories, arbitrary C# helper
rewrites, public API forwarding for moved generated types, quote-string rewrites,
quasiquote functions, and structural matching are separate changes.
