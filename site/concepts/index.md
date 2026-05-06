# Concepts

A short orientation pass for modders who want the mental model before reaching for specific docs.

## Modder tiers

Jiangyu is built around progressive layers. You only learn the next tier when you need it:

1. **Asset replacement.** Drop a file into `assets/replacements/` and ship. No code, no Unity Editor, no template authoring. Covers the bulk of skin-style mods.

2. **Template patching and cloning.** Tweak existing game data (stats, skills, weapons, perks) by writing KDL under `templates/`. Patches edit a vanilla template in place. Clones deep-copy a vanilla template into a new ID and let further patches diverge from the source.

3. **Asset additions referenced from clones.** When a cloned weapon needs its own icon (or audio, or texture), drop the file into `assets/additions/` and reference it from the clone's KDL with `set "Icon" asset="..."`. The compiler verifies the reference resolves to a real file; the loader resolves the runtime asset by name at apply time.

4. **Unity-authored content.** For genuinely new prefabs, custom shaders, or anything that needs the Unity Editor authoring surface.

5. **Runtime SDK.** Custom C# code that runs alongside the mod. For new game systems, custom UI, or hooks the data layers can't express.

The lower tiers stay simple. You don't pay the cost of the SDK to ship a re-skin.

## Name-match contract

Both replacements and additions resolve by **Unity Object name** at runtime, not by path or GUID. A replacement file at `assets/replacements/sprites/UICheckMark.png` swaps every game `Sprite` named `UICheckMark`. An addition file at `assets/additions/sprites/lrm5/icon.png` becomes a `Sprite` named `lrm5/icon`, available to any clone that references `asset="lrm5/icon"`.

This is why the asset index matters for replacements: it's the source of truth for what each game asset's name is. Build it with `jiangyu assets index`. Studio's Asset Browser and the `jiangyu assets search` CLI both read from it.

For additions, the modder picks the name. The folder layout under `assets/additions/<category>/` is the source of truth: the path under the category folder, with the extension stripped, becomes the asset's runtime name.

## Asset replacement vs addition

A replacement edits the world: every game site that references the vanilla asset picks up the modded content. An addition ships a brand-new asset that didn't exist in the vanilla game; only template clones that explicitly reference it pick it up.

| | Replacement | Addition |
| --- | --- | --- |
| Source file | `assets/replacements/<category>/<vanilla-name>.<ext>` | `assets/additions/<category>/<your-name>.<ext>` |
| Name | Must match a vanilla asset's runtime name | Modder's choice |
| Effect | Global. Every game site using that asset name gets the new content. | Local. Only template clones that reference it use it. |
| Asset index needed | Yes. Targets are validated against the indexed game catalog. | No. The modder chooses the name. |
| Use when... | Re-skinning, voice swaps, music changes | A cloned weapon, item, or skill needs distinct visuals or audio |

Both can coexist in the same mod. Both compile into the same bundle.

## Template operations

Templates take five **operations** on a field:

- **Set** writes a value into a scalar field, or into one element of a collection (`set "Field" index=N <value>`).
- **Append** adds a new element to the end of a collection.
- **Insert** adds a new element at a specific index (`insert "Field" index=N <value>`).
- **Remove** drops the element at a specific index (`remove "Field" index=N`).
- **Clear** empties a collection.

