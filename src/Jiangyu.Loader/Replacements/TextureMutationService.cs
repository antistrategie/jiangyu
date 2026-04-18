using Il2CppInterop.Runtime;
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

    public TextureMutationService(Dictionary<string, Texture2D> replacementTextures)
    {
        _replacementTextures = replacementTextures;
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

            if (TryMutateInPlace(replacement, gameTexture, log))
            {
                _mutatedInstanceIds.Add(instanceId);
                mutated++;
                log.Msg($"  Mutated texture in place: {gameTexture.name} ({gameTexture.width}x{gameTexture.height}, {gameTexture.format})");
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

            var gameTexture = gameSprite.texture;
            if (gameTexture == null)
                continue;

            var instanceId = gameTexture.GetInstanceID();
            if (gameTexture.GetInstanceID() == replacement.GetInstanceID())
                continue;
            if (_mutatedInstanceIds.Contains(instanceId) || _failedInstanceIds.Contains(instanceId))
                continue;

            if (TryMutateInPlace(replacement, gameTexture, log))
            {
                _mutatedInstanceIds.Add(instanceId);
                mutated++;
                log.Msg($"  Mutated sprite-backing texture in place: {gameSprite.name} -> {gameTexture.name} ({gameTexture.width}x{gameTexture.height}, {gameTexture.format})");
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

    private static bool TryMutateInPlace(Texture2D source, Texture2D destination, MelonLogger.Instance log)
    {
        // Blit into an RGBA32 RenderTexture (handles resize + mipmap generation
        // via autoGenerateMips), ReadPixels into a readable RGBA32 staging
        // Texture2D, Compress() to the destination's compressed format via
        // Unity's managed compressor, then CopyTexture into the game texture.
        // Uses managed compression rather than Graphics.ConvertTexture because
        // ConvertTexture's compressed-destination path relies on GPU hardware
        // encoder support, which isn't reliable across consumer GPUs and Proton.
        RenderTexture rt = null;
        RenderTexture previousActive = null;
        Texture2D staging = null;
        try
        {
            rt = RenderTexture.GetTemporary(
                destination.width,
                destination.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            rt.useMipMap = destination.mipmapCount > 1;
            rt.autoGenerateMips = destination.mipmapCount > 1;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;

            Graphics.Blit(source, rt);

            previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            staging = new Texture2D(
                destination.width,
                destination.height,
                TextureFormat.RGBA32,
                mipChain: destination.mipmapCount > 1,
                linear: false);
            staging.ReadPixels(new Rect(0, 0, destination.width, destination.height), 0, 0, recalculateMipMaps: false);
            staging.Apply(updateMipmaps: destination.mipmapCount > 1, makeNoLongerReadable: false);
            RenderTexture.active = previousActive;
            previousActive = null;

            staging.Compress(highQuality: true);
            if (staging.format != destination.format)
            {
                log.Warning($"  Compressed staging format {staging.format} does not match destination {destination.format} for '{destination.name}'; skipping CopyTexture.");
                return false;
            }

            Graphics.CopyTexture(staging, destination);
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"  Texture mutation failed for '{destination.name}' (dst {destination.width}x{destination.height} {destination.format} mips={destination.mipmapCount}, src {source.width}x{source.height} {source.format}): {ex.Message}");
            return false;
        }
        finally
        {
            if (previousActive != null)
                RenderTexture.active = previousActive;
            if (staging != null)
                UnityEngine.Object.Destroy(staging);
            if (rt != null)
                RenderTexture.ReleaseTemporary(rt);
        }
    }
}
