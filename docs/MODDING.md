# Modding

Jiangyu uses convention-first replacements under `assets/replacements/`.

Current supported replacement categories:

- models
- textures
- sprites
- audio

Use `assets search` to find the target and the suggested replacement path for each supported asset type.

## Exporting vanilla assets

For iterative authoring (recolour a base map, retouch a sprite, see a rigged mesh in Blender before modifying), export the vanilla asset first:

```bash
jiangyu assets export model <name> --path-id <id> [--output <dir>]
jiangyu assets export texture <name> --path-id <id> [--output <file-or-dir>]
jiangyu assets export sprite <name> --path-id <id> [--output <file-or-dir>]
jiangyu assets export audio <name> --path-id <id> [--output <dir>]
```

`--path-id` is required only when the name resolves to multiple assets (the command lists them for you). Output defaults live under the Jiangyu cache at `<cache>/exports/`. Textures and sprites are written as PNG; audio is written at whatever format Unity embedded (typically `.ogg`; module formats like `.it`/`.xm`/`.mod` pass through unchanged).

## 3D Model Targets

For **3D model replacements**, the preferred modder-facing target is a **`PrefabHierarchyObject`**, not a raw `GameObject`.

Example:

```bash
dotnet /path/to/jiangyu.dll assets search local_forces_basic_soldier --type PrefabHierarchyObject
```

If a prefab-hierarchy view exists, that is the target modders should use. `GameObject` remains useful for low-level inspection and internal resolution, but it is not the preferred authoring target.

## Model Replacement Path

Current convention-first model replacement path shape:

```text
assets/replacements/models/<target-name>--<pathId>/model.gltf
assets/replacements/models/<target-name>--<pathId>/model.glb
```

Example:

```text
assets/replacements/models/el.local_forces_basic_soldier--519/model.gltf
```

or:

```text
assets/replacements/models/el.local_forces_basic_soldier--519/model.glb
```

Inside the model file, mesh names should match the target mesh/LOD contract.

Jiangyu accepts authored skinned model replacements from Blender in both `.gltf` and `.glb` form. The compiler normalises authored metre-space skin data onto Jiangyu's proven replacement path; modders do not need to preserve Jiangyu-specific glTF metadata for that round-trip to work.

For changed rest poses or moderate proportion drift, Jiangyu automatically exports the indexed target into its own compiler-owned reference model and retargets the authored mesh back onto the game's expected skeleton contract. v1 bind-pose retargeting supports authored skinned models with the same bone names and hierarchy as the game skeleton.

If a replacement only provides part of an LOD family, Jiangyu warns at compile time and the loader uses the nearest available replacement within that family at runtime. Complete LOD sets are still preferred.

### Skinning weights

Jiangyu's exporter emits vertex weights that match the vanilla source's skinning layout:

- Rigid-skinned vanilla meshes (every vertex 100% bound to a single bone — typical of mechanical rigs like vehicle chassis, where each wheel vert follows its own wheel bone) export as rigid weights in the glTF.
- Blended-skinned vanilla meshes (vertices influenced by multiple bones — typical of character rigs where joints need smooth deformation) export with the full per-vertex weight mix preserved.

The compiler does not rewrite weights. Whatever the authored glTF ships is what goes into the bundle. Author normally in Blender on top of the exported baseline; avoid re-rigging mechanical parts with "parent with automatic weights" since that introduces blended influence on parts the game expects to be rigid, which shows up as linear-blend-skinning scaling artefacts (e.g. wheels visibly growing and shrinking while rotating).

### Vertex space

The compiler derives the replacement's vertex-space transform from the ratio between the authored mesh's bounds and the vanilla target's local AABB:

- Authored extent ≈ target extent → pass through unchanged.
- Authored extent ≈ target extent × 0.01 → apply a 100× scale-up (authored in metres, target stored in centimetres).

No bone-name conventions, naming prefixes, or vehicle-vs-character hints are required. If the ratio is neither close to 1 nor close to 100, Jiangyu falls back to the `extras.jiangyu.cleaned` flag to pick a space.

