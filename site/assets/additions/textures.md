# Texture additions

Ship a new `Texture2D` asset and reference it from a template clone. Use this when a clone has a `Texture2D`-typed field (raw textures, render targets, custom material maps) rather than a `Sprite`-typed field.

For UI icons and item portraits use [sprite additions](/assets/additions/sprites) instead. Most game-data templates expose icon-style fields as `Sprite`, not `Texture2D`.

## File layout

```text
assets/additions/textures/<logical-name>.<ext>
```

`<ext>` is `.png`, `.jpg`, or `.jpeg`. The basename (with subdirs, without extension) is the logical name the modder writes in KDL.

## KDL syntax

```kdl
clone "...Template" from="..." id="..." {
    set "TextureField" asset="my-folder/my-texture"
}
```

The category is inferred from the destination field's declared Unity type. The compiler walks `assets/additions/textures/` because the field is `Texture2D`.

## Compile-time errors

Same as [sprite additions](/assets/additions/sprites#compile-time-errors): missing files, duplicate logical names, and wrong destination field type are all rejected at compile time.

## Runtime resolution

Same two-phase lookup: mod bundle catalog first, live game-asset registry second. A texture addition with the same name as a vanilla texture overrides only at the apply-time write into the cloned template's field, not globally.

## CLI alternative

```sh
jiangyu assets export texture <vanilla-texture-name> --output assets/additions/textures/my-folder/my-texture.png
jiangyu compile
```
