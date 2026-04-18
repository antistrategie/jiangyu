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

## Follow-up — 2026-04-18 afternoon, inspector-driven findings

Running the inspector across the `Splash`, `Title`, and `Strategy` scenes with a fresh `RedSoldierTest` bundle produced two decisive datapoints that together reshape the research agenda.

### MENACE is on UI Toolkit, not UGUI

Diagnostic counts from every dump:

```
Canvases:          0
Graphics:          0
RawImages:         0
SpriteRenderers:   0
UI Images:         0
UiDocuments:       1–2
TotalGameObjects:  745–748
AudioSources:      617–618
```

`FindObjectsOfType` infrastructure is proven working (`GameObject` and `AudioSource` return real counts). MENACE simply has no UGUI components in memory. `UIDocument` > 0 across all three scenes confirms the UI tree is `UnityEngine.UIElements`-based.

Consequences for the current loader:

- `ReplacementCoordinator.ApplyReplacements` walks `SpriteRenderer` and `UnityEngine.UI.Image` (the UGUI class) for sprite replacement, and `AudioSource.clip` for audio. None of the UGUI types exist in MENACE at all, so the sprite sweep can never land.
- UI Toolkit consumes `Sprite` via `UnityEngine.UIElements.Image.sprite` (same class name, different namespace, different architecture) and via USS `background-image` / `StyleBackground` on `VisualElement.style`. These are managed objects hanging off `UIDocument.rootVisualElement`, not components on scene `GameObject`s, so a scene scan will never see them.

### The compile-side sprite naming bug is a genuine fix regardless

Pre-fix, the loader registered replacement sprites as `icon_hitpoints--4151.sprite` while MENACE's live sprite name is `icon_hitpoints`. The Unity staging template used `"{generatedDir}/{sprite.name}.sprite.asset"` and the compiler staging filename carried the alias (`target--pathId`). Fixed by:
- `MeshBundleBuilder.template`: asset path is now `{generatedDir}/{sprite.name}.asset` (matches textures).
- `CompilationService.DiscoverReplacementSpriteEntries`: `StagingName = "sprite_source__" + target.Name` (clean name, not alias).

After the fix, the loader log shows `Registered sprite asset: icon_hitpoints`. The bug was orthogonal to the UI Toolkit finding but both had to be resolved before sprite replacement could ever succeed.

### MenaceAssetPacker uses a completely different strategy

Reading through `../MenaceAssetPacker/src/Menace.ModpackLoader/AssetInjectionPatches.cs`, AssetPackers does **in-place asset mutation**, not consumer-reference swapping:

- **Texture replacement** (`ApplyBundleTextureReplacements`, line ~1046): walks `Resources.FindObjectsOfTypeAll(Texture2D)`, finds by `texture.name`, does `Graphics.CopyTexture(bundleTex, gameTex)` to copy pixels into the existing game texture object. Every material already bound to that `Texture2D` reference instantly picks up new pixels, regardless of how the material is consumed.
- **Audio replacement** (`ApplyBundleAudioReplacements`, line ~1098): walks `Resources.FindObjectsOfTypeAll(AudioClip)`, finds by name, does `CopyAudioClipData(source, target)` to copy sample data into the existing `AudioClip` object. Every cached reference to that clip — on an audio manager singleton, in a `PlayOneShot` cache, on an `AudioSource.clip` field — now plays the new audio. No audio-manager hunt required.
- **Sprite replacement** is implicit: sprites reference a backing `Texture2D`, so mutating the texture updates every sprite backed by it. Atlased sprites fall out naturally when their atlas texture is the replacement target, and per-sprite sub-rect replacement within a shared atlas is genuinely unsupported (and correctly so — there is no way to change one atlas entry without rebuilding the atlas).
- No Harmony patches. No UI-framework-specific walks. No scene scans. Mutate the asset object; the entire consumer graph inherits the change.

### Architectural implication for Jiangyu — hypothesis only, not a decided direction

The current loader-side replacement strategy ("find consumers, swap reference") is empirically insufficient for MENACE's UI, because consumers live outside scene-discoverable `GameObject`s. That is a proven fact from today's dumps.

It does **not** follow that in-place mutation is automatically the right replacement strategy. Before adopting it, Jiangyu needs to validate several concrete concerns that MenaceAssetPacker's implementation does not appear to address:

