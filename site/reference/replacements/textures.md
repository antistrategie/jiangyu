# Texture replacements

Replace any `Texture2D` in the game by name. The new pixels reach every consumer that already references the texture (materials, UGUI, UI Toolkit, manager caches) automatically; you don't have to enumerate them.

## Studio workflow

1. Open the [Asset Browser](../studio.md#asset-browser) pane.
2. Type the asset name in the search box. Filter the type to **Texture** if you want only textures.
3. Select the asset. The detail panel shows a `Replace` row with the path under `assets/replacements/` to save your replacement at, for example:

    ```text
    textures/window_background.png
    ```

    If the name is shared by more than one texture in the game, the detail panel also shows an `Affects` row with the count. All matching instances are painted together; see [Shared names](#shared-names) below.

4. Click **Export** to pull the vanilla pixels out as a starting point.
5. Open the exported PNG in your image editor, make your changes, save it under your project's `assets/replacements/` directory at the path Studio showed. For the example above, that's `assets/replacements/textures/window_background.png`.
6. [Compile](../studio.md#compile).

## File layout

```text
assets/replacements/textures/<target-name>.<ext>
```

`<ext>` is `.png`, `.jpg`, or `.jpeg`. Other extensions are ignored.

The basename (without extension) is the **target name** and must match the `Texture2D`'s name in the asset index.

## Shared names

When the same name covers multiple `Texture2D` assets in the game, **all of them are painted with your replacement**. The runtime matches textures by name, so targeting a single instance isn't possible.

Compile logs a warning enumerating every affected asset:

```text
warning: replacement 'Font Texture' will paint 12 Texture2D instances:
  resources.assets/Texture2D/Font_Texture--1245
  resources.assets/Texture2D/Font_Texture--1453
  resources.assets/Texture2D/Font_Texture--1676
  resources.assets/Texture2D/Font_Texture--2176
  resources.assets/Texture2D/Font_Texture--2520
  resources.assets/Texture2D/Font_Texture--2782
  resources.assets/Texture2D/Font_Texture--2793
  resources.assets/Texture2D/Font_Texture--2873
  resources.assets/Texture2D/Font_Texture--2881
  resources.assets/Texture2D/Font_Texture--4243
  resources.assets/Texture2D/Font_Texture--5052
  unity_default_resources/Texture2D/Font_Texture--10103
```

Treat the warning as a checklist. If any of those instances shouldn't change, your name is too ambiguous to replace safely; you'll have to leave that target alone or live with the side effects.

## Compile-time errors

Compile refuses the build, with a clear message, when:

- The asset index isn't built or is unreadable. Build it from Studio's index status indicator, or run `jiangyu assets index`.
- The target name doesn't resolve to any `Texture2D` in the index.
- Two replacement files in the project resolve to the same target (for example, both `foo.png` and `foo.jpg`).

## CLI alternative

For scripted workflows, the same operations exist on the CLI:

```sh
jiangyu assets index
jiangyu assets search window_background --type Texture2D
jiangyu assets export texture window_background
```

`--path-id` on `assets export texture` picks which vanilla asset to read pixels from when the name is shared. The replacement filename you save afterwards never carries a pathId suffix. Studio is the recommended surface for authoring; the CLI is intended for build pipelines and scripting.
