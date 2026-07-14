# Structural Spot-Check: EntityTemplate and SkillTemplate (2026-07-14 game update re-audit)

Date: 2026-07-14

## Goal

Re-verify the two template types flagged by `templates baseline audit` after the
2026-07-14 MENACE update, so the committed structural baseline can be regenerated.

## Why These Types

The audit flagged exactly two CHANGED types, both additive:

- `EntityTemplate`: `+ CanAlliesPassThrough`
- `SkillTemplate`: `+ UsePistolAnimation`

No types were ADDED or REMOVED, and no existing field drifted in kind or shape.

## Samples

The curated baseline samples from `template-structure-baseline.sources.json`:

- `EntityTemplate`: `player_squad.darby`, `enemy.pirate_scavengers`
- `SkillTemplate`: `active.change_plates`, `passive.ammo_armor_piercing`

## Method

`templates index` against the updated game data (8652 instances, 249 template types),
then `templates inspect` on each sample and a mechanical search for the new field in
the JSON output.

## Results

Both fields are plain serialised booleans, present at the same structural position in
both samples of their type, value `false` in all four samples:

- `CanAlliesPassThrough`: `kind: bool`, `fieldTypeName: Boolean`, in both `EntityTemplate` samples.
- `UsePistolAnimation`: `kind: bool`, `fieldTypeName: Boolean`, in both `SkillTemplate` samples.

Classification: stable across samples.

## Interpretation

The update adds sidearm and movement behaviour toggles. Related surface drift seen in
the same pass corroborates this: `VisualAlterationSlot` gains `Pistol_Holster`,
`EntityFlags` gains `Confused`, and the game ships two new event handler types
(153 to 155 in `site/reference/event-handlers.md`).

This pass validates the serialised contract only. It does not validate runtime
behaviour of either flag.

## Related API drift fixed in the same pass

The compile and codegen contract checks caught the rest of the update's drift:

- `TooltipData.AddHeading`/`AddSubheading`/`AddParagraph`/`AddSectionHeading` gain an
  optional `Il2CppSystem.Nullable<Color> _iconColor` parameter before the trailing
  `_translate` bool. Fixed the four positional call sites in
  `src/Jiangyu.Sdk.Menace/Ui/Components/Tooltip.cs`. The argument must be a boxed
  empty `Il2CppSystem.Nullable<UnityEngine.Color>` (the existing `NoColor` static),
  not a bare C# `null`: the proxies marshal the nullable through
  `Il2CppObjectBaseToPtrNotNull`, so `null` throws `NullReferenceException` at runtime
  and the tooltip rows silently vanish. Same trap as the `_iconSize` bug of 2026-06-25.
- `TacticalManager.OnOffmapAbilityUsed`/`OnOffmapAbilityCanceled` delegates change
  their first parameter from `OffmapAbilityTemplate` to `OffmapAbilityInstance`.
  Runtime symptom: `hooks: failed to attach ... mismatched native type pointers` at
  mission start. `TacticalHookPublisher` now receives the instance and publishes
  `instance.Template`, so the SDK context contract is unchanged. The hookgen contract
  check did not catch this: it resolves event accessors by name only, not by delegate
  parameter types. Extending it to compare delegate signatures would turn this class
  of drift into a gen-time failure.
- `ShipUpgrades.GetSlotType(int)` is renamed `GetSlotTemplateByIdx(int)` (same shape).
  Rebound `src/Jiangyu.Codegen.Verbs/manifests/ship.json`, regenerated
  `Strategy.Ship.g.cs`. The verb name `Ship.SlotType` and the modder-facing docs are
  unchanged.
- All 48 hooks still resolve. `HookCatalog.g.cs` is unchanged.
- Handler doc generation passes (155 handlers).

Surface baseline reports (informational, candidate new verbs/hooks):

- `StrategyState`: `CalcMaxAuthority`, `CalcMaxSquaddies`, `CalcOciCostsMult`
  (replaces `GetOciCostsMult`), `CompletePickingInitialLeaders`,
  `EnforceMaxSquaddies`, event `OnPickingInitialLeadersCompleted`
  (replaces `OnFinishedPickingInitialLeaders`).
- `Roster`: `WasLeaderDeployed(BaseUnitLeader)`.
- `Squaddies`: `AddAlive(...)` becomes `TryAddAlive(int, ...) -> bool`.
- `StoryFaction`: `HasLockedUpgrades()`.
- `TacticalManager`: offmap-ability invokers now take `OffmapAbilityInstance` instead
  of `OffmapAbilityTemplate`, new event `OnOffmapAbilityStateUpdated`, new
  `QueueFactionChange(Actor, FactionType)` / `HandleQueuedFactionChanges()`.
- `ShipUpgrades`: new tech-tree queries (`GetParentUpgrades`, `GetTechTreeUpgrades`,
  `GetInstallsCount`, `GetFullShipUpgradeInstallCosts`) and game-effect unlock plumbing.

## Conversation data drift

The update also restructures JeanSy's kill-response conversations. The bare
`JeanSy/response_kill` no longer exists. Its successors are
`JeanSy/response_kill_anyone_nice` and `JeanSy/response_kill_anyone_philistines`
(same trigger `EntityDeath`, same condition `RANDOM_100>=76` on the nice variant, same
three-role shape: `Killed`, `Killer`, speaker `JeanSy` with `HasOneTag` at requirement
index 2) plus character-specific responses (`response_kill_darby`, `_exconde`,
`_greifinger`, `_lim`, `_rewa`, `_segelstorm`). All other `JeanSy/*` paths a mod is
likely to clone survive intact.

Resolver behaviour worth knowing: `ConversationRoleLookup` is path-first with a
bare-name fallback, so a clone `from="JeanSy/response_kill"` silently resolved to
`Bog/response_kill` (first bare-name match) and only failed at role validation
(`RoleGuid "JeanSy" does not match any role. Known: Killed, Killer, bog`). A missing
path with a `/` in the lookup key falling back to another character's conversation is
a sharp edge: the validator catches it when role names differ, but a clone of a
conversation whose roles happen to match would rebind silently.

## Conclusion

Both flagged fields are stable, additive serialised booleans. The baseline is safe to
regenerate. No inspection-pipeline changes were needed this time.

## Next Step

The offmap-ability hook family (`OnOffmapAbilityStateUpdated` and the
instance-based invokers) is the strongest candidate for new SDK hooks. The
`ShipUpgrades` tech-tree queries are the strongest candidates for new verbs.
