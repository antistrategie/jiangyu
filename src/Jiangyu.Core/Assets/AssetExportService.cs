using AssetRipper.Assets;
using AssetRipper.Export.Modules.Audio;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Import.Configuration;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;
using StbImageWriteSharp;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Loads an indexed game asset by (collection, pathId), decodes it to a
/// modder-facing format, and writes the result to disk. Each exporter
/// dispatches by class name:
/// <list type="bullet">
///   <item><c>Texture2D</c> → PNG via <see cref="TextureConverter"/></item>
///   <item><c>Sprite</c> → cropped PNG via <see cref="SpriteConverter"/> (or the
///     backing texture's region when the sprite is atlas-packed)</item>
///   <item><c>AudioClip</c> → decoder-chosen format (<c>.ogg</c>/<c>.wav</c>/
///     module formats) via <see cref="AudioClipDecoder"/></item>
///   <item><c>Atlas</c> → PNG of the backing texture with coloured sprite-
///     region outlines plus a companion <c>.txt</c> legend</item>
/// </list>
///
/// <para>Atlas export reads the indexed Sprite entries (via
/// <see cref="AssetIndex"/>) to discover every sprite region inside the
/// requested atlas Texture2D. Other exporters don't need the index.</para>
/// </summary>
public sealed class AssetExportService(string gameDataPath, AssetIndex? index, IProgressSink progress, ILogSink log)
{
    private readonly string _gameDataPath = gameDataPath;
    private readonly AssetIndex? _index = index;
    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    public bool ExportTexture(string assetName, string outputFilePath, string collection, long pathId)
    {
        return ExportAssetFromIndexed<ITexture2D>(assetName, collection, pathId, "Texture2D", (texture) =>
        {
            if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Texture2D '{texture.Name}'.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
            using var stream = File.Create(outputFilePath);
            bitmap.SaveAsPng(stream);
            _log.Info($"  -> {outputFilePath} ({bitmap.Width}x{bitmap.Height})");
            return true;
        });
    }