- **Format compatibility.** `Graphics.CopyTexture(src, dst)` requires dimensions, pixel format, and mipmap chain to match. Modder replacements enter as PNG → `Texture2D(RGBA32)` via `ImageConversion.LoadImage`, while game textures are typically compressed (DXT5/BC7/ASTC) and may be non-readable. Unclear whether `Graphics.CopyTexture` handles the mismatch, silently fails, or degrades visibly. Needs a controlled test.
- **Shared atlas corruption.** 2090 of MENACE's UI sprites share a single backing texture (`sactx-0-8192x16384-DXT5|BC3-ui_sprite_atlas-614d9c5f`). Mutating that texture would change ~2089 unrelated UI elements. A naive in-place mutation path would silently corrupt the UI when a modder replaces one icon. Any in-place strategy must detect atlas-backed targets and fail the compile, not the runtime.
- **Audio sample-rate / channel-count compatibility.** `AudioClip.SetData` assumes matching rate and channels. A 44.1kHz mono modder clip replacing a 48kHz stereo game clip needs resampling or rejection.
- **Consumer-aliasing assumption.** In-place mutation works iff every consumer holds a reference to the same Unity `Texture2D`/`AudioClip` object. If MENACE makes defensive runtime copies anywhere (e.g. a UI Toolkit `StyleBackground` that clones the sprite into a platform-specific buffer), mutation would not propagate and the failure would look identical to the current consumer-walk failure.
- **Jiangyu's compile-time strictness.** Today Jiangyu rejects ambiguous runtime targets at compile time (`ValidateUniqueRuntimeSpriteNames`, `ValidateUniqueRuntimeMeshNames`, etc.). That is a principle-7 strength — the modder gets a clear error instead of silent mass corruption. An in-place mutation strategy needs a compile-time check of equal or greater strictness (e.g. "refuse to mutate a texture that backs more than one Sprite", "refuse to mutate a clip when the modder's sample rate would require resampling"). Importing AssetPackers' approach without Jiangyu-strength validation would be a regression.
- **Persistence / statefulness.** Once a texture is mutated, the whole Unity session sees the new pixels. Load-order changes, `Resources.UnloadUnusedAssets`, or scene-reload behaviour against that are not characterised.

Per `docs/PRINCIPLES.md` §2 and §7, the correct next move is a **scoped validation experiment**, not a strategy pivot. One controlled in-place `Graphics.CopyTexture` attempt on a single known-uniquely-backed texture, with diagnostic logging, will tell us whether mutation propagates to UI consumers at all. If yes, then the detailed feasibility questions above become the research agenda. If no, the whole hypothesis is retired and we look elsewhere (Harmony-patch asset loaders, managed-side registries, UI Toolkit `VisualElement` walks).

The current `JIANGYU-CONTRACT:` comments on the sprite and audio apply sites in `ReplacementCoordinator.cs` record that those sweeps are known to be empirically ineffective in MENACE. They do not commit Jiangyu to a replacement strategy — that decision is gated on the validation experiment above.

## Follow-up — 2026-04-18 evening, in-place mutation experiment results

`InPlaceMutationExperiment` (added to `Jiangyu.Loader.Diagnostics` as a flag-gated investigation tool) was run against `RedSoldierTest`'s three character-texture replacements and its `button_click_01` audio replacement. Concrete findings:

### Textures — in-place mutation validated

All three game textures (`local_forces_basic_soldier*_BaseMap`, DXT1-compressed at 1024×1024 or 2048×2048 with full mipmap chains) were successfully mutated via a two-step `Graphics.ConvertTexture` + `Graphics.CopyTexture` path. The modder's 512×512 ARGB32 PNG was first converted to an intermediate `Texture2D` matching the game texture's format/dimensions/mip count, then that intermediate was `CopyTexture`'d into the game texture in place. Report at `<UserData>/jiangyu-experiment-copytexture/<timestamp>-Tactical.json`, field `Succeeded: true` for all three targets.

The soldier rendered cream rather than red, which is itself a valuable finding:

- **Consumer-walk (previous path) produced pure red.** `MaterialReplacementService.GetOrCreateDirectTextureReplacementMaterials` allocates new `Material` instances and carries only the texture we swap — the game's original `MaskMap`/`NormalMap`/`EffectMap` slots on those materials are not preserved, so the shader runs without them and renders raw texture.
- **In-place mutation preserved the full shader pipeline** — the same material, the same `MaskMap`/`NormalMap`/`EffectMap`, just with the BaseMap's pixels rewritten. The cream colour is red × shader-configured detail effects.

In-place mutation is therefore not merely equivalent to consumer-walk for textures — it is **more correct**. The consumer-walk path has been silently stripping material properties that the game's shader relies on; solid-colour test inputs hid the bug.

### Audio — blocked by Il2CppInterop `GetData`/`SetData` marshalling bug

