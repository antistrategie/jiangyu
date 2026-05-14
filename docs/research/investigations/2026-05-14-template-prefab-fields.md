# Template Prefab Fields

Inventory of every template field that holds a `UnityEngine.GameObject` (or a
list/wrapper around one), and the runtime path that turns one into an
instantiated visual.

## Method

- Source data: `~/.local/share/jiangyu/cache/template-values.json`
  (`template-index` format v7, indexed against game-data hash
  `9d0dc2dfe8a9a14...`).
- Field selection: every field whose `FieldTypeName` matches
  `GameObject`, `PrefabList`, or `PrefabAttachment` across all 7508
  instances.
- Wrapper inspection: `ilspycmd` against
  `MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll` for `EntityTemplate`,
  `Element`, `Entity`, `EntityVisuals`, `ArmorTemplate`, `PrefabListTemplate`,
  `Squaddie`.
- Method bodies are stubs in the Il2CppInterop wrapper. Signature-level facts
  are confirmed. Predicate-level facts (e.g. exact branch order inside
  `Element.Create`) are inferred from signature shape plus data shape.

## Schema: prefab-bearing fields across all template classes

36 template classes carry at least one prefab field. 95 distinct
`(class, field, type)` slots in total.

### Body visuals (instantiated as the thing the player sees)

| Class | Field(s) | Type | Notes |
|---|---|---|---|
| `EntityTemplate` | `Prefabs` | `List<GameObject>` | Variant pool. Primary slot for **non-soldier** entities (buildings, aliens, vehicles, scatter). |
| `EntityTemplate` | `DestroyedPrefabs`, `DestroyedWalls` | `List<GameObject>` | Per-state visuals for destruction. |
| `EntityTemplate` | `Decoration`, `SmallDecoration`, `DestroyedDecoration` | `List<PrefabListTemplate>` | Two-level indirection: list of named GameObject lists. Buildings only. |
| `EntityTemplate` | `AttachedPrefabs` | `PrefabAttachment[]` | Inline struct array: `{ IsLight, AttachmentPointName, Prefab }`. Bone-attached extras. |
| `EntityTemplate` | `ActorLightOverride` | `GameObject` | Per-entity light override. |
| `ArmorTemplate` | `Model`, `ModelSecondary` | `GameObject` | Generic display model. |
| `ArmorTemplate` | `MaleModels`, `FemaleModels` | `GameObject[]` | Gender-segregated variant pool for non-leader elements. |
| `ArmorTemplate` | `SquadLeaderModel{Male,Female}{White,Brown,Black}`, `SquadLeaderModelFixed` | `GameObject` | Leader body, dispatched on gender x skin colour or fixed. |
| `WeaponTemplate`, `VehicleItemTemplate`, `ModularVehicleWeaponTemplate` | `Model`, `ModelSecondary` | `GameObject` | Held / mounted weapon model. |
| `AccessoryTemplate` | `Model`, `ModelSecondary` | `GameObject` | Worn accessory (helmet, etc.). |
| `UnitLeaderTemplate` | `CustomHead` | `GameObject` | Named-character head, stitched onto the armor body. |
| `PrefabListTemplate` | `Prefabs` | `GameObject[]` | Reusable named variant pool. |

### EntityTemplate FX slots (single-prefab effect refs, not bodies)

`DamageReceivedEffect`, `HeavyDamageReceivedEffect`,
`GetDismemberedBloodSprayEffect`, `DeathEffect`, `DeathAttachEffect`,
`ExhaustDriveEffect`, `ExhaustRevEffect`, `ExhaustIdleEffect`, plus
`GetDismemberedSmallAdditionalParts: PrefabListTemplate`.

### Other FX-only classes

