# Structural Validation Workflow

This is the concrete step-by-step process Jiangyu uses for a **structural validation pass**.

Use this when you want to validate:

- a template type
- a nested support type
- a field set
- a claim about current serialized structure

This is narrower than full reverse engineering. The goal is to validate the **current serialized contract**, not runtime behavior or formulas.

If you need the broader policy and evidence rules, read [VALIDATION.md](./VALIDATION.md) first. This document is the operational playbook.

## What to call this work

Inside Jiangyu, call this:

- **structural validation**
- **serialized contract validation**
- **schema spot-check**

If you were searching for adjacent concepts, the closest terms would be:

- Unity serialized data model validation
- Unity IL2CPP asset schema validation
- reverse-engineering serialized contract validation

## Inputs

You need:

- the built `jiangyu` CLI DLL already present in the repo
- Jiangyu's template inventory
- one or more real sample objects from the live game data

Use the built CLI DLL if local `dotnet build` is currently blocked by the known AssetRipper/MSBuild task-host issue.

## Output

Each pass should produce:

- one research note under `docs/research/investigations/`
- one short `PROGRESS.md` entry if the result materially advances Jiangyu's verified knowledge
- optional `TODO.md` refinement if the result changes what should be investigated next

## Workflow

### 1. State the target

Write down exactly what you are validating.

Good examples:

- `EntityTemplate` field set
- `LocalizedLine` serialized wrapper shape
- `RoleData` nested support-type shape under `EntityTemplate`

Bad examples:

- localization
- AI
- templates in general

Keep the target narrow.

### 2. Pick real samples from Jiangyu's own inventory

Use Jiangyu to discover real instances first.

Example:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates list --type EntityTemplate
```

Pick at least:

- two samples for a support type or template type you want to compare structurally
- more if the type looks polymorphic or highly optional

Bias toward:

- one "normal" sample
- one meaningfully different sample

### 3. Inspect the samples with Jiangyu

Use `templates inspect` for template-backed data.

Example:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_squad.darby
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name enemy.pirate_scavengers
```

If you want to machine-compare the output, strip the progress lines and save the JSON:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_squad.darby \
  | awk 'BEGIN{flag=0} /^[{]/{flag=1} flag{print}' > /tmp/darby.json
```

### 4. Isolate the structural target

Focus only on the specific object or field you are validating.

Examples:

- `m_Structure`
- `AIRole`
- `Title`
- `Description`

Do not try to validate the entire template if the actual target is a nested support type.

### 5. Compare across Jiangyu samples

Answer:

- does the field exist in all chosen samples?
- is the `fieldTypeName` stable?
- is the nested field set stable?
- do obvious primitive/enum/reference kinds match?

This is the most important step.

### 6. Classify the result

Use simple categories:

- `stable across samples`
- `sample-dependent`
- `too sparse to conclude`
- `real mismatch`
- `still unknown`

### 7. State what was and was not validated

Every note should separate:

- what this pass validates
- what this pass does not validate

For example, a support-type structural pass usually does **not** validate:

- runtime behavior
- formulas
- full managed inheritance/base layout
- memory offsets
- nonserialized managed fields

This is how we avoid overclaiming.

### 8. Record the next best target

Each pass should leave behind the next likely target:

- another recurring support type
- an outlier template
- a semantic follow-up if structure is now stable

That keeps the validation thread moving.

## Decision rule

Treat a structural pass as successful when:

- Jiangyu independently reproduces the current serialized shape from live game data
- the shape is stable enough across the chosen samples

You do **not** need to prove runtime semantics for the pass to be useful.

## Typical note structure

Use this shape:

```md
# Structural Spot-Check: <TypeName>

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

Reference examples already in the repo:

- `docs/research/investigations/2026-04-14-entity-weapon-schema-spot-check.md`
- `docs/research/investigations/2026-04-15-localized-support-type-spot-check.md`
- `docs/research/investigations/2026-04-15-roledata-support-type-spot-check.md`
- `docs/research/investigations/2026-04-15-entityproperties-support-type-spot-check.md`
- `docs/research/investigations/2026-04-15-prefabattachment-support-type-spot-check.md`
