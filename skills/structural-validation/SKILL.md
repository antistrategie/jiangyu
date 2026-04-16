---
name: jiangyu-structural-validation
description: Use when doing Jiangyu template/support-type structural validation passes against MENACE game data. Follows the repo validation workflow, stays in the serialized-contract lane, and writes the standard investigation/progress outputs.
---

# Jiangyu Structural Validation

Use this skill for **structural validation passes** in the Jiangyu repo.

The broad structural sweep is complete. This skill is now mainly for:

- edge-case audits
- post-update re-audits
- feature-driven contract checks
- narrowly scoped follow-up validation

This is for validating:

- template field sets
- nested support types
- array element types
- claims about the **current serialized contract**

Do **not** use this skill for:

- runtime behavior
- formulas
- offsets
- call chains
- managed inheritance deep-dives
- general IL2CPP code reverse engineering

Those are deeper managed/runtime validation, not structural validation.

## Required reading

Read these first:

- `AGENTS.md`
- `docs/research/VALIDATION.md`
- `docs/research/STRUCTURAL_VALIDATION_WORKFLOW.md`
- `TODO.md`

Then read only the latest relevant notes under `docs/research/investigations/` for the target area.

At minimum, check:

- the most recent support-type spot-checks
- `docs/research/investigations/2026-04-15-next-support-type-candidates.md` if choosing the next target

## Scope rules

- Stay in the **serialized contract** lane.
- Use Jiangyu-native evidence first.
- Compare across multiple Jiangyu samples before drawing conclusions.
- Do not broaden into semantics/runtime behavior unless explicitly recording that as **not validated**.

If the question depends on:

- nonserialized fields
- managed inheritance/base layout
- offsets or call chains
- runtime behavior or formulas

stop and report that the task is trying to escalate out of structural validation.

## First response rule

Before editing any files, report back with:

- chosen target
- planned samples
- whether the pass is expected to stay purely structural

Wait for approval before writing notes or editing files.

## Standard workflow

Follow `docs/research/STRUCTURAL_VALIDATION_WORKFLOW.md` exactly.

In practice:

1. State the target narrowly.
2. Pick real samples from Jiangyu's own inventory.
3. Inspect with the built `jiangyu` CLI.
4. Isolate the specific nested object/field.
5. Compare across Jiangyu samples.
6. Classify the result.
7. State what was and was not validated.
8. Record the next best target.

## Commands

Prefer the already-built CLI DLL if local `dotnet build` is blocked:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates list --type EntityTemplate
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_squad.darby
```

If you need machine-readable output, strip progress lines before parsing JSON:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_squad.darby \
  | awk 'BEGIN{flag=0} /^[{]/{flag=1} flag{print}' > /tmp/sample.json
```

## Outputs

After approval to proceed, produce only what the pass needs:

1. A new note under `docs/research/investigations/`
2. A short factual `PROGRESS.md` entry if the result materially advances verified knowledge
3. A `TODO.md` update only if the result changes what should be investigated next

Do not rewrite unrelated docs.

## Note shape

Use this structure:

```md
# Support-Type Structural Spot-Check: <TypeName>

Date: YYYY-MM-DD

## Goal

## Why This Type

## Samples

## Method

## Results

## Interpretation

## Conclusion

## Next Step
```

## Current examples

Use these as style references:

- `docs/research/investigations/2026-04-15-localized-support-type-spot-check.md`
- `docs/research/investigations/2026-04-15-roledata-support-type-spot-check.md`
- `docs/research/investigations/2026-04-15-entityproperties-support-type-spot-check.md`
- `docs/research/investigations/2026-04-15-prefabattachment-support-type-spot-check.md`
