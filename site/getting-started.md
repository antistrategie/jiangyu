# Getting started

This walkthrough takes you from zero to a working mod that replaces the MENACE main-menu logo. Once you've shipped one replacement end to end, the [Reference](./reference/manifest.md) pages cover the other asset types and template patches.

## Prerequisites

You need:

- **MENACE installed** (Steam or a game build with `MENACE.exe`).
- **[MelonLoader (latest)](https://github.com/LavaGang/MelonLoader/releases)** installed into your MENACE folder. Follow MelonLoader's own install instructions for your platform.
- **Jiangyu**. From the [latest release](https://github.com/antistrategie/jiangyu/releases/latest), download:
    - [`Jiangyu.Loader.dll`](https://github.com/antistrategie/jiangyu/releases/latest/download/Jiangyu.Loader.dll), the in-game loader.
    - **Studio** for your platform: [Windows](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-studio-win-x64.zip) or [Linux](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-studio-linux-x64.zip).
    - **CLI** (optional, for scripting): [Windows](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-cli-win-x64.zip) or [Linux](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-cli-linux-x64.zip).
- **Unity Editor** (optional). Required only when you compile asset replacements (textures, sprites, models, audio). Template-only mods don't need it. Install via [Unity Hub](https://unity.com/download). Jiangyu expects 6000.0.72f1, which you download from the [Unity archive](https://unity.com/releases/editor/archive) and add to Hub. Studio shows the exact version when you set its path. If the version here differs, trust Studio. Jiangyu auto-detects MENACE's Unity version and requires editor updates when MENACE updates its Unity version.
- **An image editor** (anything that exports PNG: GIMP, Photoshop, Krita).

## Install the Jiangyu Loader

The Loader is the in-game framework that discovers your mods and applies them at runtime (replacements, template patches, dependency checks). It's a single DLL.

1. Find your MENACE install folder (the one with `MENACE.exe`).
2. Open the `Mods/` directory inside it. MelonLoader created this on its first run.
3. Drop `Jiangyu.Loader.dll` into `Mods/`.

You only do this once per MENACE install. Updating Jiangyu means replacing this one file.

## Configure Studio

Launch **Jiangyu Studio**. The Welcome screen appears. Expand the **Configuration** panel. That's where Studio surfaces missing prerequisites and lets you set the relevant paths.

- **MENACE not found**: click **Set path…** and pick the directory containing `MENACE.exe`. The **Open Project** and **New Project** buttons only appear once this is set.
- **Unity Editor not found**: click **Set path…** and pick your Unity Editor binary. The tooltip shows the expected version. Skip this if you only plan to ship template-only mods. This tutorial replaces a texture, so set it now.
- **MelonLoader not installed**: indicates MelonLoader isn't present in your MENACE folder. Mods don't run without it. Install MelonLoader before continuing.

The Welcome screen writes these into a global config file shared with the `jiangyu` CLI. After a project is loaded, the same fields appear in the Settings dialog (palette → **Settings**).

## Create a project

From the Welcome screen, click **New project**. Pick a directory and a name (for this tutorial, call it `RedLogo`). Studio scaffolds:

```text
RedLogo/
  jiangyu.json
  .gitignore
```

`jiangyu.json` is the manifest. See [Manifest](./reference/manifest.md) for the full reference. For now the defaults are fine.

## Find a target asset

You're going to replace `menace_logo_main_menue`, the logo MENACE shows on the main menu. (The spelling `menue` is the actual name of the asset; use it as-is.)

1. Open the [Asset Browser](./reference/studio.md#asset-browser) from the palette: press **Ctrl+Shift+P** and run **Open Asset Browser**.
2. The first time, the browser shows a gate with an **Index assets** button. Click it. The first build takes a few minutes. Later searches read the cached index instantly.
3. Once the index finishes building, type `menace_logo_main_menue` in the search box.
4. Filter the type to **Texture** if you want to narrow the results.
5. Click the matching row.

The detail panel on the right shows:

- `Class`: `Texture2D`
- `Replace`: `textures/menace_logo_main_menue.png`

That `Replace` row tells you exactly where to drop your replacement, under your project's `assets/replacements/` directory.

## Export the vanilla pixels

Click **Export** in the detail panel and choose **Project** as the destination. Studio pulls the vanilla `menace_logo_main_menue` out of the game and writes it as a PNG. A success toast shows up with a **Reveal** action that opens the file location.

## Edit it

Open the exported PNG in your image editor. For this tutorial, fill the entire image with a solid red colour and save the file.

Save the modified PNG at:

```text
<project>/assets/replacements/textures/menace_logo_main_menue.png
```

That's the full path the Studio detail panel showed you, prefixed with `assets/replacements/` to land inside your project.

## Compile

Press **Ctrl+Shift+B** in Studio. The compile runs in the background. The status bar shows progress.

When compile finishes, Studio pushes a toast with a **Reveal** action. Click it to open `compiled/` in your file manager. That folder is your shippable mod.

## Install your mod

Copy your project's `compiled/` folder into MENACE's `Mods/` directory, renaming it to `RedLogo` so the mod has a recognisable name on disk. Your `Mods/` folder should now look like:

```text
<MENACE>/Mods/
  Jiangyu.Loader.dll
  RedLogo/
    jiangyu.json
    *.bundle
```

## Verify

Launch MENACE and watch the main menu. The logo should now be a solid red square. If you see it, the round-trip works: your replacement reached the game, the loader matched it by name, and the texture mutation applied.

If something looks wrong, check `<MENACE>/MelonLoader/Latest.log`. Jiangyu logs everything it does on scene load. Failures, warnings, and skipped targets all appear there.

## Next steps

You've shipped one texture replacement. The same Studio workflow handles every other replacement type:

- [Texture replacements](./reference/replacements/textures.md) covers the texture contract in full.
- [Sprite replacements](./reference/replacements/sprites.md) for UI sprites and atlas-backed sprites.
- [Model replacements](./reference/replacements/models.md) for skinned 3D meshes.
- [Audio replacements](./reference/replacements/audio.md) for voice lines, SFX, and music.
- [Templates](./reference/templates.md) for tweaking game data: stats, skills, weapons, perks.

The [Studio reference](./reference/studio.md) covers every Studio surface in detail. The [CLI reference](./reference/cli.md) covers the equivalent command-line surface for build pipelines and scripting.
