using AssetRipper.Assets;
using AssetRipper.Export.Modules.Audio;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using SharpGLTF.Scenes;
using StbImageWriteSharp;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Result of an on-demand asset preview generation.
/// </summary>
public sealed record AssetPreviewResult(byte[] Data, string MimeType, string FileExtension);

/// <summary>
/// On-demand preview generation for indexed game assets: thumbnail PNG for
/// textures and sprites, decoded audio bytes for AudioClips, raw GLB for
/// prefabs and meshes. Results are cached to disk under
/// <c>&lt;cache&gt;/previews/</c> so the second request for the same asset
/// skips decode.
/// </summary>
public sealed class AssetPreviewService(string cachePath, ILogSink log)
{
    private const string PreviewDirName = "previews";
    private const int ThumbnailMaxDimension = 256;

    private readonly string _cachePath = cachePath;
    private readonly ILogSink _log = log;

    public AssetPreviewResult? GeneratePreview(GameData gameData, string collection, long pathId, string className)
    {
        // Disk cache first; the second hit on the same asset skips decode.
        var cached = FindCachedPreview(collection, pathId);
        if (cached is not null)
        {
            var mime = MimeTypeForExtension(Path.GetExtension(cached));
            return new AssetPreviewResult(File.ReadAllBytes(cached), mime, Path.GetExtension(cached));
        }

        IUnityObjectBase? found = FindAsset(gameData, collection, pathId);
        if (found is null)
        {
            _log.Warning($"Preview: asset not found in {collection} at pathId={pathId}");
            return null;
        }

        byte[] data;
        string ext;
        string mime2;

        switch (className)
        {
            case "Texture2D" when found is ITexture2D texture:
                if (!TextureConverter.TryConvertToBitmap(texture, out var texBitmap) || texBitmap.IsEmpty)
                    return null;
                data = GenerateThumbnailPng(texBitmap);
                ext = ".png";
                mime2 = "image/png";
                break;

            case "Sprite" when found is ISprite sprite:
                DirectBitmap? fullBitmap = null;
                if (SpriteConverter.TryConvertToBitmap(sprite, out var sprBitmap) && !sprBitmap.IsEmpty)
                    fullBitmap = sprBitmap;
                else
                {
                    var backingTexture = AssetPipelineService.ResolveSpriteBackingTexture(sprite);
                    if (backingTexture is not null &&
                        TextureConverter.TryConvertToBitmap(backingTexture, out var backingBmp) &&
                        !backingBmp.IsEmpty)
                        fullBitmap = backingBmp;
                }
                if (fullBitmap is null)
                {
                    _log.Warning($"Preview: failed to decode Sprite at {collection}/{pathId}");
                    return null;
                }

                // Crop to the sprite's TextureRect within the atlas.
                // TextureRect is in Unity coords (bottom-left origin); the bitmap
                // has been FlipY'd to top-left origin by TextureConverter.
                var texRect = sprite.RD.TextureRect;
                int rx = Math.Max(0, (int)texRect.X);
                int rw = Math.Min(fullBitmap.Width - rx, (int)Math.Ceiling(texRect.Width));
                int rh = Math.Min(fullBitmap.Height, (int)Math.Ceiling(texRect.Height));
                int ry = Math.Max(0, fullBitmap.Height - (int)texRect.Y - rh);

                if (rw > 0 && rh > 0 && (rw < fullBitmap.Width || rh < fullBitmap.Height))
                {
                    var cropped = fullBitmap.Crop(rx..(rx + rw), ry..(ry + rh));
                    data = GenerateThumbnailPng(cropped);
                }
                else
                {
                    data = GenerateThumbnailPng(fullBitmap);
                }
                ext = ".png";
                mime2 = "image/png";
                break;

            case "AudioClip" when found is IAudioClip audioClip:
                if (!AudioClipDecoder.TryDecode(audioClip, out var audioData, out var audioExt, out var decodeMsg))
                {
                    _log.Warning($"Preview: failed to decode AudioClip: {decodeMsg}");
                    return null;
                }
                ext = audioExt.StartsWith('.') ? audioExt : "." + audioExt;
                mime2 = ext switch
                {
                    ".ogg" => "audio/ogg",
                    ".wav" => "audio/wav",
                    _ => "application/octet-stream",
                };
                data = audioData;
                break;

            case "GameObject" or "PrefabHierarchyObject" or "Mesh":
                data = GenerateModelGlb(found);
                if (data.Length == 0) return null;
                ext = ".glb";
                mime2 = "model/gltf-binary";
                break;

            default:
                _log.Warning($"Preview: unsupported className={className} or type mismatch (found={found.GetType().Name})");
                return null;
        }

        var diskCachePath = BuildPreviewCachePath(collection, pathId, ext);
        Directory.CreateDirectory(Path.GetDirectoryName(diskCachePath)!);
        File.WriteAllBytes(diskCachePath, data);

        return new AssetPreviewResult(data, mime2, ext);
    }

