# Your first re-skin

This walkthrough replaces the MENACE main-menu logo with a flat red square. It is the simplest mod there is: one image file, no code. It assumes you have finished [Installation](./installation), and that you have an image editor that exports PNG (GIMP, Krita, Photoshop).

A replacement swaps a game asset for your own by matching its name. You drop a file in the right place, compile, and the loader paints your version over every use of that asset at runtime.

## Create a project

From Studio's welcome screen, click **New project**, pick a folder, and name it `RedLogo`. Studio scaffolds the manifest and the dormant `code/` and `unity/` projects. The defaults are fine.

## Find the asset

You are replacing `menace_logo_main_menue`, the logo on the main menu. The misspelling is the real asset name, so use it as written.

1. Open the [Asset Browser](/studio#asset-browser) from the palette (**Ctrl+Shift+P** → **Open Asset Browser**).
2. The first time, it offers to build the asset index. Build it. This takes a few minutes once, and later searches read the cache.
3. Search `menace_logo_main_menue`, filter to **Texture** if you like, and select the match.

The detail panel shows the class (`Texture2D`) and a **Replace** row with the path to save your file at, `textures/menace_logo_main_menue.png`. That path is relative to your project's `assets/replacements/`.

## Export, edit, save

Click **Export** in the detail panel and choose **Export to default**. Studio pulls the vanilla logo out as a PNG, and the success toast has a **Reveal** action that opens it.

Open that PNG in your image editor, fill it solid red, and save it into your project at the path the Replace row gave you:

```text
RedLogo/assets/replacements/textures/menace_logo_main_menue.png
```

## Compile and install

Press **Ctrl+Shift+B**. When it finishes, Studio offers a **Reveal** action that opens `compiled/`. Copy that folder into MENACE's `Mods/`, renamed to `RedLogo`:

```text
<MENACE>/Mods/
  Jiangyu.Loader.dll
  RedLogo/
    jiangyu.json
    *.bundle
```

The palette's **Deploy Mod** command does the copy for you.

## Verify

Launch MENACE and watch the main menu. The logo is now a solid red square. If it is, everything connected: the loader found your file in `Mods/`, matched it to the logo by name, and repainted it.

If the logo did not change, open `<MENACE>/MelonLoader/Latest.log` and search for the asset name. The loader logs what it applies, and [Troubleshooting](/troubleshooting) covers the common causes.

## Next steps

- [Your first patch](./first-patch) changes game data, like stats and skills, rather than art.
- [Replace a texture](/assets/replacements/textures) and its sibling pages cover sprites, models, and audio in full.
