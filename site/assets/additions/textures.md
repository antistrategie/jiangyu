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

## How textures are imported

A texture whose width and height both divide by four compiles to DXT5 (DXT1 when it has no alpha), the block format the game uses for its own portraits, with a mipmap chain, bilinear filtering and repeat wrapping. Any other size stays uncompressed because block formats cannot encode it, at four times the memory: a 2192×3668 portrait is 10 MB compressed and 43 MB uncompressed. Author textures at dimensions divisible by four. This applies to additions only: a [replacement texture](/assets/replacements/textures) stays uncompressed, because the loader re-encodes it into the game's own texture at runtime and a second lossy pass would compound the first.

## Compile-time errors

Same as [sprite additions](/assets/additions/sprites#compile-time-errors): missing files, duplicate logical names, and wrong destination field type are all rejected at compile time.
