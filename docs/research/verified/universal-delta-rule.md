# Universal Delta Rule

Verified structural principle governing the relationship between Jiangyu's observed serialised
field sets and legacy schema field sets across all MENACE template types.

## The rule

Jiangyu sees the Unity-native serialised contract. Legacy schemas may include additional fields
that Unity's serialisation rules exclude. `serializationData` is the bridge when Odin is involved.

Three independent effects produce every observed delta:

1. **Base class exclusions.** Fields on `DataTemplate` or its ancestors that are marked
   `NotSerialized` or are private without `SerializeField`. These exist in managed code but
   Unity does not write them to serialised assets. Affects DataTemplate descendants only.

2. **Odin-routed fields.** Fields whose declared types are interfaces, abstract classes, or
   non-Unity-serialisable collection types (e.g. `HashSet<T>`). Unity's native serialiser
   skips them entirely. The game routes their data through Odin Serializer (Sirenix) instead,
   storing it inside the `serializationData` blob.

3. **The `serializationData` container.** A concrete `Sirenix.Serialization.SerializationData`
   field that Jiangyu observes but legacy schemas omit. It holds the Odin-encoded payload for
   any Odin-routed fields on the type.

For DataTemplate descendants, effects (1) and (2) co-occur. For non-DataTemplate types that
inherit directly from `SerializedScriptableObject`, only effect (2) applies.

## Evidence

Validated across all 8 template families in the full top-level audit sweep. Zero real
mismatches across the entire inventory.

### DataTemplate descendants (4+1 base pattern plus optional Odin fields)

| Template type | Jiangyu fields | Legacy fields | Base class exclusions | Odin-routed | serializationData |
|---|---|---|---|---|---|
| EntityTemplate | 109 | 112 | 4 (`m_ID`, `m_IsGarbage`, `m_IsInitialized`, `m_LocalizedStrings`) | 0 | yes (present, not assessed) |
| WeaponTemplate | 50 | 54 | 4 + `ItemType` (managed-only on base) | 0 | yes |
| SkillTemplate | 120 | 128 | 4 | 5 (`CustomAoEShape`, `AoEFilter`, `ProjectileData`, `SecondaryProjectileData`, `AIConfig`) | yes (non-empty, varies by skill category) |
| PerkTemplate | 121 | 129 | 4 (inherited from SkillTemplate) | 5 (inherited from SkillTemplate) | yes (inherited) |
| AnimationSoundTemplate | audited, standard pattern | — | 4 | 0 | yes |
| TileEffectTemplate | 13 | 16 | 4 | 0 | yes (empty — no Odin-routed base fields) |
| TagTemplate | 9 | 12 | 4 | 0 | yes (empty — no Odin-routed fields at all) |

### Non-DataTemplate type (Odin-only pattern)

| Template type | Jiangyu fields | Legacy fields | Base class exclusions | Odin-routed | serializationData |
|---|---|---|---|---|---|
| DefectTemplate | 4 | 5 | 0 (not a DataTemplate descendant) | 2 (`DisqualifierConditions`, `SkillsRemoved`) | yes (non-empty, varies by instance) |

### The four base class exclusions

These four fields appear as legacy-only across every DataTemplate descendant:

- `m_ID` — `NotSerialized` attribute
- `m_IsGarbage` — `NotSerialized` attribute
- `m_IsInitialized` — `NotSerialized` attribute
- `m_LocalizedStrings` — private, no `SerializeField` attribute

They are managed-only runtime fields on the `DataTemplate` base class. Unity's serialisation
rules exclude them from the serialised asset contract. Their absence from Jiangyu's output is
correct behaviour, not an extraction failure.

### Odin-routed field types observed

Fields routed through Odin share a common trait: their declared type cannot pass Unity's
native serialisation filter.

| Declared type | Reason excluded | Template types affected |
|---|---|---|
| `ICustomAoEShape` (interface) | Unity cannot serialise interface references | SkillTemplate, PerkTemplate |
| `ITacticalCondition` (interface) | Unity cannot serialise interface references | SkillTemplate, PerkTemplate, DefectTemplate |
| `BaseProjectileData` (abstract class) | Unity cannot serialise abstract references | SkillTemplate, PerkTemplate |
| `SkillBehavior` (likely abstract/Odin-routed) | absent from Unity's serialised type tree | SkillTemplate, PerkTemplate |
| `HashSet<SkillTemplate>` (non-Unity collection) | Unity does not natively serialise `HashSet<T>` | DefectTemplate |

## Validation method

Each template type was audited by:

1. inspecting multiple Jiangyu-native samples across distinct content categories using
   `jiangyu templates inspect`
2. comparing the observed field set across samples for stability (all stable — zero
   intra-type variation)
3. computing the set intersection and symmetric difference against the legacy schema
4. classifying every delta field by checking managed metadata (`NotSerialized` attributes,
   `SerializeField` presence, type abstractness/interface status)

## Scope and limits

This rule covers the serialised field set — which fields appear in the Unity-serialised asset
data. It does not cover:

- Odin blob contents (the `SerializedBytes` payload has not been decoded)
- runtime behaviour or managed-only fields that exist only in code
- field semantics, values, or formulas
- future game updates that could change serialisation rules

## Investigation notes

- `legacy/2026-04-14-entity-weapon-schema-spot-check.md` — first observation of the pattern
- `legacy/2026-04-15-skilltemplate-top-level-field-audit.md` — first Odin-routing classification
- `legacy/2026-04-15-defecttemplate-top-level-field-audit.md` — refined to universal rule
- `legacy/2026-04-15-perktemplate-top-level-field-audit.md` — inheritance carries the pattern
- `legacy/2026-04-15-tileeffecttemplate-top-level-field-audit.md` — abstract type confirmation
- `legacy/2026-04-15-tagtemplate-top-level-field-audit.md` — final confirmation, completes sweep
