# Polymorphic Reference Arrays

Verified structural pattern: MENACE uses polymorphic ScriptableObject reference arrays as a
cross-cutting design for composing template behaviour.

## The pattern

A parent template holds a `List<T>` field where `T` is a ScriptableObject-derived base type.
Each array element is a PPtr reference to a separate MonoBehaviour asset. The concrete type of
each element can differ — the array is heterogeneous at the instance level while homogeneous at
the declared type level.

This is distinct from inline embedded arrays (like `EntityLootEntry` or `PrefabAttachment`),
where the element data lives directly inside the parent template's serialised blob.

## Confirmed polymorphic fields

6 fields confirmed across 3 field families, validated by resolving PPtr references to their
concrete types via `jiangyu templates inspect` and `jiangyu templates list --type`.

### EventHandlers — `List<SkillEventHandlerTemplate>`

| Parent template | Field | Concrete subtypes observed |
|---|---|---|
| SkillTemplate | `EventHandlers` | 15+ distinct handler types (Attack, Damage, AddSkill, ChangeProperty, SpawnEntity, etc.) |
| PerkTemplate | `EventHandlers` | 8+ distinct handler types (ChangePropertyConditional, AddSkill, DisplayText, SetEntityFlag, etc.) |

PerkTemplate inherits `EventHandlers` from SkillTemplate. The field type is the same
(`List<SkillEventHandlerTemplate>`), but perks use a partially overlapping set of concrete
handler subtypes. 17 distinct concrete handler types confirmed across both template types.

### Items — `List<ItemTemplate>`

| Parent template | Field | Concrete subtypes observed |
|---|---|---|
| EntityTemplate | `Items` | ArmorTemplate, WeaponTemplate, AccessoryTemplate |

The widest polymorphic array observed: a single entity can carry armour, weapons, and
accessories in the same `Items` array. Concrete types verified by pathId lookup against
`templates list` output.

### Skills — `List<SkillTemplate>`

| Parent template | Field | Concrete subtypes observed |
|---|---|---|
| EntityTemplate | `Skills` | SkillTemplate, PerkTemplate |

Sparse — populated primarily on vehicle entities for direct skill grants. Establishes that
PerkTemplate inherits from SkillTemplate at the serialised level.

## Observed monomorphic reference arrays

These fields contain only one concrete type across all sampled data. The declared type may
permit subtypes, but none were observed.

| Field | Declared element type | Observed type | Notes |
|---|---|---|---|
| EntityTemplate.Tags | `List<TagTemplate>` | TagTemplate | 5 entities, 1–4 tags each |
| EntityTemplate.Decoration | `List<PrefabListTemplate>` | PrefabListTemplate | buildings only |
| EntityTemplate.SmallDecoration | `List<PrefabListTemplate>` | PrefabListTemplate | buildings only |
| EntityTemplate.DestroyedDecoration | `List<PrefabListTemplate>` | PrefabListTemplate | buildings only |
| EntityTemplate.SkillGroups | `List<SkillGroup>` | SkillGroup | reference wrapper, not polymorphic |
| EntityTemplate.DefectGroups | `List<DefectGroup>` | DefectGroup | reference wrapper, vehicles only |
| FactionTemplate.Operations | `OperationTemplate[]` | OperationTemplate | 2 factions |
| FactionTemplate.EnemyAssets | `EnemyAssetTemplate[]` | EnemyAssetTemplate | 1 faction |
| Equipment.SkillsGranted | `List<SkillTemplate>` | SkillTemplate | could hold PerkTemplate in unsampled data |

Monomorphic classification is sample-based, not universal. Fields with wider inheritance
hierarchies (especially `Equipment.SkillsGranted`) may show polymorphism in unsampled instances.

## Reference wrapper types

Three non-Template-named ScriptableObject types serve as thin grouping wrappers. Each contains
a single list-of-template-references field:

| Wrapper | Contents | Instances |
|---|---|---|
| SkillGroup | `Skills: List<SkillTemplate>` | multiple |
| DefectGroup | `Defects: List<DefectTemplate>` | multiple |
| TileEffectGroup | `TileEffects: TileEffectTemplate[]` | 1 |

These are separate MonoBehaviour assets linked by PPtr from parent templates. They are not
inline embedded types. Structurally parallel to each other — all three have one field, no
DataTemplate base fields, no Odin container.

## Why this matters

The polymorphic reference array pattern has direct implications for template patching:

- **Patching inline arrays** means modifying data inside the parent template's serialised blob.
- **Patching reference arrays** means patching independent MonoBehaviour assets linked by PPtr.
  The parent template only holds references; the actual data lives in separate objects.
- **Adding elements to polymorphic arrays** requires creating new MonoBehaviour assets of the
  correct concrete subtype and inserting PPtr references into the parent's array.

## Validation method

31 template instances inspected across 6 template types. Concrete types verified by resolving
PPtr pathId values against `jiangyu templates list --type <Type>` output. Every observed
polymorphic field matches a legacy-declared polymorphic type. Every monomorphic field is
consistent with (but narrower than) its legacy inheritance hierarchy.

## Investigation notes

- `legacy/2026-04-15-polymorphic-reference-arrays-cross-template-survey.md` — primary survey
- `legacy/2026-04-15-entitytemplate-array-element-types-spot-check.md` — SkillGroup/DefectGroup structural confirmation
