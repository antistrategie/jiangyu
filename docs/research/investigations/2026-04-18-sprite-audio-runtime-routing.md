# 2026-04-18 — Sprite and Audio Runtime Routing Smoke Test

## Purpose

First in-game end-to-end smoke test of the convention-first **texture**, **sprite**, and **audio** replacement paths, driven by a minimal test mod rather than WoMENACE.

The compile-time and bundle-loading sides of all three categories had passing unit tests and worked in isolation; what was missing was a live datapoint on the loader's runtime-apply logic against real MENACE assets.

## Setup

- Test mod (`RedSoldierTest`) with:
  - three red 512×512 PNG replacements for the three `local_forces_basic_soldier*_BaseMap` `Texture2D` assets (pathIds 1473, 1511, 1610)
  - one red 144×144 PNG replacement for the `icon_hitpoints` `Sprite` (pathId 4151)
  - one 0.25-second 880 Hz sine wave replacement for the `button_click_01` `AudioClip` (pathId 15854)
- WoMENACE temporarily moved out of `Mods/` so the test mod was the only `jiangyu.json`-driven content.
- Loader built with `Jiangyu.Shared` + `System.Text.Json` + `System.Text.Encodings.Web` merged into a single DLL via ILRepack.
- Game launched; tactical scene entered with a basic-soldier squad visible; UI buttons exercised.

## Observed behaviour

MelonLoader log (trimmed):

```
[Jiangyu] Loading bundle: redsoldiertest.bundle
[Jiangyu]   Registered audio asset: button_click_01
[Jiangyu]   Registered sprite asset: icon_hitpoints--4151.sprite
[Jiangyu]   Registered texture asset: local_forces_basic_soldier_2_BaseMap (512x512)
[Jiangyu]   Registered texture asset: local_forces_basic_soldier_3_BaseMap (512x512)
[Jiangyu]   Registered texture asset: local_forces_basic_soldier_BaseMap (512x512)
[Jiangyu] Resolved 1 loadable mod(s), skipped 0 blocked mod(s), loaded 1 bundle(s).
...
[Jiangyu] Detected matching runtime replacement targets — applying replacements...
[Jiangyu] Applied 0 visual replacement(s), 0 sprite replacement(s), and 1 audio replacement(s).
...
[Jiangyu] Scene loaded: Tactical (4)
[Jiangyu] Detected matching runtime replacement targets — applying replacements...
[Jiangyu] Applied 48 visual replacement(s), 0 sprite replacement(s), and 0 audio replacement(s).
```

In-game:

- **All three BaseMap textures swapped visually.** Every soldier variant rendered red. Matches the "Applied 48 visual replacement(s)" line.
- **`button_click_01` audio**: loader reports 1 `AudioSource.clip` swap, but UI button clicks still play the stock click sound. The replaced `AudioSource` is not the surface through which button clicks are routed.
- **`icon_hitpoints` sprite**: loader reports 0 applications. HP icon in-game is unchanged.

## Interpretation

### Texture path

Validated end-to-end. The runtime scanner walks every `SkinnedMeshRenderer` and swaps `Texture2D` bindings on `sharedMaterials` via `MaterialReplacementService.GetOrCreateDirectTextureReplacementMaterials`. Because materials are identified by the `Texture2D` slot the replacement targets, every soldier instance picked up the swap without needing per-instance work.

### Sprite path — 0 applications

`ReplacementCoordinator.ApplyReplacements` finds `SpriteRenderer` and `UnityEngine.UI.Image` instances in the current scene and compares `sprite.name` against the registered replacement names. For `icon_hitpoints` this produced zero matches. Possible causes (not narrowed down in this pass):