    private static IUnityObjectBase? FindAsset(GameData gameData, string collection, long pathId)
    {
        foreach (var col in gameData.GameBundle.FetchAssetCollections())
        {
            if (col.Name != collection) continue;
            return col.FirstOrDefault(a => a.PathID == pathId);
        }
        return null;
    }

    private static byte[] GenerateThumbnailPng(DirectBitmap bitmap)
    {
        if (bitmap.Width <= ThumbnailMaxDimension && bitmap.Height <= ThumbnailMaxDimension)
        {
            using var ms = new MemoryStream();
            bitmap.SaveAsPng(ms);
            return ms.ToArray();
        }

        int srcW = bitmap.Width;
        int srcH = bitmap.Height;
        float scale = Math.Min((float)ThumbnailMaxDimension / srcW, (float)ThumbnailMaxDimension / srcH);
        int dstW = Math.Max(1, (int)(srcW * scale));
        int dstH = Math.Max(1, (int)(srcH * scale));

        byte[] rgba = bitmap.ToRgba32();
        byte[] scaled = BoxFilterDownscale(rgba, srcW, srcH, dstW, dstH);

        using var ms2 = new MemoryStream();
        var writer = new ImageWriter();
        writer.WritePng(scaled, dstW, dstH, ColorComponents.RedGreenBlueAlpha, ms2);
        return ms2.ToArray();
    }

    private byte[] GenerateModelGlb(IUnityObjectBase asset)
    {
        SceneBuilder? scene = null;

        if (asset is IGameObject go)
        {
            var root = go.GetRoot();
            var assets = root.FetchHierarchy().Cast<IUnityObjectBase>();
            scene = GlbLevelBuilder.Build(assets, false);
        }
        else if (asset is PrefabHierarchyObject pho)
        {
            scene = GlbLevelBuilder.Build(pho.Assets, false);
        }
        else if (asset is IMesh mesh)
        {
            scene = GlbMeshBuilder.Build(mesh);
        }

        if (scene is null) return [];

        using var ms = new MemoryStream();
        if (GlbWriter.TryWrite(scene, ms, out string? errorMessage))
            return ms.ToArray();

        _log.Warning($"Preview: GLB write failed: {errorMessage}");
        return [];
    }

    private string BuildPreviewCachePath(string collection, long pathId, string extension)
    {
        var safeCollection = AssetPipelineService.SanitizeAssetPathSegment(collection);
        return Path.Combine(_cachePath, PreviewDirName, $"{safeCollection}--{pathId}{extension}");
    }

    private string? FindCachedPreview(string collection, long pathId)
    {
        var safeCollection = AssetPipelineService.SanitizeAssetPathSegment(collection);
        var dir = Path.Combine(_cachePath, PreviewDirName);
        if (!Directory.Exists(dir)) return null;
        var pattern = $"{safeCollection}--{pathId}.*";
        var files = Directory.GetFiles(dir, pattern);
        return files.Length > 0 ? files[0] : null;
    }

    private static string MimeTypeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".glb" => "model/gltf-binary",
        _ => "application/octet-stream",
    };

    // Area-average (box filter) downscale of RGBA32 pixel data.
    private static byte[] BoxFilterDownscale(byte[] rgba, int srcW, int srcH, int dstW, int dstH)
    {
        byte[] result = new byte[dstW * dstH * 4];
        float xScale = (float)srcW / dstW;
        float yScale = (float)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            int srcY0 = (int)(dy * yScale);
            int srcY1 = Math.Min((int)((dy + 1) * yScale), srcH);
            if (srcY1 <= srcY0) srcY1 = srcY0 + 1;

            for (int dx = 0; dx < dstW; dx++)
            {
                int srcX0 = (int)(dx * xScale);
                int srcX1 = Math.Min((int)((dx + 1) * xScale), srcW);
                if (srcX1 <= srcX0) srcX1 = srcX0 + 1;

                int r = 0, g = 0, b = 0, a = 0, count = 0;
                for (int sy = srcY0; sy < srcY1; sy++)
                {
                    for (int sx = srcX0; sx < srcX1; sx++)
                    {
                        int si = (sy * srcW + sx) * 4;
                        r += rgba[si];
                        g += rgba[si + 1];
                        b += rgba[si + 2];
                        a += rgba[si + 3];
                        count++;
                    }
                }

                int di = (dy * dstW + dx) * 4;
                result[di] = (byte)(r / count);
                result[di + 1] = (byte)(g / count);
                result[di + 2] = (byte)(b / count);
                result[di + 3] = (byte)(a / count);
            }
        }

        return result;
    }
}
