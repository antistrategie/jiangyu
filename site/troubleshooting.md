# Troubleshooting

When things go sideways, two places to check first:

- **`<MENACE>/MelonLoader/Latest.log`** has everything the loader did this session: which mods it discovered, which bundles it loaded, which replacements it applied, which template patches succeeded or failed.
- **Studio's compile dossier** logs every step of the build. If a compile fails, the cause is the last red line in the log column.

Most issues fall out of one of those two logs. The rest of this page covers the symptoms that aren't immediately obvious from the log line itself.

## My mod doesn't load

Open `MelonLoader/Latest.log` and search for your mod's name. The loader logs a discovery line per mod under `Mods/`. If your mod isn't there, it wasn't discovered. If it's there but blocked, the block reason is on the next line.

Common causes:

- **`Jiangyu.Loader.dll` is missing from `Mods/`.** No Jiangyu = no Jiangyu mods. Drop the DLL into `<MENACE>/Mods/` and relaunch.
- **Your mod folder isn't under `Mods/`.** The loader recursively scans `<MENACE>/Mods/**/jiangyu.json`. Your project's `compiled/` directory has to be copied (or symlinked) into `Mods/`, typically under a folder named after the mod.
- **`jiangyu.json` is missing or unreadable.** The loader logs a parse error.
- **Manifest has no `name`.** Required field. Without it the loader can't identify the mod.
- **Two mod folders share the same `name`.** Both copies are blocked. The error names every duplicate location. Rename one.
- **A required dependency isn't installed.** `depends: ["SomeMod"]` blocks your mod when `SomeMod` isn't in `Mods/`. The literal name `Jiangyu` always satisfies because it's the loader itself.

## My mod loaded but my replacement doesn't show up

The loader logs each replacement it applies. If your replacement target is in the log but the visual didn't change, there's something subtle going on. If it's not in the log, your replacement file probably isn't reaching compile.

Walk through:

- **Did compile actually emit your replacement?** Open `compiled/jiangyu.json` and check the asset bundle next to it. If your file isn't represented, compile dropped it. Re-run compile and watch the log column for warnings about your file.
- **Is the file at the path Studio's `Replace` row showed?** Filename and extension matter. `.txt` and unsupported formats are silently ignored. Textures and sprites need `.png` / `.jpg` / `.jpeg`. Audio needs `.wav` / `.ogg` / `.mp3`. Models need a `model.gltf` or `model.glb` inside `<target-name>/`.
- **Did you re-run compile after the edit?** A fresh edit doesn't reach the bundle until you rebuild.
- **Did you copy `compiled/` to `Mods/`?** Compile produces a folder in your project. The loader reads from `<MENACE>/Mods/`. Re-copy after every compile.
- **Are you in a scene where the asset is actually loaded?** Replacements apply when a scene loads, and the loader keeps reapplying them as new instances spawn (e.g. units the player deploys mid-mission). Make sure the scene with your asset has finished loading.

## Compile fails

The compile log column always names the failing step. The most common cases:

- **`Asset index not found or unreadable. Run 'jiangyu assets index' first.`** Open the Asset Browser. If a gate appears with an **Index assets** button, click it.
- **`Game path not configured` / `Unity Editor not found`.** Set them in the Welcome screen's Configuration panel, or via Settings once a project is open.
- **`Unity Editor version mismatch`.** Your installed Editor doesn't match what MENACE was built with. Studio shows the expected version. Install the right Editor via Unity Hub.
- **`Replacement target '<name>' could not be resolved`.** The asset name doesn't exist in the index. Double-check spelling (especially weird capitalisation and typos in MENACE's actual asset names) by searching in the Asset Browser.
- **`Replacement target '<name>' is ambiguous in the asset index`** (models only). Multiple `PrefabHierarchyObject` entries share the name. Disambiguate by appending `--<pathId>` to your replacement folder name. The error lists candidate paths.
- **`Multiple replacement <kind>s resolve to the same target name`.** Two files in your project map to the same target (e.g., both `foo.png` and `foo.jpg`). Delete one.
- **KDL parse error.** Your `templates/*.kdl` file doesn't parse. The error names the file and line.

## Audio sounds wrong

Symptom: your replacement plays back too fast / too slow / pitch-shifted / noisy.

Cause: Unity resamples mismatched audio at runtime, which pitch-shifts the sound. Match the vanilla clip's frequency and channel count when you save your replacement. Studio's detail panel shows both values. On the CLI, `assets search <name> --type AudioClip` includes them.

If a 48 kHz stereo replacement is targeting a 44.1 kHz mono clip, the playback will sound too high and fast. Re-save at the source's exact rate and channel layout.

## Model looks wrong

Several distinct symptoms. Pick the one that matches:

- **Mesh stretches or shrinks while moving** (typical on vehicle wheels): you've blended a rig that should be rigid. Rework with hard parenting in Blender so each vertex is bound 100% to a single bone.
- **Mesh deforms strangely on character joints**: opposite problem. You've rigid-skinned where the original was blended. Re-add per-vertex weight blending.
- **Mesh is way too big or way too small**: Jiangyu auto-detects metres vs centimetres by comparing your mesh's bounds to the target's. If the ratio is neither close to 1:1 nor close to 1:100, the auto-detection bails. Open the model in Blender and check the scene scale.
- **Lighting on a normal map looks wrong**: you saved the normal map without a `_NormalMap` (or `_Normal` / `_MaskMap` / `_EffectMap`) suffix, so the compiler treated it as sRGB. Rename the file to match the destination's name and recompile.
- **The compile rejects with "no renderer-path matches"**: you've renamed or restructured the objects in Blender. The compiler matches by hierarchy position. Revert the names and parents.

## Template patch didn't apply

The loader logs a `Template patch '<id>' ...` line per operation. Missing line = the patch isn't running. Visible line with an error = the field path or value doesn't match the schema.

Common causes:

- **The template id doesn't exist.** Check the Template Browser or `jiangyu templates list --type <TypeName>`.
- **The field path doesn't resolve.** Use `jiangyu templates query <Type>.<path>` to navigate the type tree and confirm the path.
- **The value kind doesn't match the field type.** Writing a `String` into a `Single` field fails loudly. The error names the expected type.
- **The field is Odin-only.** Some `DataTemplate` fields are serialised through Odin, which Jiangyu can't currently write. The Template Browser flags these.
- **Save-frozen field vs new campaign.** Some fields are read once when a save is created and frozen on disk. If your stat change isn't visible on an existing save, start a new campaign before assuming the patch failed.
