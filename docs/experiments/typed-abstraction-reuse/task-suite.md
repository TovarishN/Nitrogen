# Task suite and prompt artifacts

**Status:** illustrative pilot cards and templates. The host signatures below are proposed, not current Nitrogen APIs. Freeze the real signatures and keep generated held-out cards outside the agent-visible workspace during runs.

## Initial catalog given to agents

`Core` supplies booleans, finite scalars, and bounded lists. `Units` distinguishes length and mass and supplies pure arithmetic and comparison with exact unit signatures. `Geometry` supplies contact positions and a mass-weighted one-dimensional load projection. `Optimization` chooses from a finite list using a typed score. Each module has a fixed host-owned capability grant. The catalog must omit any ready-made support interval, signed margin, stability predicate, or placement selector.

Use an actual generated catalog export in the run. Do not treat this paragraph as a substitute for machine-checked signatures.

## Pilot A: platform placement

Agent-visible task card:

> A platform touches the ground at the contact positions in the input. A set of loads has mass and horizontal position. Write a Nitrogen program that decides whether the projected center of mass is at least `requiredClearance` inside both extreme contacts, then reports the smaller clearance. The program must work for different valid input lists. You may define and admit a reusable language module if the supplied vocabulary does not express the calculation cleanly.

Illustrative examples for the harness, expressed in metres: contacts `[-0.6, 0.2, 0.8]`, projected center `0.1`, required clearance `0.15` give smaller clearance `0.7` and pass. A projected center of `0.9` gives smaller clearance `-0.1` and fails. The harness must derive the projection from typed loads; it must not hand the agent the projected value as the only input. Hidden cases vary list length, position order, masses, boundary equality, and invalid inputs.

## Pilot B: candidate stance selection

Agent-visible task card, shown in a fresh conversation:

> A mobile platform can choose among candidate stances. Each stance has contact positions and a load layout. Select the valid stance with the greatest guaranteed horizontal clearance, subject to `requiredClearance`. Return the selected candidate ID and its clearance, or report that none qualifies. Use the available Nitrogen modules.

Illustrative candidates use different contacts and load layouts from A. For example, a stance with extreme contacts `[-0.4, 0.6]` and projected center `0.48` has clearance `0.12`; it qualifies at threshold `0.10`. A stance with projected center `0.53` has clearance `0.07` and does not. Hidden B cases include ties with a deterministic ID rule, empty candidate lists, reordered contacts, and candidates whose load projection is outside the contacts. The task card does not name the abstraction made in A or instruct B to import it.

## Prompt and artifact separation

- The agent-visible A prompt contains the frozen catalog, task A, resource limits, and tool instructions. It contains no evaluator terminology or example solution.
- The artifact-condition B prompt contains the frozen catalog, task B, and normal discovery access to A's admitted module interface and examples. It contains no A conversation history.
- The no-carry B prompt has no A artifact or A history.
- The text-only B prompt has a size-matched, plain-language account of A's derived idea. It has no importable module or executable definitions.
- The evaluator keeps oracle formulas, held-out inputs, scoring code, and condition labels out of all agent-visible files. Publish task-generator seed and oracle hash after runs.

## Required run artifacts

Archive the frozen environment contract; A and B task cards; agent-visible prompts; all candidate source packages; manifest and semantic-artifact hashes; composition, parse, type, lowering, preflight, and execution diagnostics with origins; admitted snapshot identity; B source and resolved import/call trace; oracle result; timing and token usage; and the completed [run record](run-record.schema.json). Record the model, configuration, seed, condition, and tool versions needed to replay the run.
