# Verified Research

Jiangyu-owned structural findings promoted from investigation work.

Use [`../README.md`](../README.md) for the research map and current status. This directory is
just the local index for promoted findings.

## Rules and structural patterns

- [Universal delta rule](universal-delta-rule.md) — why Jiangyu's serialised field set differs from legacy schemas, and how the two relate
- [Polymorphic reference arrays](polymorphic-reference-arrays.md) — the cross-cutting ScriptableObject reference array pattern and which fields use it
- [Classifier gap](classifier-gap.md) — which template-like types the classifier cannot see and why the gap is bounded

## Verified contracts

- [EntityTemplate](entitytemplate-contract.md) — top-level template contract (109 serialised fields), including inline vs reference array classification
- [EntityProperties](entityproperties-contract.md) — 102-field gameplay-stat support contract nested inside every EntityTemplate
- [Array element contracts](array-element-contracts.md) — PrefabAttachment, EntityLootEntry, and SkillOnSurfaceDefinition as validated inline embedded types
- [UnitLeaderTemplate.InitialAttributes](unitleader-initial-attributes.md) — 7-byte save-frozen starting stats, enum-ordered offset table, readback-verified

## Verified runtime paths

- [Texture2D replacement](texture-replacement.md) — convention-first `Texture2D` replacement via in-place mutation, inherited by every consumer (materials, UGUI, UI Toolkit, caches)
- [Sprite replacement](sprite-replacement.md) — convention-first `Sprite` replacement for unique-texture-backed sprites; atlas-backed sprites rejected at compile time
- [AudioClip replacement](audio-replacement.md) — convention-first `AudioClip` replacement via playback-time substitution hooked on `AudioSource` play entry points

Each doc lists the investigation notes it draws from.
