using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;
using StbImageSharp;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Composites modder sprite replacement images into atlas textures at compile time.
/// For each atlas-backed sprite replacement, the modder's image is blitted into the
/// sprite's <c>textureRect</c> within a copy of the original atlas. The result is
/// emitted as a Texture2D replacement under the atlas's name; the runtime's existing
/// identity-based texture mutation handles the rest.
/// </summary>
internal static class AtlasCompositor
{
    /// <summary>
    /// A sprite replacement that targets an atlas-backed sprite.
    /// </summary>
    internal sealed class AtlasSpriteReplacement
    {
        public required string SpriteName { get; init; }
        public required string SourceFilePath { get; init; }
        public required float TextureRectX { get; init; }
        public required float TextureRectY { get; init; }
        public required float TextureRectWidth { get; init; }
        public required float TextureRectHeight { get; init; }
        public required int PackingRotation { get; init; }
    }

    /// <summary>
    /// An atlas group: all sprite replacements targeting the same backing Texture2D.
    /// </summary>
    internal sealed class AtlasGroup
    {
        public required string AtlasName { get; init; }
        public required long AtlasPathId { get; init; }
        public required string AtlasCollection { get; init; }
        public required List<AtlasSpriteReplacement> Replacements { get; init; }
    }

    /// <summary>
    /// Composites all atlas-backed sprite replacements into their respective atlases.
    /// Returns a list of <see cref="Glb.GlbMeshBundleCompiler.CompiledTexture"/> entries,
    /// one per atlas, ready to be emitted alongside direct texture replacements.
    /// </summary>
    /// <param name="groups">Atlas groups from classification.</param>
    /// <param name="directTextures">
    /// Existing texture replacements. If a modder provides both <c>textures/atlas-name.png</c>
    /// and <c>sprites/sprite-in-atlas.png</c>, the texture replacement is used as the base
    /// and removed from this list (absorbed into the composite).
    /// </param>
    /// <param name="loadAtlasBitmap">
    /// Callback to load the original atlas as RGBA32 pixels. Receives (atlasName, collection, pathId)
    /// and returns (width, height, rgbaData) or null if loading fails.
    /// </param>
    /// <param name="log">Log sink for info/warning messages.</param>
    internal static List<Glb.GlbMeshBundleCompiler.CompiledTexture> Composite(
        IReadOnlyList<AtlasGroup> groups,
        List<Glb.GlbMeshBundleCompiler.CompiledTexture> directTextures,
        Func<string, string, long, (int Width, int Height, byte[] Rgba)?> loadAtlasBitmap,
        ILogSink log)
    {
        var result = new List<Glb.GlbMeshBundleCompiler.CompiledTexture>();

        foreach (var group in groups)
        {
            // Check for an existing texture replacement to use as the base
            var existingTexture = directTextures.FirstOrDefault(t =>
                string.Equals(t.Name, group.AtlasName, StringComparison.Ordinal));

            byte[] atlasRgba;
            int atlasWidth;
            int atlasHeight;

            if (existingTexture is not null)
            {
                // Use the modder's texture replacement as the base; load it as RGBA
                var baseImage = LoadModderImage(existingTexture.Content)
                    ?? throw new InvalidOperationException(
                        $"Failed to decode texture replacement for atlas '{group.AtlasName}'. " +
                        "Atlas compositing requires a decodable PNG/JPG base. Re-export the texture replacement and try again.");

                // Load original atlas to get its dimensions for coordinate validation
                var originalAtlas = loadAtlasBitmap(group.AtlasName, group.AtlasCollection, group.AtlasPathId);
                if (originalAtlas is not null && (baseImage.Width != originalAtlas.Value.Width || baseImage.Height != originalAtlas.Value.Height))
                {
                    // Resample the texture replacement to match original atlas dimensions
                    log.Warning(
                        $"Texture replacement for atlas '{group.AtlasName}' is {baseImage.Width}\u00d7{baseImage.Height} " +
                        $"but the original atlas is {originalAtlas.Value.Width}\u00d7{originalAtlas.Value.Height}; " +
                        "resampling to match atlas dimensions so sprite textureRect coordinates remain valid.");
                    atlasRgba = ResampleRgba(baseImage.Rgba, baseImage.Width, baseImage.Height,
                        originalAtlas.Value.Width, originalAtlas.Value.Height);
                    atlasWidth = originalAtlas.Value.Width;
                    atlasHeight = originalAtlas.Value.Height;
                }
                else
                {
                    atlasRgba = baseImage.Rgba;
                    atlasWidth = baseImage.Width;
                    atlasHeight = baseImage.Height;
                }

                directTextures.Remove(existingTexture);
                log.Info($"  Using texture replacement as base for atlas '{group.AtlasName}'; " +
                         $"compositing {group.Replacements.Count} sprite replacement(s) on top.");
            }
            else
            {
                // Load the original atlas from game data
                var original = loadAtlasBitmap(group.AtlasName, group.AtlasCollection, group.AtlasPathId)
                    ?? throw new InvalidOperationException(
                        $"Failed to load original atlas '{group.AtlasName}' (pathId={group.AtlasPathId}) from game data. " +
                        $"Cannot composite {group.Replacements.Count} sprite replacement(s) targeting this atlas. " +
                        "Re-run 'jiangyu assets index' and verify the atlas exists in the index.");

                atlasRgba = original.Rgba;
                atlasWidth = original.Width;
                atlasHeight = original.Height;
            }

            // Composite each sprite replacement into the atlas
            foreach (var replacement in group.Replacements)
            {
                var modderImage = LoadModderImageFromFile(replacement.SourceFilePath)
                    ?? throw new InvalidOperationException(
                        $"Failed to decode sprite replacement '{replacement.SpriteName}' from '{replacement.SourceFilePath}'. " +
                        "Provide a valid PNG or JPG image at this path.");

                int rectW = (int)Math.Ceiling(replacement.TextureRectWidth);
                int rectH = (int)Math.Ceiling(replacement.TextureRectHeight);

                byte[] spriteRgba = modderImage.Rgba;
                int spriteW = modderImage.Width;
                int spriteH = modderImage.Height;

                // Handle packing rotation: rotate the modder image to match
                if (replacement.PackingRotation != 0)
                {
                    log.Warning($"  Sprite '{replacement.SpriteName}' rect in '{group.AtlasName}' uses packing rotation {replacement.PackingRotation}; rotating modder image to match.");
                    (spriteRgba, spriteW, spriteH) = ApplyPackingRotation(spriteRgba, spriteW, spriteH, replacement.PackingRotation);
                }

                // Resample if dimensions don't match
                if (spriteW != rectW || spriteH != rectH)
                {
                    log.Warning($"  Sprite '{replacement.SpriteName}' replacement is {spriteW}\u00d7{spriteH} but its textureRect in '{group.AtlasName}' is {rectW}\u00d7{rectH}; resampling to fit.");
                    spriteRgba = ResampleRgba(spriteRgba, spriteW, spriteH, rectW, rectH);
                    spriteW = rectW;
                    spriteH = rectH;
                }

                // Blit into the atlas. TextureRect is in Unity coordinates (bottom-left origin).
                // The RGBA buffer is stored top-left origin (row 0 = top of image).
                int rectX = Math.Max(0, (int)replacement.TextureRectX);
                int rectY = Math.Max(0, (int)replacement.TextureRectY);
                BlitRgba(atlasRgba, atlasWidth, atlasHeight, spriteRgba, spriteW, spriteH, rectX, rectY);
            }

            // Encode the composited atlas as PNG
            var pngBytes = EncodeRgbaToPng(atlasRgba, atlasWidth, atlasHeight);

            result.Add(new Glb.GlbMeshBundleCompiler.CompiledTexture
            {
                Name = group.AtlasName,
                Content = pngBytes,
                Linear = Glb.GlbMeshBundleCompiler.IsLinearTextureName(group.AtlasName),
            });

            log.Info($"  Composited {group.Replacements.Count} sprite replacement(s) into atlas '{group.AtlasName}'.");
        }

        return result;
    }