1. **Timing.** The apply pass runs once per scene-load (`_autoApplied = true` after the first matching pass). The HP icon is rendered inside a unit-inspector panel or popup that may not be instantiated while the Tactical scene first loads. Later-instantiated `Image` components are never rescanned.
2. **Atlas routing.** `icon_hitpoints` appears in the index as **both** a `Texture2D` (pathId 2164) and a `Sprite` (pathId 4151). Nearby names include `universal_square_icons_144x144*` — each a `Texture2D` + a `Sprite` pair. That is the fingerprint of a packed atlas: one `Texture2D` containing many icons, with a corresponding `Sprite` asset per sub-rect. If the Image consumes a Sprite that Unity has routed through `SpriteAtlas.GetSprite("icon_hitpoints")` or a similar packed form, the live `Image.sprite.name` we see at scan time may not equal `"icon_hitpoints"`.
3. **Indirect consumer.** The icon may be held on a `ScriptableObject` field (e.g. an `EntityTemplate.HealthIcon` slot) that the UI binds at render time. The coordinator does not walk ScriptableObject Sprite fields.

Neither (1), (2), nor (3) has been disambiguated; all are plausible and overlapping.

### Audio path — 1 application, inaudible

Exactly one `AudioSource` in the scene had `clip.name == "button_click_01"`; its `clip` field was reassigned. UI button clicks in MENACE appear to not route through that `AudioSource`. The common Unity UI pattern is for a button to call a method on an audio manager singleton, which in turn invokes `audioSource.PlayOneShot(clipCachedOnManager)` — `PlayOneShot` ignores `audioSource.clip` and uses the argument clip. That cached clip lives on the manager's field or in a dictionary, not reachable via a scene-wide `AudioSource.clip` sweep.

## What this tells us

- **Bundle construction, mod discovery, and asset registration work for all three categories.** Compile → bundle → load → registry entries is validated end-to-end by today's log.
- **Runtime application only genuinely lands for textures.** The sprite and audio scanners cover only the simplest direct-reference shape of each type and miss the shapes MENACE actually uses for UI icons and button SFX.
- The existing `JIANGYU-CONTRACT` comments on the sprite and audio apply sites claim the paths are "valid for the current proven Sprite/AudioClip replacement path when the target … name is unique at runtime." That claim is too strong: the targets WERE unique (one `icon_hitpoints` Sprite, one `button_click_01` AudioClip) and the paths still did not observably land. Name uniqueness is necessary but not sufficient.

## Research questions opened

1. **Sprite atlas routing.** Determine how MENACE references and resolves UI sprites that have an atlas pair in the index. Likely requires a Harmony trace of `SpriteAtlas.GetSprite(string)` during UI rendering, and an investigation of whether MENACE uses `SpriteAtlas.GetSprite` directly, or binds `Image.sprite` to pre-resolved packed Sprite assets at scene/prefab bake time.
2. **Scene-load timing vs continuous application.** Whether the sprite miss is (also) caused by `_autoApplied` latching `true` before the UI surface is instantiated. A diagnostic pass that enumerates every `Image.sprite.name` observed across multiple frames, with and without the inspector panel open, would separate the timing cause from the atlas cause.
3. **Audio routing.** Locate the singleton/manager that owns UI sound playback in MENACE, determine how its clip registry is populated, and design a clip-swap strategy that targets that registry rather than scene-wide `AudioSource.clip` fields.
4. **ScriptableObject sprite/audio consumers.** Decide whether the loader should walk known template classes that hold `Sprite`/`AudioClip` references (e.g. `EntityTemplate.Badge`, `EntityTemplate.SoundWhileAlive`) and whether that counts as a narrow, defensible add-on or a heuristic we should decline.

## Immediate correctness action

Per project principles (correctness before convenience, explicit contracts over guessing), do not add continuous-rescan or speculative atlas handling without first investigating the actual MENACE routing. Correct the `JIANGYU-CONTRACT` comments in `ReplacementCoordinator.cs` (sprite and audio apply sites) to reflect that these are scoped direct-reference sweeps, not general `Sprite`/`AudioClip` replacement contracts. Promote the texture path to a verified finding independently of the unresolved sprite/audio questions.

## Environment

- Jiangyu commit at time of test: `d5101ade3` + uncommitted loader/CLI changes from this session (ILRepack merge, `--path-id` on `assets export model`, `ResolveGameObjectBacking`, compile heads-up).
- MENACE Unity version: `6000.0.63f1`.
- MelonLoader: `0.7.2`.
- Platform: Linux (Steam Proton). MelonLoader ran under Proton; log captured from `MelonLoader/Latest.log`.
