# Audio replacements

Replace any `AudioClip` in the game by name. When the game plays a clip whose name matches your replacement, Jiangyu substitutes your audio at playback time. Every gameplay path that triggers the clip (UI, voice lines, weapon SFX, ambient) picks up the substitution.

## Studio workflow

1. Open the [Asset Browser](../studio.md#asset-browser) pane.
2. Type the asset name in the search box. Filter the type to **Audio** if you want only audio.
3. Select the asset. The detail panel shows:

    - A `Replace` row with the path under `assets/replacements/` to save your replacement at, for example:

        ```text
        audio/mortar_incoming_whistle_04.wav
        ```

    - A `Frequency` row with the clip's sample rate, for example `44100 Hz`.
    - A `Channels` row with the channel count, for example `1`.
    - An inline player to audition the vanilla clip.

    Author your replacement at the **same frequency and channel count**. Mismatches are resampled by Unity at runtime, which pitch-shifts the result; see [Frequency and channels](#frequency-and-channels) below.

4. Click **Export** to pull the vanilla clip out as a starting point. Audio is exported in whatever format Unity embedded (typically `.ogg`; module formats like `.it`, `.xm`, `.mod` pass through unchanged).
5. Open the exported file in your audio editor, make your changes, save it under your project's `assets/replacements/` directory at the path Studio showed. For the example above, that's `assets/replacements/audio/mortar_incoming_whistle_04.wav`.
6. [Compile](../studio.md#compile).

## File layout

```text
assets/replacements/audio/<target-name>.<ext>
```

`<ext>` is `.wav`, `.ogg`, or `.mp3`. Other extensions are ignored.

The basename (without extension) is the **target name** and must match the `AudioClip`'s name in the asset index.

## Frequency and channels

Unity resamples mismatched audio at runtime, which pitch-shifts the sound. Match the vanilla clip's frequency and channel count when you save your replacement. Studio's detail panel shows both values; on the CLI, `assets search <name> --type AudioClip` includes them in the output.

If you author a replacement at 48 kHz stereo for a target that ships as 44.1 kHz mono, the sound will play back too fast and high. Mono targets need mono replacements; stereo targets need stereo replacements.

## Shared names

When the same name covers multiple `AudioClip` assets in the game, **all of them are substituted with your replacement**. The runtime matches by clip name, so targeting a single instance isn't possible.

Compile logs a warning enumerating every affected clip:

```text
warning: replacement 'taking_fire_10' will substitute 2 AudioClip instances:
  sharedassets0.assets/AudioClip/taking_fire_10--317
  sharedassets0.assets/AudioClip/taking_fire_10--9518
```

Treat the warning as a checklist. If any of the listed clips shouldn't change, your name is too ambiguous to replace safely.

## Compile-time errors

Compile refuses the build, with a clear message, when:

- The asset index isn't built or is unreadable. Build it from Studio's index status indicator, or run `jiangyu assets index`.
- The target name doesn't resolve to any `AudioClip` in the index.
- Two replacement files in the project resolve to the same target (for example, both `foo.wav` and `foo.ogg`).

## CLI alternative

```sh
jiangyu assets index
jiangyu assets search mortar_incoming_whistle_04 --type AudioClip
jiangyu assets export audio mortar_incoming_whistle_04
```

`assets search` shows each clip's frequency and channels alongside its suggested replacement path. Studio is the recommended surface for authoring; the CLI is intended for build pipelines and scripting.