`SurfaceEffectsTemplate` (13 surface-keyed effects), `SkillTemplate` and
`PerkTemplate` (`MuzzleEffect`, `MalfunctionEffect`, `DefaultImpactEffect`),
10 `*TileEffect.SpawnObjectOrEffect` slots, `Scanner.{Blip,Scanline}`,
`Regeneration.Effect`, `HalfCoverTemplate.EffectOnDeath`,
`WeatherTemplate.CameraEffect`, `AttachObject.ObjectToAttach`,
`AttachObjectWhileActive.Prefab`, `AttachTemporaryPrefab.Prefab`,
`AnimationSequenceTemplate.Prefab`, `SpawnPrefab.Prefab`,
`SpawnGore.Prefabs`, `DestroyWreckage.SpawnDecoPrefab`,
`TacticalConfig.{ActorLightPrefab, CablePrefabs, FakeLightPrefab,
GpuInstancerMapMagicIntegration, LaserFatalityEffect, LaserFatalityAshes,
PlasmaFatalityEffect, PlasmaFatalityAshes, TargetDiscoveredEffect}`,
`LightConditionTemplate.{DirectionalActorLightPrefab, DirectionalLightPrefab}`,
`BiomeTemplate.WindZone`, `EnvironmentFeatureTemplate.{Prefabs, Details,
DestroyEffect}`, `PlanetTemplate.{MissionSelectPrefab,
OperationSelectScenePrefab}`, `UIConfig.DelayedAbilityPositionMarkerPrefab`.

## Wrapper types

### `PrefabListTemplate`

Thin reusable wrapper around a `GameObject[]`. Surface:

```csharp
public class PrefabListTemplate : SerializedScriptableObject {
    public Il2CppReferenceArray<GameObject> Prefabs { get; set; }
    public GameObject GetRandomPrefab(PseudoRandom _random);
    public bool IsEmpty();
}
```

Used in two ways:
- as a single ref (`EntityTemplate.GetDismemberedSmallAdditionalParts`,
  `TacticalConfig.LaserFatalityAshes`, `TacticalConfig.PlasmaFatalityAshes`,
  `SpawnGore.Prefabs`): the slot holds one reusable named list.
- as a list of refs
  (`EntityTemplate.{Decoration, SmallDecoration, DestroyedDecoration}`):
  list of named lists.

### `PrefabAttachment`

Inline serialised struct, not a separate asset:

```csharp
struct PrefabAttachment {
    bool IsLight;
    string AttachmentPointName;   // bone name, e.g. "bone_body"
    GameObject Prefab;
}
```

Used only on `EntityTemplate.AttachedPrefabs`. Element-side consumer is
`Element.AttachPrefab(GameObject prefab, string attachmentPointName)`.

## `Prefabs[]` semantics

`EntityTemplate.GetRandomPrefab(PseudoRandom)` exists on the template wrapper.
That confirms `Prefabs` as a **random variant pool**, not a per-LOD list.
LODs live inside each individual prefab's `LODGroup`, not at the template
level.

Empirical confirmation from instance counts:
- Named characters and single buildings: `Prefabs.Count = 1`.
- Grunt-class units with element variety: count ranges from 9
  (`civilian_worker`) to 18 (`pirate_saboteur`).
- Environment scatter (rocks, bushes, wrecks, junk): count is the variant
  pool size (e.g. `SM_Env_Rock_Small_*` has 25 entries).

## Runtime composition flow

```
Entity.Create(EntityTemplate template, Tile tile, FactionType faction, int hp)
  ├─ Entity.CreateElementFromSquaddie(int squaddieId)
  └─ Entity.CreateElement(UnitLeaderTemplate leader, int squaddieId, int hp, Vector2 scaleRange)
       └─ Element.Create(int index, Entity e, Tile tile, Gender gender, int hp, Vector2 scaleRange)
            ├─ EntityVisuals.DetermineGender(template, squaddie, leader, rng)
            ├─ EntityVisuals.DetermineArmorPrefab(template, squaddie, index, gender, items, leader, rng): GameObject
            │     dispatches through equipped ArmorTemplate when an armor item is present;
            │     falls back to EntityTemplate.GetRandomPrefab(rng) when no armor is equipped
            ├─ EntityVisuals.StitchHeadToBody(model, index, leader)
            │     attaches UnitLeaderTemplate.CustomHead onto the body
            └─ Element.CreateAttachments(index, items, template)
                  iterates EntityTemplate.AttachedPrefabs[]
                  └─ Element.AttachPrefab(prefab, attachmentPointName)
```