## Current Limitation

At runtime, Jiangyu still resolves live mesh replacements by `sharedMesh.name`.

That means convention-first model replacement is only safe when the target model's expected mesh names are unique. Jiangyu should reject ambiguous targets at compile time rather than silently replacing the wrong live mesh.

## Texture Replacement Path

For direct texture replacement, use:

```text
assets/replacements/textures/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/textures/local_forces_basic_soldier_BaseMap--1234.png
```

This replaces the matching `Texture2D` asset when Jiangyu can prove the runtime texture name is unique. At runtime the loader mutates the game's `Texture2D` in place via `Graphics.ConvertTexture` + `Graphics.CopyTexture`, so every consumer — materials, UGUI, UI Toolkit, caches, template references — inherits the new pixels. See [`docs/research/verified/texture-replacement.md`](research/verified/texture-replacement.md) for the full contract.

## Sprite Replacement Path

For UI icons and other direct sprite replacement, use:

```text
assets/replacements/sprites/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/sprites/MenaceFontIcons_0--9316.png
```

Sprite replacement piggybacks on texture replacement: every `Sprite` references a
backing `Texture2D`, so mutating that texture updates the sprite for every
consumer (UGUI, UI Toolkit, `SpriteRenderer`, cached references) automatically.

**Only sprites backed by a unique `Texture2D` can be replaced.** The compiler
rejects any sprite target whose backing texture backs more than one indexed
sprite (i.e. a packed atlas); the error names the atlas texture and lists
co-tenant sprites. This is a principle-7 compile-time check — Jiangyu refuses
to mutate a shared atlas and silently corrupt unrelated UI elements. Use
`jiangyu assets search <name> --type Sprite` to find candidates; if the
resulting compile fails with an atlas error, that sprite is not replaceable in
the current contract. See [`docs/research/verified/sprite-replacement.md`](research/verified/sprite-replacement.md) for the full contract.

**Re-index after upgrading Jiangyu** (`jiangyu assets index`) so the atlas
check has backing-texture identity to work with. The compiler will tell you if
the current index is too old.

## Audio Replacement Path

For direct audio replacement, use:

```text
assets/replacements/audio/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/audio/sfx_rifle_fire--4321.wav
```

At runtime the loader installs Harmony prefixes on `AudioSource`'s playback
methods (`Play`, `PlayOneShot`, `PlayDelayed`, `PlayScheduled`, and the static
`PlayClipAtPoint`). When one of those fires with a clip whose `.name` matches
a registered replacement target, the prefix substitutes the modder's clip
before the original method proceeds. This catches every playback path
including clips cached on audio-manager singletons and `PlayOneShot(clip)`
argument paths that older consumer-walk approaches miss. See
[`docs/research/verified/audio-replacement.md`](research/verified/audio-replacement.md)
for the full contract.

**Match frequency and channels.** Unity resamples mismatched audio at runtime
which pitch-shifts the sound. Check the target's frequency and channel count
with `jiangyu assets search <name> --type AudioClip` and author the
replacement at the same rate and channel layout.

## Template Patching

Jiangyu can write into MENACE's live `DataTemplate` instances — player-squad
stats, skill parameters, unit leader loadouts — without binary-patching
`resources.assets`. Patches live in the mod's top-level `jiangyu.json` under
`templatePatches` and apply once per scene load when the target template cache
is materialised.

### Shape

```json
{
  "name": "MyMod",
  "templatePatches": [
    {
      "templateType": "EntityTemplate",
      "templateId": "player_squad.darby",
      "set": [
        { "fieldPath": "HudYOffsetScale", "value": { "kind": "Single", "single": 5.0 } }
      ]
    },
    {
      "templateType": "UnitLeaderTemplate",
      "templateId": "squad_leader.darby",
      "set": [
        { "fieldPath": "InitialAttributes.Agility", "value": { "kind": "Byte", "byte": 100 } },
        {
          "fieldPath": "PerkTrees[0]",
          "value": {
            "kind": "TemplateReference",
            "reference": { "templateType": "PerkTreeTemplate", "templateId": "perk_tree.greifinger" }
          }
        }
      ]
    }
  ]
}
```

