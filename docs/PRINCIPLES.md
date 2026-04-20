# Jiangyu Principles

## Project Aim

Jiangyu is a MENACE modkit that keeps common modding paths simple and builds on verified contracts.

The long-term goal is a toolkit where:

- simple replacements stay simple
- data-driven changes do not require code
- complex authored Unity content is possible when genuinely needed
- new content is built by deriving from known-good game contracts rather than inventing unknown ones from scratch
- research findings are promoted into production truth only after Jiangyu has reproduced them

Jiangyu should be a strong modkit, not a full mod manager platform.

## Intended Users

Jiangyu should support multiple levels of modder:

- modders who only want to replace assets
- modders who want to patch or clone data/templates
- modders who need Unity Editor workflows for complex authored content
- modders who need custom runtime code through an SDK

These should feel like progressive layers, not unrelated systems.

## Core Principles

### 1. Simple Things Stay Simple

Basic modding tasks should not require code or Unity Editor setup.

This includes, where possible:

- mesh/material/texture/audio replacement
- simple template patching
- simple template cloning once the contract is validated

If a straightforward content change needs custom code or a full Unity authoring workflow, Jiangyu is making the common case too hard.

### 2. Correctness Before Convenience on Foundation-Critical Paths

Any assumption that becomes part of:

- compiler output
- loader behaviour
- public mod format
- trusted schema
- canonical exported asset contract

must be verified before production code depends on it.

Convenience features are welcome, but not at the cost of baking unverified assumptions into the foundation.

### 3. Derive From Known-Good Game Contracts

When Jiangyu adds support for new content, it should prefer:

- cloning existing templates
- cloning or redirecting existing prefab/content references
- selectively overriding visual or data payloads

over:

- constructing unknown game-native contracts from scratch

Jiangyu should bias toward conservative derivation from real game content until stronger guarantees exist.

### 4. Data First, Code When Necessary

If a mod can be expressed through assets, templates, patches, or declarative content, it should not require custom C#.

Code should be reserved for:

- genuinely new runtime behaviour
- advanced hooks
- UI/logic extensions
- cases where the game itself is not sufficiently data-driven

### 5. CLI First, Editor Additive

The CLI should remain Jiangyu's primary workflow and automation surface.

Unity Editor integration, if added, should be:

- additive
- targeted at complex authored Unity content
- not required for the basic replacement and patching workflows

A future GUI should help with browsing, inspection, validation, and management. It should not become a second full authoring system by accident.

### 6. Keep Layers Honest

Jiangyu should keep these concerns distinct:

- serialised asset truth
- managed/code-side truth
- runtime behavioural truth
- research hypotheses and historical notes

These layers inform each other, but they are not interchangeable.

### 7. Prefer Explicit Contracts Over Guessing

When Jiangyu knows the real authored or compiled contract, prefer explicit mappings over heuristics.

Examples:

- explicit per-material texture bindings are better than mesh-wide texture-family guessing
- explicit dependency/load behaviour is better than accidental filesystem order

Heuristics are acceptable as scoped bridges, not as invisible permanent truth.

For common replacement workflows, Jiangyu should still prefer convention over repetitive manifest bookkeeping:

- `assets/replacements/` is the primary source of truth for standard replacement inputs
- `jiangyu.json` should describe metadata and exceptional cases, not force modders to enumerate every routine replacement by hand
- explicit manifest mappings should remain available as the escape hatch for ambiguous or nonstandard cases

### 8. Be a Modkit, Not a Platform

Jiangyu should own:

- build/compile workflows
- runtime loading
- mod format
- dependency handling
- conflict/load behaviour
- packaging/install ergonomics

It should not prematurely expand into:

- registry hosting
- full mod manager UX
- ecosystem platform services

unless that becomes clearly necessary later.

## Decision Rules

When facing a structural decision, prefer the option that:

1. keeps simple workflows simple
2. depends on verified contracts rather than assumptions
3. derives from known-good game content instead of inventing unknown structure
4. preserves a clean boundary between Core, CLI, Loader, and future Editor/GUI roles
5. keeps Jiangyu focused on being a trustworthy modkit

When those goals conflict, foundation correctness should win over short-term convenience.