`EntityVisuals` also exposes static helpers `GetEquippedWeapon(items, index)`,
`GetEquippedArmor(items)`, `GetSpecialHeavyWeaponAnimType(items, index)`.

## Worked example: Darby

`player_squad.darby` (`resources.assets:112512`) is the canonical squad leader.
The visual is assembled from four templates, not one:

| Template | Field | Value |
|---|---|---|
| `EntityTemplate player_squad.darby` | `Items[0]` | `armor.player_fatigues` (path 112652) |
| `EntityTemplate player_squad.darby` | `Prefabs[0]` | `rmc_scout_marine_tier1` (fallback only; armor wins) |
| `UnitLeaderTemplate squad_leader.darby` | `Gender` / `SkinColor` / `CustomHead` | `Female` / `Black` / `pv_sl_darby_head` |
| `ArmorTemplate armor.player_fatigues` | `SquadLeaderMode` | `Custom` |
| `ArmorTemplate armor.player_fatigues` | `SquadLeaderModelFemaleBlack` | `rmc_default_female_soldier_2` |

In-game body: `rmc_default_female_soldier_2` with `pv_sl_darby_head` stitched
on. The EntityTemplate's `Prefabs[]` is the wrong slot to redirect Darby's
visual.

## Slice difficulty ladder for prefab cloning

| Slice | Templates touched | Axes | Difficulty |
|---|---|---|---|
| New building / alien / decoration | EntityTemplate clone, 1 new prefab | one | **lowest** |
| New vehicle | EntityTemplate + maybe VehicleItemTemplate | one | low |
| New weapon | WeaponTemplate, 1-2 prefabs | one | low |
| Soldier re-skin | new ArmorTemplate, two model lists | gender | medium |
| New squad leader | EntityTemplate, UnitLeaderTemplate, ArmorTemplate (or variants), WeaponTemplate, Squaddie | gender x skin x head | **highest** |

The alien/building slice is single-axis, single-template, single-prefab. The
squad-leader slice is multi-template composition with gender and skin-colour
dispatch plus a head-stitch step.

## Loader plumbing for Layer 1 (prefab as asset addition)

The escape-hatch shape: a modder authors a complete prefab in an external
Unity Editor project, exports it into a `.bundle`, and points an
EntityTemplate clone's `Prefabs[0]` at it via `asset="name"`.

Concrete change list:

- `Jiangyu.Shared.Replacements.AssetCategory.IsSupported`: add `"GameObject"`.
- `Jiangyu.Shared.Replacements.AssetCategory.ForClassName`: route
  `"GameObject"` to `Prefabs` instead of throwing.
- `Jiangyu.Loader.Bundles.BundleReplacementCatalog`: add an
  `AdditionPrefabs: Dictionary<string, GameObject>` dictionary, kept separate
  from the existing replacement `Prefabs` dictionary (which holds
  `ReplacementPrefab` with swap metadata).
- `Jiangyu.Loader.Templates.ModAssetResolver.TryFind`: add a
  `case nameof(GameObject)` branch consulting `AdditionPrefabs`.
- Compile pipeline: pack `assets/additions/prefabs/*` GameObjects into the
  bundle. Source extensions need to grow `.prefab` / bundle handling for that
  category in `AdditionExtensionsForCategory`.
- KDL authoring: `set "Prefabs" index=0 asset="my_alien"` (or `clear "Prefabs"` +
  `append "Prefabs" asset="my_alien"` to replace the whole list). The existing
  nested-array-element set plus AssetReference path handles this once the
  AssetCategory + ModAssetResolver cases are added.

