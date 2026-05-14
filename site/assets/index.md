# Assets

Jiangyu mods can put two kinds of asset content into the game:

- **[Replacements](/assets/replacements/textures)** swap an existing game asset by name. The new file ships in the mod's bundle, and at load time Jiangyu finds every game asset of the same name and points it at the modded content. Use replacements for re-skins, voice swaps, music changes, and any other "make the existing thing look or sound different" workflow.

- **[Additions](/assets/additions/)** introduce a brand-new asset that didn't exist in the game. Templates and clones can then point at it (`set "Icon" asset="my-new-icon"`). Use additions when a cloned item, weapon, or skill needs its own icon, sprite, or audio.

The two are orthogonal. A mod can ship any mix of replacements, additions, and pure template patches in the same project.

## Same naming convention

Both routes resolve assets by Unity Object name at runtime. A replacement file under `assets/replacements/sprites/UICheckMark.png` is loaded so that any game `Sprite` named `UICheckMark` picks up the new pixels. An addition file under `assets/additions/sprites/lrm5/icon.png` is loaded as a `Sprite` named `lrm5/icon`, available to any clone that references `asset="lrm5/icon"`.

The same asset categories apply across both: textures, sprites, audio, models. Per-category contracts (atlas compositing, mesh contracts, audio playback hooks) are documented on each replacement page and apply equally to additions.

## How to choose

| You want to ... | Use |
| --- | --- |
| Re-skin an existing icon, weapon, or unit | [Replacement](/assets/replacements/textures) |
| Swap a voice line or music track | [Replacement](/assets/replacements/audio) |
| Give a cloned item its own distinct icon | [Addition](/assets/additions/sprites) |
| Add a new audio clip a clone's skill plays | [Addition](/assets/additions/audio) |
| Change every appearance of a unit globally | [Replacement](/assets/replacements/models) |

When the same mod contains both, replacements and additions live side by side under `assets/` in the project directory:

```text
my-mod/
  assets/
    replacements/
      textures/
      sprites/
      models/
      audio/
    additions/
      sprites/
      textures/
      audio/
  templates/
  jiangyu.json
```

Both directories are walked recursively and packed into the same compiled bundle.

## Not a separate pipeline

A "modified copy" of a vanilla asset (the cocaine icon with a top hat drawn on) isn't its own kind of asset. Either ship a regular addition with the modified pixels, or replace the vanilla asset globally.
