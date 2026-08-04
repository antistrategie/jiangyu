# Save File Name Derivation (per-save mod state sidecars)

Date: 2026-08-04

## Goal

Establish how `SaveSystem.Save` turns a typed save name into an on-disk file name, so the
per-save mod state sidecar (`<savePath>.jiangyu.<modId>.json`) lands beside the file the game
actually wrote.

## Symptom

A new save created with spaces or non-Latin characters in its name loads with every mod's
persistent state cleared: relationship levels, squad size caps and Black Market pool settings
fall back to their defaults. Autosaves are unaffected, and overwriting an existing save is
unaffected even when that save's name contains the same characters.

## Method

`SaveSystem` and `StringExtensions` have no managed bodies in the Il2CppInterop assemblies, so
the RVAs from the Cpp2IL stub assembly (`MelonLoader/Dependencies/Il2CppAssemblyGenerator/
Cpp2IL/cpp2il_out/Assembly-CSharp.dll`, `[Address(RVA=..., Offset=...)]`) were disassembled out
of `GameAssembly.dll` with `objdump -b binary -m i386:x86-64`. Two saves already on disk served
as the cross-check.

## Findings

`SaveSystem.Save(SaveStateType, string _filePath, string _saveGameName)` (RVA `0x5CB270`) picks
its target file in three steps:

1. `_filePath` non-empty wins outright. This is the overwrite path.
2. Otherwise the save name goes through `Menace.Tools.StringExtensions.ToSnakeCaseFileName`
   (RVA `0x63A1B0`) and the result is passed to `SaveSystem.GetSaveFilePath`.
3. If the folded name is null, or `folded.Replace('_', ' ')` is whitespace, the game falls back
   to `"manual_" + TimeToString(now)`.

`GetSaveFilePath` (RVA `0x5CA870`) is a plain `<Saves folder>/<name>.save` concatenation. It
applies no transformation of its own, which is why calling it with a raw save name produced a
path that no file ever occupied.

Save writes one temporary file, copies it onto `latest.save`, then moves it to the real slot, so
the alias is byte identical to the file just written. `cmp latest.save auto_20260803_153953.save`
on disk confirms it. That makes the alias an exact fingerprint for identifying a save whose path
the postfix is not handed.

`ToSnakeCaseFileName` trims, returns `""` for an empty result, and appends each character
through `AppendSnakeCaseFileName` (RVA `0x6386B0`):

- ASCII digits and lowercase letters are kept verbatim.
- `A`-`Z` become lowercase, preceded by `_` when the previous character was not uppercase and
  the buffer does not already end in `_`. The first character never gets that separator.
- `ß` becomes `ss`, `Ä`/`ä` become `ae`, `Ö`/`ö` become `oe`, `Ü`/`ü` become `ue`.
- Everything else, which is every space, punctuation mark, Korean syllable, CJK ideograph and
  Cyrillic letter, becomes `_`, collapsed so consecutive folds never produce a double underscore.

So `"my save"` is written to `my_save.save`, `"MySave"` to `my_save.save`, and `"세이브"` folds
to `"_"`, fails the whitespace guard, and lands in `manual_<timestamp>.save`.

## Cross-check against saves on disk

Two saves in `AppData/LocalLow/Overhype Studios/Menace/Saves` carry the typed name in their
header, length-prefixed just after the `strategy_config` block:

| Typed name | Derived | On-disk file |
| --- | --- | --- |
| `brok33?` | `brok33_` | `brok33_.save` |
| `:)` | `_` (blank after the guard) | `manual_20260412_144433.save` |

Both match the disassembled rule.

## Consequence for the loader

`SaveNameFold` folds the save name through the game's own `ToSnakeCaseFileName` before asking for
the path, and returns null when the fold leaves nothing or the interop call throws. Both the save
path and the sidecar sweep go through it, so the two cannot drift apart. The surrounding decision
(an explicit path wins, a derived path with no file behind it is discarded) lives in
`Jiangyu.Shared.State.SaveSlotResolver` so it is unit-testable away from IL2CPP.

A save with no explicit path and a null slot falls through to the recovery, which covers autosaves,
quicksaves, the `manual_<timestamp>` fallback and any drift in the fold. That recovery identifies
the file by streaming it against the latest alias, and falls back to the newest file by mtime when
the alias cannot be read or nothing matches it. An explicit path is never recovered from: with no
file behind it the write did not land, and a guess would attach the state to an unrelated slot.

`ModStateSidecarRepairPlan` sorts the sidecars a save folder has stranded. A sidecar whose folded
save is also gone is dead weight and is deleted. One whose folded save is there is moved onto it,
unless that save already carries state for the same mod, or another sidecar in the same sweep has
claimed the move: those are renamed to `.orphan`, which is outside the sweep's glob and keeps the
state readable.
