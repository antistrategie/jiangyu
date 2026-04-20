# KDL Authoring Templates CLI Audit

Date: 2026-04-20

## Scope

Audit the existing `jiangyu templates` CLI surface against the five-step
template-authoring workflow a modder needs for the current
`templatePatches` / `templateClones` `jiangyu.json` contract.

This note is about the static authoring UX only. It does not cover runtime
inspection, in-game validation, or future KDL syntax design.

## Workflow audit

### 1. Discover which template types exist

`jiangyu templates list` already answers this by printing every indexed
template class name and instance count, but it is missing any search/filter
surface when the modder only remembers part of a type name; the first thing a
modder would try is `jiangyu templates list`.

### 2. Discover which instances of a type exist and how to identify them

`jiangyu templates list --type WeaponTemplate` already answers this by
printing each instance's `Name`, type, collection, and `pathId`, but it is
missing case-insensitive substring search across template name and collection
for the common "I do not remember the exact template ID" workflow; the first
thing a modder would try is `jiangyu templates list --type WeaponTemplate`.

### 3. Discover a template's fields, field types, and current values

`jiangyu templates inspect --type UnitLeaderTemplate --name squad_leader.darby`
plus `jiangyu templates query UnitLeaderTemplate` together answer this today,
but `inspect` is missing a scan-friendly text view, named-array sugar, stable
`TemplateReference` identities, and Odin-only field warnings that make the
output directly usable for authoring; the first thing a modder would try is
`jiangyu templates inspect --type <Type> --name <m_ID>`.

### 4. Preview the effective state of a clone + patch batch before launching the game

No existing `jiangyu templates` command answers this today, so there is no
static preview of the effective post-clone/post-patch template state for a mod
source tree; the first thing a modder would try is
`jiangyu templates inspect --type <Type> --name <m_ID> --with-mod <path>`.

### 5. Discover valid value shapes for each field

`jiangyu templates query` already answers the broad member-shape question from
`Assembly-CSharp.dll`, but it is missing enum member listings, clear
TemplateReference target identities, and Odin-only warnings that tell a
modder whether a field is safe and meaningful to patch; the first thing a
modder would try is `jiangyu templates query WeaponTemplate` or a leaf query
such as `jiangyu templates query WeaponTemplate.Damage`.

## Concrete gaps

- No additive search surface for case-insensitive substring matching across
  template IDs and collections.
- `templates inspect` is JSON-only; there is no text rendering optimised for
  authoring review.
- Reference rendering is asset-centric (`fileId` / `pathId`) rather than
  template-centric when the field points at another template.
- Named-array byte fields such as `UnitLeaderTemplate.InitialAttributes` do
  not surface the modder-facing symbolic names from
  `TemplateFieldPathSugar`.
- No static patched-view preview for `templateClones` + `templatePatches`.
- Odin-only serialisation surfaces are not flagged in authoring output, so a
  modder can target a field our reflection-based runtime applier will not
  actually write.

## Immediate direction

Close the gaps additively by extending the existing CLI surface:

- add search support without breaking `list`
- add text output without changing JSON defaults
- render template references and named-array sugar in inspect output
- flag Odin-only fields in query/inspect output
- add a mod-aware static preview path for effective template state
