# Sprite additions

Ship a new `Sprite` asset and reference it from a template clone. Common use case: a cloned weapon, item, or skill needs its own icon.

## Workflow

1. Drop a PNG or JPG under `assets/additions/sprites/`. The path under that directory (with the extension stripped) is the asset's runtime name. Subdirectories are allowed and encouraged for organisation.

    ```text
    assets/additions/sprites/lrm5/icon.png
    assets/additions/sprites/lrm5/icon_equipment.png
    ```

2. Reference each asset from a template clone:

    ```kdl
    clone "ModularVehicleWeaponTemplate" from="mod_weapon.medium.rocket_launcher" id="mod_weapon.light.rocket_launcher_lrm5" {
        set "Icon" asset="lrm5/icon"
        set "IconEquipment" asset="lrm5/icon_equipment"
    }
    ```

3. [Compile](/studio#compile). The compiler verifies each `asset="..."` resolves to a real file and packs the sprite into the mod's bundle.

## File layout

```text
assets/additions/sprites/<logical-name>.<ext>
```

`<ext>` is `.png`, `.jpg`, or `.jpeg`. The basename (with subdirs, without extension) is the **logical name** the modder writes in KDL.

Two files sharing the same logical name with different extensions in the same folder (`icon.png` and `icon.jpg`) are a hard compile error.

## Authoring at the right size

There's no atlas compositing for additions. The Sprite is created from the full PNG, with the pivot at `(0.5, 0.5)` and a default `pixelsPerUnit` of 100. Author the file at the size you want the icon to render in-game.

If you're starting from a vanilla asset to use as reference, [export it as a texture](/assets/replacements/textures#cli-alternative) first:

```sh
jiangyu assets export texture <vanilla-name> --output assets/additions/sprites/my-icon.png
```

## Studio workflow

1. Open the [Asset Browser](/studio#asset-browser) pane.
2. Find the vanilla asset you want to start from.
3. Click **Export** to pull the pixels out, save under `assets/additions/sprites/<your-logical-name>.png`.
4. In the [Visual Editor](/studio), open the clone you want to attach it to. The asset-typed field shows an **Asset** kind chip and a path input. Type your logical name (`lrm5/icon`).
5. [Compile](/studio#compile).

## Compile-time errors

Compile refuses the build, with a clear message, when:

- An `asset="..."` reference doesn't resolve to a file under `assets/additions/sprites/`.
- Two files share the same logical name with different extensions.
- The destination field's declared Unity type isn't `Sprite`. The same `asset="..."` syntax works for textures and audio (with the appropriate Unity field type); the category is inferred from the field, not stated in KDL.

## Runtime resolution

At apply time the loader looks up the sprite in the mod's bundle catalog first, then falls back to the live game-asset registry. The fallback lets a clone reference a vanilla sprite by name without the modder having to ship a duplicate. To override a vanilla sprite for one clone only (without touching every game site that uses it), ship the addition with the same name and the bundle wins.

## CLI alternative

```sh
jiangyu assets search <vanilla-sprite-name> --type Sprite
jiangyu assets export sprite <vanilla-sprite-name>
jiangyu compile
```
