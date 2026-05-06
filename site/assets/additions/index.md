# Additions

An **addition** ships a brand-new asset in your mod and lets a template clone reference it by name. Use additions when a cloned item, weapon, or skill needs its own visual or audio identity that doesn't exist in the vanilla game.

The modder workflow is:

1. Drop the asset file under `assets/additions/<category>/<name>.<ext>`.
2. Reference it from a template clone with `set "FieldName" asset="<name>"`.
3. Compile and ship.

## File layout

```text
assets/additions/
  sprites/
  textures/
  audio/
```

Each category folder is walked recursively. The asset's runtime name is its **path under the category folder, with the extension stripped**. So `assets/additions/sprites/lrm5/icon.png` becomes a `Sprite` referenced as `asset="lrm5/icon"`.

Forward slashes (`/`) in the reference mirror the folder layout. Use them whenever you want to organise files by feature, weapon family, or any other grouping.

## KDL syntax

```kdl
clone "ItemDefinition" from="pen" id="fancy-pen" {
    set "Name" "Fancy Pen"
    set "Icon" asset="lrm5/icon"
}
```

The category is **inferred** from the destination field's declared Unity type. `Icon` on `ItemDefinition` is a `Sprite`, so the compiler looks for the file under `assets/additions/sprites/`. You don't have to repeat the category in the KDL.

## Supported categories

| Category | Field types | Page |
| --- | --- | --- |
| Sprite | `UnityEngine.Sprite` | [Sprites](/assets/additions/sprites) |
| Texture | `UnityEngine.Texture2D` | [Textures](/assets/additions/textures) |
| Audio | `UnityEngine.AudioClip` | [Audio](/assets/additions/audio) |
| Material | `UnityEngine.Material` | (no dedicated page) |

## Compile-time checks

The compiler walks `assets/additions/` once at build time and verifies:

- Every `asset="..."` reference resolves to a real file under the inferred category folder. Missing files fail the build with the expected path:

    ```text
    Template patch '...': asset="lrm5/icon" was not found at
    assets/additions/sprites/lrm5/icon.<ext>. Add the asset file or correct
    the reference name.
    ```

- Two files in the same category folder don't share the same logical name with different extensions. `assets/additions/sprites/icon.png` and `assets/additions/sprites/icon.jpg` are a hard error: the runtime can't disambiguate by name.

- The destination field's declared Unity type is a supported asset class. `asset="..."` on a `string` or numeric field is rejected with a clear message.

## Runtime resolution

At apply time the loader walks the mod's bundle catalog first, then falls back to the live game-asset registry filtered by name. Bundle hits win on collision: a modder shipping `assets/additions/sprites/cocaine.png` overrides the vanilla `cocaine` sprite for any clone that references `asset="cocaine"`, while leaving the vanilla item using its original sprite.

## Backslashes are not allowed

KDL `asset="..."` must use forward slashes only:

```kdl
set "Icon" asset="lrm5/icon"      // good
set "Icon" asset="lrm5\\icon"     // rejected at parse time
```

The compiler walks the filesystem with native separators (so Windows authors don't need to do anything special on disk) but the authored KDL stays portable.
