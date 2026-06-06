# Installation

This sets up the Jiangyu toolchain: the in-game loader, Studio, and the build tools. Once it is done, [Your first patch](./first-patch) walks you through making a mod.

## Prerequisites

A few things need to be in place. This core set is required for every mod:

- **MENACE**, installed through Steam or any build with `MENACE.exe`.
- **[MelonLoader](https://github.com/LavaGang/MelonLoader/releases)** (latest), installed into your MENACE folder. Follow MelonLoader's own instructions for your platform. Nothing you build runs without it.
- **.NET**. The [.NET 10 SDK](https://dotnet.microsoft.com/download) runs Jiangyu's toolchain. The [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) is also needed, because a mod's `code/` and the loader target `net6.0`. A compile uses both.
- **Unity Editor**, version `6000.0.72f1`, via [Unity Hub](https://unity.com/download) (older versions are in the [archive](https://unity.com/releases/editor/archive)). Studio shows the exact version it expects, so trust that over these docs if they ever differ.
- **Jiangyu**, from the [latest release](https://github.com/antistrategie/jiangyu/releases/latest):
    - **Studio** for [Windows](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-studio-win-x64.zip) or [Linux](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-studio-linux-x64.zip). It bundles the in-game loader and deploys it for you.
    - **CLI**, optional, for scripting: [Windows](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-cli-win-x64.zip) or [Linux](https://github.com/antistrategie/jiangyu/releases/latest/download/jiangyu-cli-linux-x64.zip).
    - [`Jiangyu.Loader.dll`](https://github.com/antistrategie/jiangyu/releases/latest/download/Jiangyu.Loader.dll), optional, only if you install the loader by hand rather than from Studio.

## Point Studio at the game

Launch Studio. The welcome screen opens with a Configuration panel that flags whatever is missing.

- **MENACE not found**: click **Set path…** and pick the folder with `MENACE.exe`.
- **Unity Editor not found**: leave it for now. Set it when you first build a prefab or UI mod. The tooltip shows the version to install.
- **MelonLoader not installed**: install MelonLoader into your MENACE folder before going further.

These paths are written to a global config file that the `jiangyu` CLI shares. Once a project is open, the same fields live in Settings (palette → **Settings**).

## Install the loader

The loader is one DLL that MENACE runs on startup. It finds your mods under `Mods/` and applies them. Studio bundles it, so there are two ways to get it in place:

- **From Studio** (the easy path): with a project open, open **Settings** (palette → **Settings**), find the **Loader** row, and click **Deploy user**. Studio copies its bundled loader into the game's `Mods/` folder and shows the deployed version. Updating Jiangyu later is the same row, where the button reads **Update** instead. The row also offers a **dev** build, which adds the Studio bridge for live iteration. Deploy **user** for normal play. See [Deploying the loader](/studio#deploying-the-loader) for the difference.
- **By hand**: drop [`Jiangyu.Loader.dll`](https://github.com/antistrategie/jiangyu/releases/latest/download/Jiangyu.Loader.dll) into `<MENACE>/Mods/` yourself. MelonLoader creates `Mods/` on its first run.

That is the whole setup. Next, [make your first mod](./first-patch).