- `templateType` — a `DataTemplate` subtype name (`EntityTemplate`,
  `UnitLeaderTemplate`, `SkillTemplate`, etc.). Omit to default to
  `EntityTemplate`.
- `templateId` — the template's serialised `m_ID`. Find candidates with
  `jiangyu templates list --type <TypeName>`.
- `fieldPath` — dotted member navigation with optional `[N]` indexers
  (`Properties.Accuracy`, `Skills[0].Uses`, `InitialAttributes[0]`). Later-
  loaded mods override earlier ones with a warning.

### Value kinds

| `kind`              | typed field       | notes                                                                   |
|---------------------|-------------------|-------------------------------------------------------------------------|
| `Boolean`           | `boolean`         |                                                                         |
| `Byte`              | `byte`            | 0–255 storage; gameplay ranges vary per field                           |
| `Int32`             | `int32`           |                                                                         |
| `Single`            | `single`          |                                                                         |
| `String`            | `string`          | Targets `System.String` fields; Il2CppSystem.String not yet supported   |
| `Enum`              | `enumValue`       | Optional `enumType` to assert the target enum name                      |
| `TemplateReference` | `reference`       | Object with `templateType` + `templateId` — resolves a live wrapper     |

A patch fails loudly at load or apply when a path is malformed, a target
template doesn't exist, a member is missing, or a value kind doesn't match the
member type. Check `MelonLoader/Latest.log` for the `Template patch ...` line
per operation.

### Discovering patch targets

`jiangyu templates query` is a jq-like navigator over `Assembly-CSharp.dll`.
It uses the same path syntax as `fieldPath` and emits a copy-pasteable
`templatePatches` snippet for leaf fields:

```bash
jiangyu templates query EntityTemplate.Properties.Accuracy
jiangyu templates query 'UnitLeaderTemplate.InitialAttributes[0]'
jiangyu templates query EntityTemplate.Skills   # auto-unwraps to SkillTemplate
```

Combine with `jiangyu templates list --type <TypeName>` and
`jiangyu templates inspect --type <TypeName> --name <id>` to find valid
template IDs and their current field values.

### UnitLeader attribute sugar

`UnitLeaderTemplate.InitialAttributes` is a 7-byte array where each offset is
one of the `UnitLeaderAttribute` enum values. Jiangyu accepts either form:

```json
{ "fieldPath": "InitialAttributes.Agility", "value": { "kind": "Byte", "byte": 100 } }
{ "fieldPath": "InitialAttributes[0]",      "value": { "kind": "Byte", "byte": 100 } }
```

The sugar rewrites the named form to `InitialAttributes[N]` at compile time;
unknown attribute names fail loudly with a listing of valid names. See
[`research/verified/unitleader-initial-attributes.md`](research/verified/unitleader-initial-attributes.md)
for the verified offset table and source citations.

### Save-frozen vs render-per-frame

MENACE snapshots most player-visible gameplay stats into the save at campaign
init and only reads the template again on a **new campaign**. Examples that
are **save-frozen**: `UnitLeaderTemplate.InitialAttributes`, `PerkTrees`,
`EntityTemplate.Properties.Accuracy`, most combat stats. A patch lands at
runtime but its effect won't appear in an existing save.

Fields the game re-reads per frame (e.g. `EntityTemplate.HudYOffsetScale`)
update live on existing saves, so those are convenient smoke-test targets.

If you're changing a stat and not seeing it in an existing save, start a new
campaign before investigating whether the patch applied — the log will tell
you if the write landed.

### Not yet supported

- Collection appends/inserts and full template cloning (blocked on new IL2CPP
  object construction — the "cloning problem").
- Localisation patching (`LocalizedLine` / `LocalizedMultiLine`) — planned but
  not shipped.
- A stable KDL-like authoring surface under `templates/`. The current
  `templatePatches` shape is the compiled form; it works but is not the
  long-term modder-facing contract. Treat it as provisional.
