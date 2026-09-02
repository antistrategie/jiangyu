using Il2CppInterop.Runtime;
using Jiangyu.Loader.Logging;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// In-place Texture2D mutation. Sweeps loaded textures by name, renders the
/// modder's source into a staging buffer sized to the destination, compresses
/// to the destination's format, and Graphics.CopyTexture's the result into
/// the live game texture. Every consumer that holds a reference to the game
/// texture inherits the change because Unity's texture references are
/// identity-based.
///
/// See docs/research/verified/texture-replacement.md for the scoped contract.
/// </summary>
internal sealed class TextureMutationService
{
    private readonly Dictionary<string, Texture2D> _replacementTextures;
    private readonly HashSet<int> _mutatedInstanceIds = new();
    private readonly HashSet<int> _failedInstanceIds = new();
    private readonly HashSet<IntPtr> _spriteTextureCastBlocklist = new();
    // Registered names placed at least once in the current scene, the unit MayHaveUnresolvedTargets
    // counts in. Separate from the instance sets because a name can own several instances.
    private readonly HashSet<string> _resolvedNames = new(StringComparer.Ordinal);

    public TextureMutationService(Dictionary<string, Texture2D> replacementTextures)
    {
        _replacementTextures = replacementTextures;
    }

    /// <summary>
    /// A cheap, sweep-free "is any registered name still unplaced in this scene" gate for the
    /// screen-activation pass. Counted by NAME, not by instance: one name routinely resolves to
    /// several instances, because a bundle sprite registers its backing texture under the sprite's
    /// name too, so counting instances would clear the gate while names were still unresolved.
    /// Scene-scoped, because a new scene loads new instances of the same names.
    /// </summary>
    public bool MayHaveUnresolvedTargets => _resolvedNames.Count < _replacementTextures.Count;

    /// <summary>
    /// Drop the scene's dedupe state. Instance ids and sprite pointers both belong to objects the
    /// scene owns, so carrying them into the next scene both leaks entries and risks a recycled id
    /// being mistaken for one already handled. Mirrors the SMR set the coordinator clears.
    /// </summary>
    public void OnSceneUnloaded()
    {
        _mutatedInstanceIds.Clear();
        _failedInstanceIds.Clear();
        _spriteTextureCastBlocklist.Clear();
        _resolvedNames.Clear();
    }

    public bool HasPendingTargets()
    {
        if (_replacementTextures.Count == 0)
            return false;

        var allTextures = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
        foreach (var obj in allTextures)
        {
            var gameTexture = obj?.TryCast<Texture2D>();
            if (!IsPendingMutationCandidate(gameTexture))
                continue;

            return true;
        }

        // Fallback: also consider game Sprite objects whose backing Texture2D
        // might not surface in FindObjectsOfTypeAll<Texture2D> (UI Toolkit
        // references sprites directly; the backing texture may be reachable
        // only via the Sprite's .texture property).
        var allSprites = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
        foreach (var obj in allSprites)
        {
            var gameSprite = obj?.TryCast<Sprite>();
            if (gameSprite == null || string.IsNullOrEmpty(gameSprite.name))
                continue;
            if (!_replacementTextures.ContainsKey(gameSprite.name))
                continue;
            var backing = gameSprite.texture;
            if (backing == null)
                continue;
            var instanceId = backing.GetInstanceID();
            if (_mutatedInstanceIds.Contains(instanceId) || _failedInstanceIds.Contains(instanceId))
                continue;
            return true;
        }

        return false;
    }

    public int ApplyPending(MelonLogger.Instance log)
    {
        if (_replacementTextures.Count == 0)
            return 0;

        var mutated = 0;
        var allTextures = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
        foreach (var obj in allTextures)
        {
            var gameTexture = obj?.TryCast<Texture2D>();
            if (!IsPendingMutationCandidate(gameTexture))
                continue;

            var replacement = _replacementTextures[gameTexture.name];
            var instanceId = gameTexture.GetInstanceID();

            if (TextureMutationHelpers.MutateInPlace(replacement, gameTexture, log))
            {
                _mutatedInstanceIds.Add(instanceId);
                _resolvedNames.Add(gameTexture.name);
                mutated++;
                LoaderDebug.Write(log, $"  Mutated texture in place: {gameTexture.name} ({gameTexture.width}x{gameTexture.height}, {gameTexture.format})");
            }
            else
            {
                _failedInstanceIds.Add(instanceId);
            }
        }

        // Second pass: walk Sprite objects and mutate their backing textures by
        // following the live Sprite -> Texture2D reference chain. Catches cases
        // where the backing Texture2D isn't surfaced by FindObjectsOfTypeAll<Texture2D>
        // directly (observed for UI Toolkit-consumed sprites whose backing texture
        // is reachable only via the sprite's .texture property).
        var allSprites = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
        foreach (var obj in allSprites)
        {
            var gameSprite = obj?.TryCast<Sprite>();
            if (gameSprite == null || string.IsNullOrEmpty(gameSprite.name))
                continue;

            if (!_replacementTextures.TryGetValue(gameSprite.name, out var replacement) || replacement == null)
                continue;

            // Some sprites in MENACE's runtime carry a backing-texture PPtr that
            // resolves to a non-Texture2D Object (observed: AudioClip). The
            // IL2CPP marshaller hard-fails the Sprite.texture cast in that case.
            // Skip and remember the bad sprite by pointer so the next apply tick
            // doesn't repeat the throw.
            var spritePtr = gameSprite.Pointer;
            if (_spriteTextureCastBlocklist.Contains(spritePtr))
                continue;

            Texture2D gameTexture;
            try
            {
                gameTexture = gameSprite.texture;
            }
            catch (Exception ex)
            {
                _spriteTextureCastBlocklist.Add(spritePtr);
                log.Warning($"  Skipping sprite '{gameSprite.name}': backing texture cast failed ({ex.GetType().Name}: {ex.Message}).");
                continue;
            }
            if (gameTexture == null)
                continue;

            var instanceId = gameTexture.GetInstanceID();
            if (gameTexture.GetInstanceID() == replacement.GetInstanceID())
                continue;
            if (_mutatedInstanceIds.Contains(instanceId) || _failedInstanceIds.Contains(instanceId))
                continue;

            if (TextureMutationHelpers.MutateInPlace(replacement, gameTexture, log))
            {
                _mutatedInstanceIds.Add(instanceId);
                _resolvedNames.Add(gameSprite.name);
                mutated++;
                LoaderDebug.Write(log, $"  Mutated sprite-backing texture in place: {gameSprite.name} -> {gameTexture.name} ({gameTexture.width}x{gameTexture.height}, {gameTexture.format})");
            }
            else
            {
                _failedInstanceIds.Add(instanceId);
            }
        }

        return mutated;
    }

    private bool IsPendingMutationCandidate(Texture2D gameTexture)
    {
        if (gameTexture == null)
            return false;

        if (string.IsNullOrEmpty(gameTexture.name))
            return false;

        if (!_replacementTextures.TryGetValue(gameTexture.name, out var replacement))
            return false;

        if (replacement == null)
            return false;

        // Skip the modder-supplied replacement texture itself (registered in the
        // catalogue under the target name, so it shows up in the scan). Mutating
        // a texture into itself would be a no-op at best and an ICall error at
        // worst.
        if (gameTexture.GetInstanceID() == replacement.GetInstanceID())
            return false;

        var instanceId = gameTexture.GetInstanceID();
        if (_mutatedInstanceIds.Contains(instanceId) || _failedInstanceIds.Contains(instanceId))
            return false;

        return true;
    }

}
