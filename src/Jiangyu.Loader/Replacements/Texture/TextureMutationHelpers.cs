using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// In-place Texture2D mutation primitive. Blits the source into an sRGB ARGB32
/// RenderTexture sized to the destination, ReadPixels into a readable staging
/// Texture2D whose alpha-presence matches the destination (RGB24 for DXT1/BC1
/// no-alpha targets, RGBA32 for DXT5/BC3 with-alpha targets), Compress() to the
/// destination's format via Unity's managed compressor, then Graphics.CopyTexture
/// into the game texture. Managed compression rather than Graphics.ConvertTexture
/// because the latter's compressed-destination path depends on GPU hardware
/// encoders that aren't reliable across consumer GPUs and Proton.
/// </summary>
internal static class TextureMutationHelpers
{
    public static bool MutateInPlace(Texture2D source, Texture2D destination, MelonLogger.Instance log)
    {
        RenderTexture rt = null;
        RenderTexture previousActive = null;
        Texture2D staging = null;
        try
        {
            // Pin the destination. Every asset the loader itself brings in is pinned against
            // Resources.UnloadUnusedAssets (clones, patched template values, bundle assets,
            // meshes); the one object a mutation WRITES to was not. A destination whose pixels
            // stream from a .resS side file can be unloaded and re-read from disk, which
            // silently restores the vanilla image and undoes the mutation.
            try { destination.hideFlags |= HideFlags.DontUnloadUnusedAsset; }
            catch (Exception ex)
            {
                // Not fatal, but say so: unpinned, this texture can be re-read from its .resS side
                // file later and the vanilla image comes back with nothing to explain it.
                log.Warning($"  Could not pin '{destination.name}' against unload "
                    + $"({ex.GetType().Name}: {ex.Message}); the mutation may not survive a scene change.");
            }

            var stagingFormat = ChooseStagingFormat(destination.format);
            rt = RenderTexture.GetTemporary(
                destination.width,
                destination.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            // No mip settings on the RT: GetTemporary hands back an already-created texture (so
            // assigning them is a Unity error), Blit only writes level 0, and ReadPixels only reads
            // level 0. Staging's chain comes from Apply below.
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;

            Graphics.Blit(source, rt);

            previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            // mipCount taken from the destination rather than mipChain:true. Graphics.CopyTexture
            // needs the two chains to be the same length, and a game texture is often authored with
            // a partial chain, so asking for a full one guarantees the mismatch the copy then has
            // no way to resolve: ConvertTexture is unreliable into a compressed destination, and a
            // texture that fails both is blocklisted for good.
            staging = new Texture2D(
                destination.width,
                destination.height,
                stagingFormat,
                mipCount: Math.Max(destination.mipmapCount, 1),
                linear: false);
            staging.ReadPixels(new Rect(0, 0, destination.width, destination.height), 0, 0, recalculateMipMaps: false);
            staging.Apply(updateMipmaps: destination.mipmapCount > 1, makeNoLongerReadable: false);
            RenderTexture.active = previousActive;
            previousActive = null;

            staging.Compress(highQuality: true);
            if (CanCopy(staging, destination))
            {
                Graphics.CopyTexture(staging, destination);
                return true;
            }

            // Managed Compress only produces DXT1 (from RGB24) or DXT5 (from RGBA32).
            // Formats outside that set (BC7, ASTC, BC4/5/6H) fall through here. Try
            // Unity's GPU-side Graphics.ConvertTexture as a last-resort path — it's
            // format-conversion-only rather than new-encoding so it's more portable
            // than using ConvertTexture for the primary path.
            if (Graphics.ConvertTexture(staging, destination))
                return true;

            log.Warning(
                $"  Texture mutation format path failed for '{destination.name}': compress produced "
                + $"{staging.format} {staging.width}x{staging.height} mips={staging.mipmapCount}, destination is "
                + $"{destination.format} {destination.width}x{destination.height} mips={destination.mipmapCount}, "
                + "ConvertTexture returned false.");
            return false;
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

    // Graphics.CopyTexture is a straight memory copy and does not resample: the two textures must
    // agree on size, format and mip count. Staging is built from the destination's size and mip
    // count, so those hold by construction and only the format is genuinely in question, Compress
    // being the one step that can land somewhere other than intended. The mip count is compared
    // anyway, cheaply, so a future change to how staging is built cannot reintroduce a copy whose
    // preconditions do not hold.
    private static bool CanCopy(Texture2D source, Texture2D destination) =>
        source.format == destination.format
        && source.mipmapCount == destination.mipmapCount;

    private static TextureFormat ChooseStagingFormat(TextureFormat destinationFormat)
    {
        // Pick a staging format whose alpha-presence matches the destination so that
        // the subsequent Compress() auto-picks the matching compressed variant:
        // RGB24 -> DXT1 (BC1, no alpha); RGBA32 -> DXT5 (BC3, with alpha).
        return destinationFormat switch
        {
            TextureFormat.DXT1 => TextureFormat.RGB24,
            TextureFormat.DXT1Crunched => TextureFormat.RGB24,
            TextureFormat.BC4 => TextureFormat.RGB24,
            TextureFormat.BC6H => TextureFormat.RGB24,
            TextureFormat.RGB24 => TextureFormat.RGB24,
            TextureFormat.RGB565 => TextureFormat.RGB24,
            TextureFormat.R8 => TextureFormat.RGB24,
            _ => TextureFormat.RGBA32,
        };
    }
}
