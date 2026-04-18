# Verified — Convention-First AudioClip Replacement

`AudioClip` replacement is validated end-to-end. Replacement happens at playback time via Harmony prefixes on `AudioSource` play entry points, rather than by mutating audio sample data.

## Contract

- Modder drops a replacement audio file at `assets/replacements/audio/<target-name>--<pathId>.<ext>` in a mod project. Supported extensions: `.wav`, `.ogg`, `.mp3`.
- `<target-name>--<pathId>` identifies an `AudioClip` asset in the Jiangyu asset index.
- Jiangyu compiles the file into the mod's AssetBundle as an `AudioClip` asset named `<target-name>`.
- At runtime, the loader installs Harmony prefixes on `AudioSource`'s six play entry points (`Play`, `PlayOneShot(AudioClip)`, `PlayOneShot(AudioClip, float)`, `PlayDelayed`, `PlayScheduled`, and the static `PlayClipAtPoint`). Each prefix inspects the clip about to play — either the `AudioClip` argument or the `AudioSource.clip` field — and if its `.name` matches a registered replacement target, substitutes the modder's clip before the original method proceeds.

## Why substitution rather than sample mutation

The "mutate the game `AudioClip` in place" approach used for textures cannot be applied to audio on this stack. `AudioClip.GetData(float[], int)` and `AudioClip.SetData(float[], int)` both throw `ObjectCollectedException: Object was garbage collected in IL2CPP domain` in MelonLoader 0.7.2 + Il2CppInterop 1.5.1 on Unity 6, regardless of whether the clip was bundle-loaded or game-native. A direct-ICall probe confirmed Il2CppInterop does not surface the real native `AudioClip::GetData` function either — resolution yields a managed stub rather than the native method, so the usual wrapper pattern (used for `Il2CppAssetBundleManager` in `BundleLoader.cs`) is not viable.

Playback-time substitution sidesteps the broken primitive entirely. Every audio playback on this stack routes through one of the hooked entry points, so substituting the clip at prefix time reaches every consumer — audio manager singletons, cached `PlayOneShot(clip)` argument paths, and direct `AudioSource.clip` playback — without needing to read or write sample data.

## Surfaces covered

Every `AudioSource` playback method exposed by Unity:

- `AudioSource.Play()`
- `AudioSource.PlayOneShot(AudioClip)`
- `AudioSource.PlayOneShot(AudioClip, float)`
- `AudioSource.PlayDelayed(float)`
- `AudioSource.PlayScheduled(double)`
- `AudioSource.PlayClipAtPoint(AudioClip, Vector3)` (static)

If a game uses audio middleware (Wwise, FMOD) that bypasses Unity's `AudioSource`, that game is out of scope — those playback paths don't go through the hooked methods. MENACE uses Unity's built-in audio, verified via 618 `AudioSource` components and 19,227 `AudioClip` assets live per scene.

## Validation

Verified 2026-04-18 with `RedSoldierTest`'s `button_click_01` replacement (880 Hz sine wave, 0.25 s). Clicking main menu buttons played the sine wave in place of the stock click sound. In the probe spike, MENACE was observed to route 100% of its audio through `AudioSource.PlayDelayed(float)` — music, UI SFX, ambient wind, narration, footsteps, combat SFX — but Jiangyu hooks all six entry points for portability to other games.

## Compile-time validation

- Target resolution: the `<pathId>` must resolve to exactly one `AudioClip` entry in the asset index (`ResolveReplacementAudioTarget`).
- Runtime name uniqueness: ambiguous target names are rejected by `ValidateUniqueRuntimeAudioNames`. The loader matches by `clip.name` at runtime, so ambiguous names would silently replace the wrong clip.

Sample-rate and channel-count matching are **not** currently enforced at compile time. Unity resamples a replacement clip to the target's mixer settings automatically, but a mismatch can audibly pitch-shift the sound. Modders should match the target's frequency and channel count. Check the target's values via `jiangyu assets search <name> --type AudioClip` (target metadata is recorded in the asset index).

## Out of scope for this contract

- Audio middleware bypass (Wwise, FMOD, CRI).
- Sample-level audio processing (effects, filtering, real-time DSP). Replacement is a whole-clip substitution, not an in-place sample edit.
- `AudioClip`s created at runtime via `AudioClip.Create` with procedural callbacks. Not targeted.
- Compile-time sample-rate / channel-count validation — deferred; Unity handles mismatch at runtime via resampling.
