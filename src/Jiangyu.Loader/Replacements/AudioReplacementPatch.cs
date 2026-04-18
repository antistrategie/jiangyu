using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Playback-time audio replacement. Installs Harmony prefixes on AudioSource's
/// six play entry points. When the game calls one of them with (or on) a clip
/// whose name is in the replacement catalogue, the prefix substitutes the
/// modder's replacement clip before the original method proceeds.
/// </summary>
internal static class AudioReplacementPatch
{
    private static Dictionary<string, AudioClip> _replacementClips;
    private static MelonLogger.Instance _log;
    private static readonly HashSet<string> _mismatchWarningsEmitted = new(StringComparer.Ordinal);

    public static void Install(
        HarmonyLib.Harmony harmony,
        Dictionary<string, AudioClip> replacementClips,
        MelonLogger.Instance log)
    {
        _replacementClips = replacementClips;
        _log = log;

        PatchInstance(harmony, nameof(PlayOneShotPrefix), "PlayOneShot", typeof(AudioClip));
        PatchInstance(harmony, nameof(PlayOneShot2Prefix), "PlayOneShot", typeof(AudioClip), typeof(float));
        PatchInstance(harmony, nameof(PlayPrefix), "Play", Array.Empty<Type>());
        PatchInstance(harmony, nameof(PlayDelayedPrefix), "PlayDelayed", typeof(float));
        PatchInstance(harmony, nameof(PlayScheduledPrefix), "PlayScheduled", typeof(double));
        PatchStatic(harmony, nameof(PlayClipAtPointPrefix), "PlayClipAtPoint", typeof(AudioClip), typeof(Vector3));
    }

    private static void PatchInstance(HarmonyLib.Harmony harmony, string prefixName, string method, params Type[] args)
    {
        try
        {
            var target = AccessTools.Method(typeof(AudioSource), method, args);
            if (target == null)
            {
                _log?.Warning($"  Audio patch: AudioSource.{method} not found.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(AudioReplacementPatch), prefixName));
        }
        catch (Exception ex)
        {
            _log?.Error($"  Audio patch on AudioSource.{method} failed: {ex.Message}");
        }
    }

    private static void PatchStatic(HarmonyLib.Harmony harmony, string prefixName, string method, params Type[] args)
    {
        try
        {
            var target = AccessTools.Method(typeof(AudioSource), method, args);
            if (target == null)
            {
                _log?.Warning($"  Audio patch: static AudioSource.{method} not found.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(AudioReplacementPatch), prefixName));
        }
        catch (Exception ex)
        {
            _log?.Error($"  Audio patch on static AudioSource.{method} failed: {ex.Message}");
        }
    }

    private static AudioClip TryGetReplacement(string name)
    {
        if (_replacementClips == null || name == null) return null;
        _replacementClips.TryGetValue(name, out var replacement);
        return replacement;
    }

    private static void WarnIfMismatched(AudioClip original, AudioClip replacement)
    {
        if (original == null || replacement == null) return;
        if (original.frequency == replacement.frequency && original.channels == replacement.channels)
            return;

        var name = original.name ?? "<unnamed>";
        lock (_mismatchWarningsEmitted)
        {
            if (!_mismatchWarningsEmitted.Add(name))
                return;
        }

        _log?.Warning(
            $"  Audio replacement '{name}': frequency/channels differ from target " +
            $"(target {original.frequency}Hz {original.channels}ch, replacement {replacement.frequency}Hz {replacement.channels}ch). " +
            "Unity will resample at runtime, which can pitch-shift the sound. Author the replacement at the target's rate for best fidelity.");
    }

    private static void SubstituteOnSource(AudioSource source)
    {
        if (source == null) return;
        var current = source.clip;
        if (current == null) return;
        var replacement = TryGetReplacement(current.name);
        if (replacement == null) return;
        if (replacement.GetInstanceID() == current.GetInstanceID()) return;
        WarnIfMismatched(current, replacement);
        source.clip = replacement;
    }

    private static void SubstituteArgument(ref AudioClip clip)
    {
        if (clip == null) return;
        var replacement = TryGetReplacement(clip.name);
        if (replacement == null) return;
        if (replacement.GetInstanceID() == clip.GetInstanceID()) return;
        WarnIfMismatched(clip, replacement);
        clip = replacement;
    }

    public static void PlayOneShotPrefix(AudioSource __instance, ref AudioClip clip) => SubstituteArgument(ref clip);
    public static void PlayOneShot2Prefix(AudioSource __instance, ref AudioClip clip, float volumeScale) => SubstituteArgument(ref clip);
    public static void PlayPrefix(AudioSource __instance) => SubstituteOnSource(__instance);
    public static void PlayDelayedPrefix(AudioSource __instance, float delay) => SubstituteOnSource(__instance);
    public static void PlayScheduledPrefix(AudioSource __instance, double time) => SubstituteOnSource(__instance);
    public static void PlayClipAtPointPrefix(ref AudioClip clip, Vector3 position) => SubstituteArgument(ref clip);
}
