# Your first patch

This walkthrough makes a working mod that buffs the carbine, so every shot hits far harder. You change one field on one template, compile, and see the difference in a real mission. It assumes you have finished [Installation](./installation).

A patch edits a value on the data MENACE ships with. Your `resources.assets` file is never touched. The loader applies the change at runtime, and removing the mod restores the vanilla value next launch.

## Create a project

From Studio's welcome screen, click **New project**, pick a folder, and name it `CarbineBuff`. Studio scaffolds:

```text
CarbineBuff/
  jiangyu.json      # the manifest
  .gitignore
  code/             # C# project, dormant until you add code
  unity/            # Unity project, dormant until you add prefabs or UI
```

The defaults in `jiangyu.json` are fine for now. `code/` and `unity/` stay empty and ship nothing.

## Find the carbine

A weapon's damage is a stat on its own template, so that is what you patch.

1. Open the [Template Browser](/studio#template-browser) from the palette (**Ctrl+Shift+P** → **Open Template Browser**).
2. The first time, it offers to build the template index. Build it. The first build takes a moment, then later searches read the cache.
3. Search `carbine`. One of the results is `weapon.generic_carbine_tier1_spc`, a `WeaponTemplate`. That is the standard service rifle most local forces carry. Select it.

The detail panel lists the weapon's fields and their vanilla values. Find **Damage**: each shot lands `9`. That is the number you will raise.

## Write the patch

In the detail panel, open the **Scaffold** dropdown and pick **Create Patch**. Studio writes a patch block for this weapon into a file under `templates/` and opens it:

```kdl
patch "WeaponTemplate" "weapon.generic_carbine_tier1_spc" {
}
```

Add one line inside the block to set `Damage`:

```kdl
patch "WeaponTemplate" "weapon.generic_carbine_tier1_spc" {
    set "Damage" 25
}
```

Save with **Ctrl+S**. The Visual editor does the same thing if you prefer: every field shows as a row, and typing `25` into `Damage` writes that `set` for you, with the vanilla `9` shown beside it so you know what you are overriding.

## Compile and install

Press **Ctrl+Shift+B**. When the compile finishes, Studio offers a **Reveal** action that opens `compiled/`.

Copy that `compiled/` folder into MENACE's `Mods/`, renamed to `CarbineBuff`:

```text
<MENACE>/Mods/
  Jiangyu.Loader.dll
  CarbineBuff/
    jiangyu.json
```

The palette's **Deploy Mod** command does the copy for you if you would rather not do it by hand.

## Verify

Launch MENACE. The reliable check is the log: open `<MENACE>/MelonLoader/Latest.log` and search for `weapon.generic_carbine_tier1_spc`. The loader logs every patch it applies, so a line for your weapon means the value reached the game.

The satisfying check comes in a mission, where a unit holding the generic carbine now hits for `25` a shot rather than `9`. Weak enemies that used to take a couple of hits drop to one.

No line in the log for your weapon means the mod was not read at all. Confirm `compiled/` was copied into `Mods/` under the `CarbineBuff` folder, then see [Troubleshooting](/troubleshooting) if it still does not show.

## Next steps

That is a one-field patch. The same flow scales up from here:

- [Templates](/templates) is the full KDL reference. It covers every operation, cloning a template into a new variant, and reaching nested values like the fire skill a weapon grants.
- [Replace an asset](/assets/replacements/textures) is the other common starting point: re-skinning textures, sprites, models, and audio.
- [The SDK](/sdk/) is for the rarer mod that needs C#.