For the full syntax of each op, including descent (`type=`) and construction (`handler=`), see [Templates](/templates#operations).

Patches and clones are both lists of operations. A patch targets an existing template by ID; a clone deep-copies a vanilla template into a new ID and then applies its inline operations to the copy.

### Sequential within a template

Operations on the same template apply **in source order**. Each runs against the result of the previous one.

```kdl
patch "WeaponTemplate" "weapon.foo" {
    clear "SkillsGranted"
    append "SkillsGranted" ref="SkillTemplate" "active.aimed_shot"
    append "SkillsGranted" ref="SkillTemplate" "active.steady_aim"
}
```

Reads as: empty the list, then append two skills. Order matters. Swapping `clear` and the appends would empty the list after the appends and end up with nothing.

The same rule covers more involved compositions:

- `clear` followed by appends is the idiomatic "replace the whole list" pattern.
- `remove` followed by `insert` at the same index swaps an element.
- Two `set` directives on the same field is last-write-wins.

### Apply order across templates and mods

The loader sequences mods deterministically:

1. **Bundles load** for every mod, in lexical mod-folder order.
2. **Clones register** before patches run. A clone deep-copies a vanilla template into a new ID and adds it to the live template registry, so subsequent patches can target that new ID. Clones-before-patches is a hard rule.
3. **Patches apply** to live templates once the game has materialised the relevant template type. Each patch's directives run in source order, as above.
4. **Per-type latching**: once a template type has been patched in a session, it's marked applied and skipped on later scene loads.

This ordering means a patch on `mod_weapon.light.rocket_launcher_lrm5` runs cleanly against the LRM5 clone every time, regardless of which mod registered the clone or in what order the templates appear in the source files.

### Late-binding values

Some values resolve only at apply time, not at compile time:

- **Template references** look the target up in the live template registry. The target must already be registered. Clones-before-patches keeps cross-clone references working.
- **Asset references** look the target up in the loaded mod-bundle catalog first, then the live game-asset registry. Either source must have the asset by then.

The compiler validates everything it can verify offline (bundle assets, addition files, field types), but cross-mod references are resolved live. A typo in another mod's clone ID, or a stale reference to a removed game asset, surfaces as a runtime error logged through MelonLoader rather than a compile failure.

### Conflicts and last-write-wins

When two patches target the same field of the same template, the later application wins. Within one mod, that's source order. Across mods, that's the lexical mod-folder order from step 1.

Conflict detection is intentional rather than blocked: shipping a patch that overrides another mod's value is a valid use case (compatibility patches, rebalance overlays). The loader logs every successful application so collisions are visible in `MelonLoader/Logs/` and modders can confirm the final state matches their intent.

## Value kinds

Operations carry a value. KDL templates support several kinds:

- **Scalars** (`set "Damage" 25.0`)
- **Strings** (`set "Name" "Long Range Missile Launcher"`)
- **Enums** (`set "SlotType" enum="ItemSlot" "ModularVehicleLight"`)
- **Template references** (`set "Skill" ref="SkillTemplate" "active.aimed_shot"`): point at another live `DataTemplate`-descended template by ID.
- **Asset references** (`set "Icon" asset="lrm5/icon"`): point at a `Sprite`/`Texture2D`/`AudioClip`/`Material` asset, either an addition shipped by the mod or a vanilla game asset of the same name.
- **Composites** (`composite="OperationResources" { set "m_Supplies" 75 }`): construct a fresh value-typed payload inline.
- **Handler construction** (`handler="AddSkill" { ... }`): construct a fresh `ScriptableObject` for a polymorphic-element list (e.g. `EventHandlers`).

The category for asset references is derived from the destination field's declared Unity type, so the modder writes only the name. See [Templates](/templates) for the full KDL grammar and [Additions](/assets/additions/) for the asset-reference contract.

## Project layout

A typical Jiangyu mod project looks like this:

```text
my-mod/
  jiangyu.json          # manifest
  assets/
    replacements/       # swap vanilla assets by name
      textures/
      sprites/
      models/
      audio/
    additions/          # new assets a clone references
      sprites/
      textures/
      audio/
  templates/            # KDL: patches and clones
    weapon-foo.kdl
    skill-bar.kdl
  compiled/             # build output (don't edit, don't commit)
    jiangyu.json
    <mod-name>.bundle
```

Only `jiangyu.json`, `assets/`, and `templates/` are author-owned. `compiled/` is regenerated by `jiangyu compile`.
