# Classifier Gap

Verified assessment of which template-like types are invisible to Jiangyu's `TemplateClassifier`
and the bounded shape of the gap.

## The gap

`TemplateClassifier` discovers MonoBehaviour script names ending with `"Template"`. Two abstract
template families have concrete subtypes that do not follow this naming convention. These
subtypes are structurally template-like (carry DataTemplate base fields, live in `resources.assets`
as named MonoBehaviours) but are invisible to the classifier.

### Affected families

| Abstract base | Concrete subtypes | Naming pattern | Instances | Subtype count |
|---|---|---|---|---|
| TileEffectTemplate | `ApplySkillTileEffect`, `SpawnObjectTileEffect`, etc. | `*TileEffect` | 23 | 7 |
| SkillEventHandlerTemplate | `Attack`, `Damage`, `AddSkill`, etc. | diverse (no consistent suffix) | ~4,500 | 119+ |

Together these account for roughly one third of all MonoBehaviours in `resources.assets`.

### Reference wrappers (minor gap)

Three non-Template-named ScriptableObject types serve as grouping wrappers:

| Wrapper | Instances | In legacy schema? |
|---|---|---|
| SkillGroup | multiple | yes |
| DefectGroup | multiple | yes |
| TileEffectGroup | 1 | no (newly discovered) |

These are not directly patchable data objects. They exist as PPtr reference targets from
template fields.

## What the survey found

A broad survey of all 13,448 MonoBehaviours in `resources.assets` was conducted, cross-referencing
Jiangyu's template index (73 types) against the legacy schema (84 types) and sampling across
all major MonoBehaviour name prefix groups.

### No additional hidden families

- All 11 legacy-only types are accounted for: 7 are abstract base classes with no instances,
  2 are the abstract bases of the gap families above, 1 is an inline struct misclassified as
  a MonoBehaviour in the legacy schema, 1 has zero instances
- The anonymous MonoBehaviour population (4,544 entries) is entirely runtime components
  (HDRP lighting, animation controllers, UI toolkit) — no template-like data objects
- Every named MonoBehaviour group resolves to a known Template-named type, a known handler
  subtype, a known wrapper type, or a non-game engine type
- 7 of 9 abstract template families have exclusively Template-named concrete subtypes

### The two gap families are structurally distinct

**TileEffectTemplate:** the abstract base is correctly Template-named and would be classifiable,
but has no instances. All 23 instances use concrete subtype names that do not end with
"Template". The legacy schema records the abstract base but has no knowledge of any concrete
subtype.

**SkillEventHandlerTemplate:** the abstract base is also Template-named. Its 119+ concrete
subtypes use diverse names with no consistent suffix. The legacy schema lists the abstract base
and records the inheritance hierarchy. 15 concrete handler types have been structurally validated
by Jiangyu.

## Scope and limits

This assessment is bounded to the current `resources.assets` collection and the surveyed
template families. It does not guarantee completeness across:

- other asset collections
- future game updates
- types loaded at runtime from different sources

The gap is structural (naming convention) rather than functional (missing data). The underlying
MonoBehaviours exist in the asset data and are inspectable by collection + pathId — they are
just not indexed by the template classifier's name-based discovery.

## Validation method

1. Extracted all unique name prefixes from 13,448 MonoBehaviours (80+ prefix groups)
2. Stratified sampling: resolved `m_Script` PPtr references on representative entries from
   each group to discover actual script class names
3. Sampled 23 anonymous entries (every 200th of 4,544) for script class diversity
4. Sampled 14 named groups spanning diverse prefixes
5. Traced all 9 legacy abstract template families for concrete subtypes
6. Cross-referenced Jiangyu template index against legacy schema to identify coverage gaps
7. Inspected specific anomalies: TileEffectGroup, DecalCollection/DecalTemplate,
   MissionStrategicAssetTemplate

## Investigation notes

- `legacy/2026-04-15-classifier-gap-assessment.md` — primary assessment
- `legacy/2026-04-15-tileeffecttemplate-top-level-field-audit.md` — concrete subtype census and discovery path