## Confirmed by runtime spike (2026-05-14)

### Vanilla cross-asset substitution (Phase 2)

Patched `object_wind_turbine_01_1x1.Prefabs` to a single-entry list pointing
at `civilian_antenna_tower_optimized` via a temporary AssetCategory-side
allowance for `GameObject` (Phase 2 resolution against the live
`Resources.FindObjectsOfTypeAll` registry, no bundle). Wind turbines spawned
as antenna towers across the map.

- Buildings read `EntityTemplate.Prefabs[]` at spawn time. The
  `Structure.Create` / `ChunkGenerator` path resolves through the same
  variant pool the investigation already documented for actors.
- `Resources.FindObjectsOfTypeAll(GameObject)` returns prefab GameObjects
  for at least some assets. Antenna tower was findable by name. Phase 2
  works for vanilla cross-asset substitution. Bundle plumbing is still
  required for mod-shipped prefabs, which the runtime registry won't carry.
- Cross-template GameObject substitution survives Unity's instantiation
  pipeline cleanly.

The earlier antenna→soldier attempt did not visibly substitute because
soldier prefabs do not render standalone outside `Element.Create` wrapping
(Animator/SkinnedMeshRenderer lifecycle expects the Entity scaffolding to
drive them). Static-prop-to-static-prop substitution is the right shape for
the building slice; soldier-shaped substitution waits on the Element path
being exercised (cloned actor template, not building).

### Mod-shipped bundle substitution (Phase 1)

Authored `sample_cube.prefab` in `unity/Assets/Prefabs/` (a cube with a
`Menace/building` material, bundled mesh + material as project assets), built
the bundle via `mise compile`'s Unity batchmode pass, deployed alongside
the main mod bundle, and patched 14 common static-object EntityTemplates
to substitute the cube. Magenta cubes appeared at every patched template's
spawn locations (junk piles, containers, barrels, supplies, wind turbines,
antenna towers).

This validates Layer 1 end-to-end:

- `AssetCategory` plumbing for `GameObject`.
- `ModManifest.AdditionPrefabs` round-trips from compile to loader.
- `BuildBundles.cs` produces correctly-named bundles
  (`Assets/Prefabs/<name>.prefab` → `<name>.bundle`, nested paths
  translate `/` to `__`).
- `AdditionPrefabStaging` merges Unity-built bundles into `compiled/`.
- Loader dispatches bundle by filename stem (not Object.name), registers
  via `RegisterAdditionPrefab`.
- `ModAssetResolver.TryFind` Phase 1 dict lookup resolves `asset="name"`
  references at patch-apply time.
- Game runtime instantiates the mod-shipped prefab via the same
  `EntityTemplate.GetRandomPrefab → Object.Instantiate` path it uses for
  vanilla prefabs.

### Authoring constraints surfaced during validation

- **Bundled materials must use `Menace/*` shaders** (`Menace/building`,
  etc.). Texture and colour swaps work on any shader that declares the
  matching property names.
- **The Unity Editor renders `Menace/*` shaders as magenta.** AssetRipper
  extracts them as Properties-only stubs with a placeholder `frag()`
  returning solid white and a `Fallback` to
  `Hidden/Shader Graph/FallbackError` (the magenta error shader). The
  stubs compile in the Editor but their variants render as the fallback.
  In-editor preview is therefore not usable; iterate by building the
  bundle and inspecting the result in MENACE.
- **Bundled shaders are stubs that don't render. They must be rebound at
  load time to the runtime's resolved shader by name.** The loader works
  around this in `RegisterAdditionPrefab`: for every Renderer on a
  registered prefab, it walks materials and reassigns
  `material.shader = Shader.Find(material.shader.name)`. The runtime
  resolves to the game's real shader (which has correct variants
  precompiled). Material properties (colors, textures, keywords) are
  preserved across the swap. Without this rebind, bundled materials all
  render magenta. If `Shader.Find` returns null, the loader logs a
  warning and leaves the material on its broken stub shader, surfacing
  the magenta render as a hint that the material's shader name isn't in
  the game's runtime — switch it to a `Menace/*` shader.