    /// <summary>
    /// Loads the indexed Texture2D asset and decodes it to RGBA32 pixel data.
    /// Used by compile-time atlas compositing to obtain the original atlas image.
    /// </summary>
    public (int Width, int Height, byte[] Rgba)? LoadTexture2dRgba(string assetName, string collection, long pathId)
    {
        (int Width, int Height, byte[] Rgba)? result = null;
        ExportAssetFromIndexed<ITexture2D>(assetName, collection, pathId, "Texture2D", (texture) =>
        {
            if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Texture2D '{texture.Name}'.");
                return false;
            }
            result = (bitmap.Width, bitmap.Height, bitmap.ToRgba32());
            return true;
        });
        return result;
    }

    public bool ExportSprite(string assetName, string outputFilePath, string collection, long pathId)
    {
        return ExportAssetFromIndexed<ISprite>(assetName, collection, pathId, "Sprite", (sprite) =>
        {
            if (!SpriteConverter.TryConvertToBitmap(sprite, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Sprite '{sprite.Name}'.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
            using var stream = File.Create(outputFilePath);
            bitmap.SaveAsPng(stream);
            _log.Info($"  -> {outputFilePath} ({bitmap.Width}x{bitmap.Height})");
            return true;
        });
    }

    public string? ExportAudio(string assetName, string outputDirectory, string collection, long pathId)
    {
        string? writtenPath = null;
        var success = ExportAssetFromIndexed<IAudioClip>(assetName, collection, pathId, "AudioClip", (audioClip) =>
        {
            if (!AudioClipDecoder.TryDecode(audioClip, out var decodedData, out var fileExtension, out var message))
            {
                _log.Error($"Failed to decode AudioClip '{audioClip.Name}': {message}");
                return false;
            }

            // AssetRipper's AudioClipDecoder returns extensions without a leading dot
            // (e.g. "ogg", "wav"); normalise so we produce "<name>.<ext>" rather than
            // "<name><ext>" which reads as "nameogg".
            if (!fileExtension.StartsWith('.'))
                fileExtension = "." + fileExtension;

            Directory.CreateDirectory(outputDirectory);
            var outputFilePath = Path.Combine(outputDirectory, $"{assetName}{fileExtension}");
            File.WriteAllBytes(outputFilePath, decodedData);
            _log.Info($"  -> {outputFilePath} ({decodedData.Length} bytes)");
            writtenPath = outputFilePath;
            return true;
        });
        return success ? writtenPath : null;
    }

    public bool ExportAtlas(string atlasName, string outputFilePath, string collection, long pathId)
    {
        return ExportAssetFromIndexed<ITexture2D>(atlasName, collection, pathId, "Texture2D", (texture) =>
        {
            if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Texture2D '{texture.Name}'.");
                return false;
            }

            var rgba = bitmap.ToRgba32();
            int width = bitmap.Width;
            int height = bitmap.Height;

            var sprites = _index?.Assets?
                .Where(a =>
                    string.Equals(a.ClassName, "Sprite", StringComparison.Ordinal) &&
                    a.Sprite?.BackingTexturePathId == pathId &&
                    string.Equals(a.Sprite?.BackingTextureCollection, collection, StringComparison.Ordinal) &&
                    a.Sprite.TextureRectWidth.HasValue &&
                    a.Sprite.TextureRectHeight.HasValue)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList()
                ?? [];

            if (sprites.Count == 0)
            {
                _log.Info("No sprites reference this texture; exporting plain atlas.");
            }

            // Distinct palette for outlines, cycling if there are more sprites than colours.
            ReadOnlySpan<(byte R, byte G, byte B)> palette =
            [
                (255,  50,  50),  // red
                ( 50, 200,  50),  // green
                ( 50, 100, 255),  // blue
                (255, 200,  50),  // yellow
                (255,  50, 200),  // magenta
                ( 50, 220, 220),  // cyan
                (255, 140,  50),  // orange
                (180,  50, 255),  // purple
            ];

            var legendLines = new List<string> { $"Atlas: {atlasName} ({width} x {height})", "" };

            for (int i = 0; i < sprites.Count; i++)
            {
                var sprite = sprites[i];
                var (cr, cg, cb) = palette[i % palette.Length];

                int rx = (int)Math.Round(sprite.Sprite?.TextureRectX ?? 0);
                int ry = (int)Math.Round(sprite.Sprite?.TextureRectY ?? 0);
                int rw = (int)Math.Round(sprite.Sprite?.TextureRectWidth ?? 0);
                int rh = (int)Math.Round(sprite.Sprite?.TextureRectHeight ?? 0);

                // Unity textureRect: bottom-left origin. RGBA buffer: top-left origin.
                int topLeftY = height - ry - rh;
                DrawRectOutline(rgba, width, height, rx, topLeftY, rw, rh, cr, cg, cb);

                legendLines.Add($"  [{i + 1}] {sprite.Name ?? "(unnamed)"}  rect=({rx}, {ry}, {rw}, {rh})  colour=#{cr:X2}{cg:X2}{cb:X2}");
            }

            if (sprites.Count > 0)
            {
                legendLines.Add("");
                legendLines.Add($"{sprites.Count} sprite(s) outlined.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

            using (var stream = File.Create(outputFilePath))
            {
                var writer = new ImageWriter();
                writer.WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
            }

            var legendPath = Path.ChangeExtension(outputFilePath, ".txt");
            File.WriteAllLines(legendPath, legendLines);

            _log.Info($"  -> {outputFilePath} ({width}x{height}, {sprites.Count} sprite outlines)");
            _log.Info($"  -> {legendPath} (legend)");
            return true;
        });
    }

    private bool ExportAssetFromIndexed<T>(
        string assetName,
        string collection,
        long pathId,
        string expectedTypeLabel,
        Func<T, bool> exporter)
        where T : class, IUnityObjectBase
    {
        _log.Info($"Loading game data from: {_gameDataPath}");

        using var session = new GameDataSession(_gameDataPath, _progress, scriptContentLevel: ScriptContentLevel.Level0);

        if (!session.HasAnyAssetCollections)
        {
            _log.Error("No asset collections found in game data.");
            return false;
        }

        IUnityObjectBase? found = null;
        foreach (var col in session.GameData.GameBundle.FetchAssetCollections())
        {
            if (col.Name != collection)
                continue;

            found = col.FirstOrDefault(a => a.PathID == pathId);
            break;
        }

        if (found is not T typed)
        {
            _log.Error(
                $"No {expectedTypeLabel} named '{assetName}' found in collection '{collection}' at pathId={pathId} " +
                $"(found={found?.GetType().Name ?? "null"}).");
            return false;
        }

        return exporter(typed);
    }

    /// <summary>
    /// Draws a 2px outlined rectangle into an RGBA buffer (top-left origin). The outer ring is
    /// drawn in black for contrast, the inner ring in the specified colour.
    /// </summary>
    private static void DrawRectOutline(byte[] rgba, int imgWidth, int imgHeight,
        int x, int y, int w, int h, byte r, byte g, byte b)
    {
        // Outer ring (black, 1px)
        DrawHollowRect(rgba, imgWidth, imgHeight, x, y, w, h, 0, 0, 0, 255);
        // Inner ring (colour, 1px inset)
        if (w > 2 && h > 2)
            DrawHollowRect(rgba, imgWidth, imgHeight, x + 1, y + 1, w - 2, h - 2, r, g, b, 255);
    }

    private static void DrawHollowRect(byte[] rgba, int imgWidth, int imgHeight,
        int x, int y, int w, int h, byte r, byte g, byte b, byte a)
    {
        for (int dx = 0; dx < w; dx++)
        {
            SetPixel(rgba, imgWidth, imgHeight, x + dx, y, r, g, b, a);
            SetPixel(rgba, imgWidth, imgHeight, x + dx, y + h - 1, r, g, b, a);
        }
        for (int dy = 0; dy < h; dy++)
        {
            SetPixel(rgba, imgWidth, imgHeight, x, y + dy, r, g, b, a);
            SetPixel(rgba, imgWidth, imgHeight, x + w - 1, y + dy, r, g, b, a);
        }
    }

    private static void SetPixel(byte[] rgba, int imgWidth, int imgHeight,
        int px, int py, byte r, byte g, byte b, byte a)
    {
        if (px < 0 || px >= imgWidth || py < 0 || py >= imgHeight) return;
        int offset = (py * imgWidth + px) * 4;
        rgba[offset] = r;
        rgba[offset + 1] = g;
        rgba[offset + 2] = b;
        rgba[offset + 3] = a;
    }
}