    /// <summary>
    /// Blits source RGBA data into the destination at the given Unity-coordinate rect.
    /// The destination buffer is in top-left-origin layout; Unity's textureRect origin
    /// is bottom-left, so we flip Y during the blit.
    /// </summary>
    internal static void BlitRgba(
        byte[] dest, int destWidth, int destHeight,
        byte[] src, int srcWidth, int srcHeight,
        int rectX, int rectY)
    {
        for (int sy = 0; sy < srcHeight; sy++)
        {
            // Unity textureRect Y=0 is the bottom row; in the top-left buffer,
            // the bottom row is at index (destHeight - 1).
            int destY = destHeight - 1 - (rectY + sy);
            if (destY < 0 || destY >= destHeight)
                continue;

            int srcRowOffset = sy * srcWidth * 4;
            int destRowOffset = destY * destWidth * 4;

            for (int sx = 0; sx < srcWidth; sx++)
            {
                int dx = rectX + sx;
                if (dx < 0 || dx >= destWidth)
                    continue;

                int srcIdx = srcRowOffset + sx * 4;
                int destIdx = destRowOffset + dx * 4;

                dest[destIdx] = src[srcIdx];
                dest[destIdx + 1] = src[srcIdx + 1];
                dest[destIdx + 2] = src[srcIdx + 2];
                dest[destIdx + 3] = src[srcIdx + 3];
            }
        }
    }

