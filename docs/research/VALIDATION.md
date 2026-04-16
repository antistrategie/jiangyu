# Research And Validation

This document defines how Jiangyu turns reverse-engineering work into trusted project knowledge.

The goal is simple:

- research should produce reproducible evidence
- evidence should be clearly separated from interpretation
- only verified claims should shape production code, manifests, and public docs

## Why this exists

Jiangyu depends on recovered knowledge about a specific Unity IL2CPP game.

That kind of knowledge is easy to get wrong. Decompiler output can mislead, old notes can cargo-cult incorrect conclusions, and one-off successful experiments can hide fragile assumptions.

So Jiangyu needs a consistent promotion path:

1. observe something
2. record it as a hypothesis
3. reproduce it with Jiangyu-native tools or controlled tests
4. promote it to verified knowledge

## Definitions

### Observation

A raw fact seen in data, code, assets, or runtime behavior.

Examples:

- a mesh named `local_forces_basic_soldier_LOD0` exists
- a material has `_MaskMap`
- a decompiled method reads offset `0x58`

An observation is not automatically an interpretation.

### Hypothesis

A proposed meaning or rule derived from observations.

Examples:

- offset `0x58` is probably `Damage`
- `_Effect_Map` likely controls emissive behavior
- this handler fires on kill

Hypotheses are allowed in research notes, but not as production truth.

### Verified claim

A claim Jiangyu has reproduced with:

- live game data
- Jiangyu extraction / inspection tooling
- a controlled in-game experiment
- a regression test or fixture

Verified claims may be promoted into production docs, compiler logic, runtime assumptions, or tests.

## Evidence hierarchy

Use stronger evidence over weaker evidence.

From strongest to weakest:

1. Reproducible in-game behavior from a minimal isolated test
2. Jiangyu-native extraction / inspection output against live game data
3. Direct serialized asset inspection
4. IL2CPP metadata and decompiled code
5. External extraction tool output
6. Legacy project notes, schemas, and generated artifacts
7. Intuition

Rules:

- stronger evidence can overrule weaker evidence
- weaker evidence can suggest hypotheses, but not finalize them
- if two sources disagree, prefer the stronger source

## Claim categories

Different claims need different validation methods.

### Existence claims

Examples:

- a type exists
- a field exists
- an asset exists

Preferred validation:

- Jiangyu search / inspect commands
- direct extraction output
- serialized asset inspection

### Layout claims

Examples:

- field type is `int`
- field order is stable
- a struct contains these members

Preferred validation:

- serialized asset structure
- typetree or equivalent metadata
- multiple examples from live data

### Semantic claims

Examples:

- this field means crit chance
- this enum value means `OnKill`

Preferred validation:

- controlled edits with one variable changed at a time
- runtime observation
- comparison across multiple known assets/templates

### Formula claims

Examples:

- damage stacking math
- effect timing rules

Preferred validation:

- minimal in-game experiments
- logging around a single mechanic
- repeated test cases with known expected changes

### Shader / material claims

Examples:

- `_MaskMap` channel meaning
- `_Effect_Map` usage

Preferred validation:

- compare original and modified materials
- isolate one property at a time
- validate with render output, not naming guesses alone

## Promotion rules

A claim may be promoted from research to Jiangyu truth only when all of the following are true:

- the claim is written clearly enough to test
- the validation source is recorded
- the game version or asset version is recorded when relevant
- the claim has been reproduced by Jiangyu tooling, a controlled runtime experiment, or a regression test
- the result is specific enough to survive re-checking later

Until then, keep it marked as:

- `legacy`
- `unverified`
- `hypothesis`

Do not encode unverified claims into:

- production compiler logic
- runtime replacement assumptions
- public manifest contracts
- user-facing documentation phrased as fact

## Required outputs from research

Research is only useful long-term if it leaves behind something reproducible.

Preferred outputs:

- a new Jiangyu inspect command
- a regression test
- a fixture / sample asset
- a validated note in docs
- an explicit compiler/runtime invariant

Avoid research that ends only as:

- a chat conclusion
- an undocumented manual memory
- an untraceable generated blob

## Investigation end state

Investigation notes are a transitional form of knowledge, not the intended final state of
the project.

The end state Jiangyu is aiming for is:

- Jiangyu-owned verified docs under `docs/research/verified/`
- machine-readable validation artifacts under `validation/`
- live tooling that can re-check the important structural claims against current game data

Investigation notes remain in `docs/research/investigations/` with full methodology and raw
observations. Verified findings are promoted into `docs/research/verified/`.

## Structural baseline

Jiangyu's current chosen durable output for the structural-validation phase is a
**machine-generated structural baseline**.

This baseline should:

- live outside `docs/` because it is a machine-readable artifact
- be committed to git
- be generated by Jiangyu tooling, not handwritten
- be reviewed by humans before baseline changes are committed

Current planned location:

```text
validation/template-structure-baseline.json
```

### What it is for

The structural baseline is for:

- post-update drift detection
- structural diffing
- audit tooling
- preserving the currently validated serialized contract in a reproducible form

It is **not** meant to become the runtime or compiler source of truth.
Jiangyu should continue to derive truth from live game data whenever possible.

### What goes into it

The baseline should contain:

- validated template top-level types
- validated support types
- observed structural facts only

For each type entry, include:

- `typeName`
- `category` (`template` or `supportType`)
- `fieldCount`
- `sampleNames`
- `fields`

For each field entry, include:

- `name`
- `kind`
- `fieldTypeName`
- `elementTypeName` when relevant for arrays

Field order should be preserved by list order. `sampleNames` and type entries should be
sorted deterministically.

### What stays out

The baseline should not contain:

- legacy framing or comparisons
- interpretive rules
- semantic claims
- promotion status
- prose narration

Those belong in research notes, docs, and later promotion decisions, not in the baseline
artifact itself.

### Current JSON shape

The first version should stay simple:

```json
{
  "generatedAt": "2026-04-16T12:00:00Z",
  "gameAssemblyHash": "abc123",
  "types": [
    {
      "typeName": "EntityTemplate",
      "category": "template",
      "fieldCount": 87,
      "sampleNames": ["player_squad.darby", "enemy.pirate_scavengers"],
      "fields": [
        {
          "name": "Properties",
          "kind": "object",
          "fieldTypeName": "Menace.Tactical.EntityProperties"
        }
      ]
    }
  ]
}
```

Keep it to a single baseline file at first. Split only if it becomes unwieldy.

## Standard workflow

Use this workflow for new research.

1. State the claim.
2. Classify the claim type.
3. Record current evidence.
4. Decide the minimum stronger evidence needed.
5. Create the smallest Jiangyu-native validation path.
6. Record the result.
7. Either:
   - promote to verified knowledge
   - keep as unverified
   - reject as false

For the concrete step-by-step process Jiangyu is currently using for template/support-type schema spot-checks, see [STRUCTURAL_VALIDATION_WORKFLOW.md](./STRUCTURAL_VALIDATION_WORKFLOW.md).

## Validation templates

When recording a claim, use this shape:

```md
Claim:
Source:
Game version:
Evidence category:
Validation method:
Result:
Confidence:
Follow-up:
```

Example:

```md
Claim: MENACE soldier shader uses `_Effect_Map` as a real texture slot.
Source: Live material inspection + runtime texture replacement test.
Game version: Unity 6 / MENACE current dev target.
Evidence category: Shader / material claim.
Validation method: Inspect original material properties, replace only `_Effect_Map`, compare render output.
Result: Verified that the shader reads `_Effect_Map`; channel semantics still unverified.
Confidence: High.
Follow-up: Determine channel meaning.
```

## Anti-patterns

Do not do these:

- promote a decompiler guess straight into production logic
- trust legacy schemas because they look detailed
- mix raw observation with semantic interpretation without labeling the difference
- validate multiple unknowns at once
- keep knowledge only in generated files without provenance
- call something verified when it only worked once in an uncontrolled test

## External sources

Artifacts from older tooling or external projects are not source of truth for Jiangyu.

They may suggest hypotheses to test, but should not be treated as authoritative schemas,
final field meanings, proof of runtime behavior, or architectural direction.

Jiangyu's own verified knowledge lives under `docs/research/verified/` and
`validation/`.

## Desired long-term state

Jiangyu should gradually replace fragile inherited knowledge with:

- verified Jiangyu-native inspection tools
- reproducible test cases
- narrow documented invariants
- explicit provenance for every important claim

That is what makes the project durable.
