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

Standing portraits use trilinear filtering and a mipmap bias of `-0.5` to retain detail as the UI scales them. Jiangyu selects this automatically for texture additions referenced by `SpeakerTemplate.StandLookLeftImage`, `StandLookRightImage`, or `StandLookRightInactiveImage` in KDL. No preset or filename convention is needed. The setting is baked into the texture and applies wherever it is reused. Standing portraits omit the CPU-readable pixel copy to reduce RAM use. Runtime code can display them normally, but CPU pixel access such as `GetPixels` requires a separate readable copy. Compression, mipmaps and wrapping stay the same.

## Load standing portraits on display

Set `"deferStandingPortraits": true` in your source `jiangyu.json` to leave standing portrait additions unloaded until they are displayed:

```json
{
  "name": "MyMod",
  "deferStandingPortraits": true
}
```

Native leader portraits, conversations, event dialogue and the tactical selected-unit panel request the appropriate image automatically. Each texture loads on its first request and stays cached for the session. Image resolution, compression and sampling are unchanged. This reduces initial graphics memory use, with the loading cost paid when an image is first needed.

Custom UI must request standing artwork through `Jiangyu.Game.Ui.Portraits.GetStanding` on the main thread:

```csharp
using Jiangyu.Game.Ui;

var texture = Portraits.GetStanding(speaker, StandingPortrait.Left);
```

Choose `Left`, `Right` or `Inactive` to match the speaker field you need. Omitting the second argument selects `Right`. Before that request, the raw `SpeakerTemplate.StandLook*Image` field can still contain its inherited value. Avoid caching raw fields during template initialisation when opting in.

Deferral applies to direct KDL `set` assignments of bundled additions to the three standing portrait fields. Portraits on templates used as clone sources stay eager so their children inherit the assigned artwork. Replacement textures and other asset references keep their normal loading behaviour. The option defaults to `false`.

## Compile-time errors

Same as [sprite additions](/assets/additions/sprites#compile-time-errors): missing files, duplicate logical names, and wrong destination field type are all rejected at compile time.