- **Bundled prefabs must use project-asset meshes and materials, not
  Unity built-ins.** Production game runtimes commonly strip Unity's
  built-in primitive meshes (`Cube`, `Sphere`, etc.) and `Default-Material`.
  Prefabs that reference these built-ins by ID spawn as ghost GameObjects
  (null mesh, null material) in the game. `SamplePrefabCreator.cs`
  duplicates the built-in cube mesh and creates a fresh material, saving
  both as project assets so they pack into the bundle. Any prefab the
  modder authors must follow the same pattern (use imported model assets
  or explicitly-saved generated assets, never primitives left as built-in
  references).
- **The bones/animator hypothesis was wrong.** Substituting a static
  `MeshRenderer`-only cube for an animated building template
  (wind turbine, with `Animator` + `SkinnedMeshRenderer` + bones) works
  fine. The building-spawn pipeline tolerates static-only substitutes.
  Required prefab contract is just "renderable Unity GameObject", not a
  particular component shape.

## Still open

1. Whether a GameObject loaded from a mod bundle via
   `Il2CppAssetBundleManager.LoadFromMemory()` survives the same path. The
   vanilla case is proven; mod-bundle is plausible by extension but
   unverified.
2. The exact branch order inside `Element.Create` for the actor path
   (whether `EntityTemplate.Prefabs[]` is consulted only when
   `DetermineArmorPrefab` returns null, or in some other order). Does not
   affect the alien / building slice.

## Animation retargeting: humanoid avatars across the soldier family

Sampled via `jiangyu assets inspect object` against the 76 `Avatar` assets in
the index, looking at `m_Avatar.m_HumanSkeletonIndexArray.count` (zero for
generic, 20 for Mecanim humanoid).

| Avatar | Humanoid bone count |
|---|---|
| `a_rmc_default_male_soldier_avatar_tpAvatar` | 20 |
| `a_rmc_default_female_soldier_avatar_tpAvatar` | 20 |
| `rmc_local_forces_marinesAvatar` | 20 |
| `a_local_forces_officer_avatar_tpAvatar` | 20 |
| `a_local_forces_conscript_soldier_avatar_tpAvatar` | 20 |
| `APC_MG_TurretAvatar` | 0 |
| `skm_carrier_chassis_destroyedAvatar` | 0 |
| `local_forces_walker_turretAvatar` | 0 |

Every soldier-family Avatar carries a full 20-bone Mecanim humanoid mapping.
Vehicles and turrets are generic.

Implication for prefab cloning of soldiers: the AnimatorController drives
clips through Mecanim's humanoid muscle space, not via hard-coded bone paths.
A cloned soldier prefab can ship its own skeleton with arbitrary bone naming
and hierarchy as long as the prefab's Avatar is configured humanoid (mapped to
Unity's 20-bone schema) in Unity Editor. Animations retarget automatically.

Vehicles and turrets do not get this property and would need either an
exact-skeleton match or a custom AnimatorController plus clips.

## Visual override via `bind` directive

### Why direct template patching doesn't work

Three approaches were tried before settling on the runtime override:

1. **Patch the vanilla armor's models directly.** Setting
   `armor.player_fatigues.SquadLeaderModelFemaleBlack = <X>` does change
   the visual every female-black squad leader wearing fatigues renders
   with. But it affects every such unit (vanilla Darby plus any cloned
   Darby), so isolation to a specific unit clone is impossible.
2. **Clone the armor and set its fields.** Cloning
   `armor.player_fatigues_darby_jiangyu` and setting its models has no
   runtime effect: the dispatch reads from the armor instance equipped
   on the unit's `ItemContainer`, which the runtime sets to the shared
   vanilla armor at spawn regardless of what the squad's
   `EntityTemplate.Items[0]` points at.
