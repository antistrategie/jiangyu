# EntityFlags enum classification and post-update baseline re-audit

Date: 2026-06-26

## Goal

Unblock `templates baseline audit`/`generate` after a game update, which aborted on a
structural inconsistency, and record the resulting schema drift in the committed baseline.

## Trigger

`templates baseline audit` failed during baseline generation:

```
baseline regeneration failed: Structural inconsistency in 'Menace.Tactical.EntityProperties'
across curated samples: field 'Flags' kind differs: 'enum' in 'player_squad.darby' vs 'int'
in 'player_vehicle.modular_ifv'.
```

## Findings

`EntityProperties.Flags` is typed `Menace.Tactical.EntityFlags`, a `[Flags]` enum, on both
samples (identical `fieldTypeName`). The divergence is value-dependent:

- `player_squad.darby`: `Flags = 0` (`None`) classified `kind: enum`.
- `player_vehicle.modular_ifv`: `Flags = 96` (`32|64`) classified `kind: int`.

`ManagedTypeInspectionEnricher.PromoteEnumScalar` only set `kind = enum` when the integer
matched a single named member. A `[Flags]` combination matches no single member, so it fell
through to `int`. The kind therefore depended on the value, not the declared type, so two
instances of the same field compared as different kinds and the cross-sample generator aborted.

## Fix

`PromoteEnumScalar` now classifies every enum-typed scalar as `kind: enum` regardless of value:

- single member: shown by name (unchanged).
- `[Flags]` combination: decomposed to the ` | `-joined names of its set members (new
  `TryComposeFlags`, gated on `System.FlagsAttribute`).
- otherwise: keeps the numeric value, still typed `enum`.

`modular_ifv.Flags` now reads `enum` / `"ImmuneToSuppression | ImmuneToIndirectSuppression"`;
`darby.Flags` stays `enum` / `"None"`. The inspect-cache format version
(`TemplateIndexService.CurrentFormatVersion`) is bumped 8 → 9.

## Schema drift recorded

With generation unblocked, the audit reported the real game-update drift across three types,
now written into `validation/template-structure-baseline.json` (new `gameAssemblyHash`):

- `EntityTemplate`: `+HUDOffset`, `+IsIgnoredForCollateral`, `-HudYOffsetScale`.
- `SkillTemplate`: `+HitStrength`, `+MarkAsActed`, `+RecoilStrength`, `+ShowAllTilesInRangeOnHover`,
  `+UsabilityWhileContained`, `-IsUsableWhileContained`, `~Repetitions` (Int32 → UInt16).
  `IsUsableWhileContained` (bool) was replaced by `UsabilityWhileContained` (the
  `Menace.Tactical.Skills.UsabilityWhileContained` enum).
- `UnitLeaderTemplate`: `+PromotionBarkSounds`, `+PromotionCostMult`, `-PromotedBarkSound`,
  `-PromotionTax`, `-SlotInjured`.

No Jiangyu source or WOMENACE patch references any removed field, so nothing depends on them;
this is schema-record drift only. Consistent with the runtime template self-check, which flagged
only the separately-fixed `StrategyConfig.InitialAdditionalUnlockedItems` move.

## Conclusion

`templates baseline audit` is clean (`no drift detected`). Enum field kinds are now stable across
instances, and `[Flags]` combinations render readable composed names.

## Next step

None outstanding for this pass. Future game updates re-run the audit; flags decomposition keeps
enum kinds stable so the generator no longer aborts on combination values.