    /// <summary>
    /// Bilinear resample of RGBA data to the target dimensions.
    /// </summary>
    internal static byte[] ResampleRgba(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            float srcYf = dy * yRatio;
            int srcY0 = (int)srcYf;
            int srcY1 = Math.Min(srcY0 + 1, srcH - 1);
            float yFrac = srcYf - srcY0;

            for (int dx = 0; dx < dstW; dx++)
            {
                float srcXf = dx * xRatio;
                int srcX0 = (int)srcXf;
                int srcX1 = Math.Min(srcX0 + 1, srcW - 1);
                float xFrac = srcXf - srcX0;

                int i00 = (srcY0 * srcW + srcX0) * 4;
                int i10 = (srcY0 * srcW + srcX1) * 4;
                int i01 = (srcY1 * srcW + srcX0) * 4;
                int i11 = (srcY1 * srcW + srcX1) * 4;
                int di = (dy * dstW + dx) * 4;

                for (int c = 0; c < 4; c++)
                {
                    float v00 = src[i00 + c];
                    float v10 = src[i10 + c];
                    float v01 = src[i01 + c];
                    float v11 = src[i11 + c];

                    float top = v00 + (v10 - v00) * xFrac;
                    float bot = v01 + (v11 - v01) * xFrac;
                    float val = top + (bot - top) * yFrac;
                    dst[di + c] = (byte)Math.Clamp(val + 0.5f, 0, 255);
                }
            }
        }

        return dst;
    }

    /// <summary>
    /// Applies the SpritePackingRotation transform to an RGBA image buffer.
    /// The modder's image is in "upright" orientation; this rotates/flips it to match
    /// how the sprite is stored in the atlas.
    /// </summary>
    internal static (byte[] Rgba, int Width, int Height) ApplyPackingRotation(
        byte[] src, int width, int height, int packingRotation)
    {
        // SpritePackingRotation values (from SettingsRaw bits 2-5):
        // 0 = None, 1 = FlipHorizontal, 2 = FlipVertical, 3 = Rotate180, 4 = Rotate90
        return packingRotation switch
        {
            1 => FlipHorizontal(src, width, height),
            2 => FlipVertical(src, width, height),
            3 => Rotate180(src, width, height),
            4 => Rotate90(src, width, height),
            _ => (src, width, height),
        };
    }

    private static (byte[] Rgba, int Width, int Height) FlipHorizontal(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcIdx = (y * w + x) * 4;
                int dstIdx = (y * w + (w - 1 - x)) * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }
        return (dst, w, h);
    }

    private static (byte[] Rgba, int Width, int Height) FlipVertical(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w * 4;
            int dstRow = (h - 1 - y) * w * 4;
            Buffer.BlockCopy(src, srcRow, dst, dstRow, w * 4);
        }
        return (dst, w, h);
    }

    private static (byte[] Rgba, int Width, int Height) Rotate180(byte[] src, int w, int h)
    {
        var (flipped, fw, fh) = FlipHorizontal(src, w, h);
        return FlipVertical(flipped, fw, fh);
    }

    private static (byte[] Rgba, int Width, int Height) Rotate90(byte[] src, int w, int h)
    {
        // 90-degree clockwise rotation: output is h-wide, w-tall
        var dst = new byte[w * h * 4];
        int newW = h;
        int newH = w;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcIdx = (y * w + x) * 4;
                int dstX = h - 1 - y;
                int dstY = x;
                int dstIdx = (dstY * newW + dstX) * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }
        return (dst, newW, newH);
    }

    private static (int Width, int Height, byte[] Rgba)? LoadModderImage(byte[] content)
    {
        try
        {
            var image = ImageResult.FromMemory(content, ColorComponents.RedGreenBlueAlpha);
            return (image.Width, image.Height, image.Data);
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height, byte[] Rgba)? LoadModderImageFromFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return (image.Width, image.Height, image.Data);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodeRgbaToPng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }
}
