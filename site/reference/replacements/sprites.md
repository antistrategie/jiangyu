# Sprite replacements

Replace any `Sprite` in the game by name. Sprites ride on texture mutation: the new pixels reach every UI element, `SpriteRenderer`, and `Image` component that already references the sprite, automatically.

Most sprites in MENACE are packed into atlases. Jiangyu handles atlases for you at compile time; you author your replacement at the sprite's region size and the compiler composites it into the atlas without disturbing the other sprites packed alongside it. See [Atlas-backed sprites](#atlas-backed-sprites) below.

## Studio workflow

1. Open the [Asset Browser](../studio.md#asset-browser) pane.
2. Type the asset name in the search box. Filter the type to **Sprite** if you want only sprites.
3. Select the asset. The detail panel shows a `Replace` row with the path under `assets/replacements/` to save your replacement at, for example:

    ```text
    sprites/UICheckMark.png
    ```

    For atlas-backed sprites, the detail panel also shows two informational rows:

    - `Atlas` names the backing atlas texture.
    - `Rect` gives the sprite's region dimensions, for example `64 × 64 px`. Author your replacement at that size.

4. Click **Export** to pull the vanilla pixels out as a starting point.
5. Open the exported PNG in your image editor, make your changes, save it under your project's `assets/replacements/` directory at the path Studio showed. For the example above, that's `assets/replacements/sprites/UICheckMark.png`.
6. [Compile](../studio.md#compile).

## File layout

```text
assets/replacements/sprites/<target-name>.<ext>
```

`<ext>` is `.png`, `.jpg`, or `.jpeg`. Other extensions are ignored.

The basename (without extension) is the **target name** and must match the `Sprite`'s name in the asset index.

## Atlas-backed sprites

Many sprites in MENACE share a backing `Texture2D` (an atlas). Mutating an atlas as a whole would corrupt every sprite drawn from it, so Jiangyu's compiler reads the sprite's `textureRect` from the asset index and composites your replacement image into that exact rectangle inside a copy of the original atlas. Co-tenant sprites are pixel-for-pixel identical because their regions aren't touched.

Compile shows a short info line per atlas it composited into:

```text
info: composited 1 sprite replacement into atlas 'MenaceFontIcons'
```

A few rules to keep in mind:

- **Author at the sprite's `textureRect` size.** Studio's detail panel shows it. If your image dimensions don't match, the compiler resamples and logs a warning:

    ```text
    warning: sprite 'UICheckMark' replacement is 128×128 but its textureRect in atlas
             'MenaceFontIcons' is 64×64; resampling to fit.
    ```

- **Multiple sprites in the same atlas compose into one output.** If you replace three sprites that all live in `MenaceFontIcons`, the compiler emits one `Texture2D` replacement under the atlas name with all three regions composited.
- **Replacing the atlas itself plus individual sprites is allowed.** Drop a `Texture2D` replacement at `assets/replacements/textures/<atlas-name>.<ext>` to provide a new base atlas. Any sprite replacements targeting that atlas composite on top, overriding their regions.

## Shared names

When the same name covers multiple `Sprite` assets in the game, all of them are replaced. For non-atlas sprites, each backing texture is mutated independently. For atlas-backed sprites, each atlas's region for that name is composited.

Compile logs a warning enumerating every affected sprite, the same way [textures](./textures.md#shared-names) do. Treat the warning as a checklist; if any of the listed instances shouldn't change, your name is too ambiguous to replace safely.

## Compile-time errors

Compile refuses the build, with a clear message, when:

- The asset index isn't built or is out of date. Rebuild with `jiangyu assets index`. Atlas compositing needs sprite `textureRect` metadata that an out-of-date index may not have.
- The target name doesn't resolve to any `Sprite` in the index.
- Two replacement files in the project resolve to the same target.

## CLI alternative

```sh
jiangyu assets index
jiangyu assets search UICheckMark --type Sprite
jiangyu assets export sprite UICheckMark
```

Studio is the recommended surface for authoring; the CLI is intended for build pipelines and scripting.
