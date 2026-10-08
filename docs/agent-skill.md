# The Nitrogen agent skill and semantic catalog

The [Nitrogen agent skill](../.agents/skills/nitrogen/SKILL.md) applies Nitrogen's approach (typed capabilities, checked before they run) across projects. It frames a task as typed capability requirements, searches a separate semantic catalog for concepts and evidence, validates a solution before authorized execution, and proposes reusable observations through a reviewable pull request. A catalog concept may guide work in another language; it is not necessarily a runnable Nitrogen module.

The private catalog is `TovarishN/Nitrogen.Concepts`. With access to that repository, clone it beside Nitrogen and point the skill at the checkout:

```sh
git clone git@github.com:TovarishN/Nitrogen.Concepts.git ../Nitrogen.Concepts
export NITROGEN_CONCEPT_CATALOG="$(cd ../Nitrogen.Concepts && pwd)"
```

To make the maintained skill available to Codex from other projects, run the following from this Nitrogen repository after checking that the destination does not already contain another skill:

```sh
mkdir -p "$HOME/.codex/skills"
cp -R .agents/skills/nitrogen "$HOME/.codex/skills/nitrogen"
```

After updating Nitrogen, copy the updated skill files into that installed directory so the personal installation stays current. This environment uses a regular directory because its filesystem sandbox does not support a symlinked skill root.

The skill also checks for an existing sibling catalog checkout. If the private repository is unavailable, the agent can finish the main task and keep catalog findings as an unpublished local draft. Catalog publication does not grant permission to execute a module: a receiving host must validate its exact contract and admission requirements.