An analogous experiment for `AudioClip` in-place mutation failed at the `GetData` call with `Object was garbage collected in IL2CPP domain`. The diagnostic pass confirmed the failure is not specific to our bundle-loaded replacement clip: probing both the game clip (`button_click_01`, pathId 220258) and our replacement clip (instance 289298) with a tiny 64-sample buffer triggered the same exception on both. The bug is in Il2CppInterop's `float[]` marshalling for this MelonLoader 0.7.2 + Unity 6 + Il2CppInterop combination. It is not MENACE-specific and not bundle-specific.

Report: `<UserData>/jiangyu-experiment-copytexture/<timestamp>-Tactical.json`, `AudioTargets[].Candidates[].Note` carries the per-probe outcome. Confirmed across `Splash`, `Title`, and `Tactical`.

This is the same bug class the codebase already works around in `BundleLoader.cs` (two Unity 6 IL2CPP ICall issues documented in the TODO comment at the top of `JiangyuMod.cs`). The fix pattern is a manual ICall wrapper that bypasses Il2CppInterop's broken array marshalling and calls Unity's underlying native method directly.

### Atlas concerns confirmed, unaddressed

`icon_hitpoints` shares its backing texture (`sactx-0-8192x16384-DXT5|BC3-ui_sprite_atlas-614d9c5f`) with 2089 other UI sprites. The experiment did not attempt to mutate that atlas — it would silently corrupt every UI element using it. Production-grade sprite support must include compile-time atlas detection (refuse any sprite target whose backing `Texture2D` backs more than one `Sprite` in the asset index) before an in-place sprite strategy can be adopted.

### Net decision

In-place mutation is the validated strategy for `Texture2D` replacement. The architectural concerns captured in the earlier section of this note are mostly resolved by today's experiment:

- Format compatibility → handled at runtime via `Graphics.ConvertTexture`, one GPU conversion per texture per session.
- Consumer-aliasing assumption → validated empirically.
- Shared-atlas corruption → real risk, but scoped to sprites; addressable at compile time via atlas detection.
- Audio sample-rate / channel compatibility → pre-empted by the more fundamental IL2CPP marshalling bug which must be solved first.
- Jiangyu's compile-time strictness → preserved; atlas detection is a natural extension of the existing uniqueness checks.

Textures and sprites can now move to production implementation. Audio needs a manual ICall wrapper before it can even be tested against MENACE's consumers.

## Tooling

Follow-up research here is supported by `Jiangyu.Loader.Diagnostics.RuntimeInspector`, added alongside this investigation.

- Enable: `touch <Menace>/UserData/jiangyu-inspect.flag` (any contents; empty is fine).
- Disable: remove the flag file.
- While enabled, each scene-load writes a JSON dump to `<Menace>/UserData/jiangyu-inspect/<timestamp>-<scene>.json` containing:
  - every live `SpriteRenderer` and `UnityEngine.UI.Image`, with `sprite.name` and `GameObject` path
  - every loaded `Sprite` asset from `Resources.FindObjectsOfTypeAll` (captures atlas-packed sub-sprites that scene-scoped scans miss)
  - every live `AudioSource`, with `clip.name` and `GameObject` path
  - every loaded `AudioClip` asset

To answer the open questions here:

1. **Is `icon_hitpoints` present as a loaded `Sprite` asset at all?** Grep `"SpriteName": "icon_hitpoints"` in the dump. If yes, atlas/reference hypothesis is supported (the asset exists, but no `Image.sprite` points at it by that name). If no, it's not even loaded and something else is going on.
2. **Is the miss timing?** Take a dump with the unit-inspector panel closed, then open the panel and reload the scene, then diff the two dumps' `UiImages` lists. Any `Image.sprite.name == "icon_hitpoints"` that appears in the second dump but not the first means the miss is timing — the coordinator just needs a later pass.
3. **Where does MENACE cache the click audio clip?** Grep the dump for `"ClipName": "button_click_01"`. Count occurrences in `AudioSources` vs `AudioClipAssets`. If one `AudioSource` has it but there are many `AudioClipAssets` instances with the same name, the clip exists multiple places in memory (suggesting a manager holds its own reference), and `AudioSource.clip` swapping alone won't win.

Extend the inspector rather than writing one-off scripts as new identity surfaces become relevant (e.g. `SpriteAtlas.GetSprites` enumeration, ScriptableObject `Sprite`/`AudioClip` fields).

## Environment

- Jiangyu commit at time of test: `d5101ade3` + uncommitted loader/CLI changes from this session (ILRepack merge, `--path-id` on `assets export model`, `ResolveGameObjectBacking`, compile heads-up).
- MENACE Unity version: `6000.0.63f1`.
- MelonLoader: `0.7.2`.
- Platform: Linux (Steam Proton). MelonLoader ran under Proton; log captured from `MelonLoader/Latest.log`.