3. **Clone the EntityTemplate and redirect `UnitLeaderTemplate.InfantryUnitTemplate`.**
   Each unit's `BaseUnitLeader.GetTemplate()` returns the
   EntityTemplate, and the runtime does honour the redirect for
   tactical-element spawning. But the strategy squad UI's leader-list
   rendering walks `leader.GetTemplate() → Items[0]` for previews. When
   that walk hits a cloned EntityTemplate whose Items[0] points at a
   cloned armor with a non-soldier-shape model substituted, downstream
   rendering code throws and the entire squad-slot list collapses to
   empty squares. Even with a soldier-shape substitute the redirect
   stays risky because any UI code consuming the cloned chain can fail
   on subtle component differences.

### The override mechanism

The loader hooks `EntityVisuals.DetermineArmorPrefab` (the function
MENACE's runtime calls to pick an element's visual prefab) and runs as
a Harmony postfix. When the call's `_leaderTemplate` parameter is a
modder-cloned `UnitLeaderTemplate` with an associated cloned
`ArmorTemplate`, the postfix:

1. Reads the model from the cloned armor's
   `SquadLeaderModel{Gender}{SkinColor}` for element index 0 (the
   leader), or `MaleModels[0]` / `FemaleModels[0]` for non-leader
   indices (grunts).
2. Replaces `__result` with the cloned armor's value.

Everything else stays untouched. The cloned leader still uses the
vanilla `InfantryUnitTemplate`, the vanilla `Items[]`, and the vanilla
default armor. Only the visual dispatch is re-routed for that one
leader template.

### Binding declaration

The association between a cloned `UnitLeaderTemplate` and the cloned
`ArmorTemplate` is declared explicitly in KDL:

```kdl
bind "leader_armor" leader="squad_leader.darby_jiangyu_clone" armor="armor.player_fatigues_darby_jiangyu"
```

The `bind` directive is pure Jiangyu metadata. The compiler emits it
into `jiangyu.json` under `templateBindings`; at runtime
`TemplateBindingCatalog` loads it and
`RuntimeActorVisualRefreshPatch.EnsureOverrideMap` builds the
leader-name → cloned-armor map on first dispatch.

### Strategy UI compatibility

Because the vanilla template chain stays intact, the strategy squad UI
walks structurally vanilla data for every leader (including the
cloned one — its `InfantryUnitTemplate` is still vanilla
`player_squad.darby`). The squad-slot list renders normally. The
runtime override fires only at `EntityVisuals.DetermineArmorPrefab`,
which the strategy UI invokes for the leader portrait but not for any
slot-position-affecting computation. Substituting a soldier-shape
prefab works for both tactical and strategy views; substituting a
non-soldier-shape prefab (e.g. a static prop like
`my_antenna`) still breaks the strategy UI's downstream consumers,
which is why the recommended substitute is another vanilla soldier
prefab (e.g. `el.local_forces_basic_soldier` or
`civilian_worker_a_3`) or a modder-shipped soldier-shaped bundle.

### Vanilla asset references

`asset="<name>"` in KDL now resolves either against the mod's local
additions or against the vanilla game asset index built by
`jiangyu assets index`. Lets the modder reference any in-game
GameObject (or sprite, texture, audio clip, material) by name without
having to ship a local copy. Runtime resolution falls through
`ModAssetResolver` Phase 1 (mod bundle dict) → Phase 2
(`Resources.FindObjectsOfTypeAll` over loaded Unity assets) to find
the actual object at apply time.

## Out of scope for this investigation

- Layer 2: clone-with-overrides authoring (`prefabs/<name>.kdl` clone-base
  plus mesh / material / animation overrides, built into a bundle via Unity
  Editor automation at compile time).
- Recovery of method bodies for `Element.Create` and
  `DetermineArmorPrefab` via Cpp2IL's ISIL-to-CIL analysis layers. Empirical
  signal from data shape and signature shape is enough to commit Layer 1.
