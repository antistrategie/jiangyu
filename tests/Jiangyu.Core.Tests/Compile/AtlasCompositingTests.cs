using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Compile;

public class AtlasCompositingTests : IDisposable
{
    private const int AtlasWidth = 128;
    private const int AtlasHeight = 128;
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>
    /// Two sprite replacements targeting the same atlas produce a single CompiledTexture
    /// under the atlas name, with both target regions painted and other regions unchanged.
    /// </summary>
    [Fact]
    public void Composite_TwoSpritesInSameAtlas_ProducesSingleTexture()
    {
        var atlasRgba = CreateSolidRgba(AtlasWidth, AtlasHeight, 0x00, 0x00, 0xFF, 0xFF); // blue atlas

        var groups = new List<AtlasCompositor.AtlasGroup>
        {
            new()
            {
                AtlasName = "test_atlas",
                AtlasPathId = 1000,
                AtlasCollection = "resources.assets",
                Replacements =
                [
                    CreateReplacement("sprite_a", 0, 0, 32, 32),
                    CreateReplacement("sprite_b", 64, 64, 32, 32),
                ],
            },
        };

        var directTextures = new List<GlbMeshBundleCompiler.CompiledTexture>();
        var log = new RecordingLog();

        var result = AtlasCompositor.Composite(
            groups,
            directTextures,
            (name, collection, pathId) => (AtlasWidth, AtlasHeight, (byte[])atlasRgba.Clone()),
            log);

        Assert.Single(result);
        Assert.Equal("test_atlas", result[0].Name);

        var infos = log.Infos;
        Assert.Contains(infos, m => m.Contains("Composited 2 sprite replacement(s)"));

        // Decode the output PNG and verify pixel placement
        var output = DecodePng(result[0].Content);
        Assert.Equal(AtlasWidth, output.Width);
        Assert.Equal(AtlasHeight, output.Height);

        // sprite_a at Unity rect (0,0,32,32) → top-left buffer row (128-0-32)=96..127
        // modder image is red (0xFF,0x00,0x00). Check centre of that region.
        AssertPixelColour(output.Rgba, AtlasWidth, 16, 128 - 16, 0xFF, 0x00, 0x00, "sprite_a region should be red");

        // sprite_b at Unity rect (64,64,32,32) → top-left buffer row (128-64-32)=32..63
        AssertPixelColour(output.Rgba, AtlasWidth, 80, 128 - 80, 0xFF, 0x00, 0x00, "sprite_b region should be red");

        // Untouched region (e.g. row 0, col 0 in top-left = Unity top-right corner) stays blue
        AssertPixelColour(output.Rgba, AtlasWidth, 0, 0, 0x00, 0x00, 0xFF, "untouched region should be blue");
    }

    /// <summary>
    /// When both a texture replacement and a sprite replacement target the same atlas,
    /// the texture replacement is used as the base, the sprite region is composited on top,
    /// and the texture replacement is removed from the direct list.
    /// </summary>
    [Fact]
    public void Composite_TextureAndSpriteOverlap_SpriteWinsItsRegion()
    {
        var atlasRgba = CreateSolidRgba(AtlasWidth, AtlasHeight, 0x00, 0x00, 0xFF, 0xFF);
        var baseTextureRgba = CreateSolidRgba(AtlasWidth, AtlasHeight, 0x00, 0xFF, 0x00, 0xFF); // green base

        var groups = new List<AtlasCompositor.AtlasGroup>
        {
            new()
            {
                AtlasName = "test_atlas",
                AtlasPathId = 1000,
                AtlasCollection = "resources.assets",
                Replacements =
                [
                    CreateReplacement("sprite_a", 0, 0, 32, 32),
                ],
            },
        };

        // Create a PNG from the green base so it can be decoded by the compositor
        var basePng = EncodeToPng(baseTextureRgba, AtlasWidth, AtlasHeight);
        var directTextures = new List<GlbMeshBundleCompiler.CompiledTexture>
        {
            new()
            {
                Name = "test_atlas",
                Content = basePng,
                Linear = false,
            },
        };
        var log = new RecordingLog();

        var result = AtlasCompositor.Composite(
            groups,
            directTextures,
            (name, collection, pathId) => (AtlasWidth, AtlasHeight, (byte[])atlasRgba.Clone()),
            log);

        // The texture replacement should be absorbed (removed from directTextures)
        Assert.Empty(directTextures);
        Assert.Single(result);
        Assert.Equal("test_atlas", result[0].Name);

        Assert.Contains(log.Infos, m => m.Contains("Using texture replacement as base"));

        // Decode and verify: the base is green, the sprite_a region (0,0,32,32) should be red
        var output = DecodePng(result[0].Content);

        // sprite_a centre in Unity coords (16,16) → top-left buffer (16, 128-16)
        AssertPixelColour(output.Rgba, AtlasWidth, 16, 128 - 16, 0xFF, 0x00, 0x00, "sprite region should be red (wins over green base)");

        // Non-sprite region should be green (from the texture replacement base)
        // Check (120, 120) in Unity coords → top-left (120, 128-120)=row 8
        AssertPixelColour(output.Rgba, AtlasWidth, 120, 8, 0x00, 0xFF, 0x00, "non-sprite region should be green (texture base)");
    }

    /// <summary>
    /// When the modder image dimensions don't match the textureRect, it gets resampled.
    /// </summary>
    [Fact]
    public void Composite_MismatchedDimensions_Resamples()
    {
        var atlasRgba = CreateSolidRgba(AtlasWidth, AtlasHeight, 0x00, 0x00, 0x00, 0xFF);

        var groups = new List<AtlasCompositor.AtlasGroup>
        {
            new()
            {
                AtlasName = "test_atlas",
                AtlasPathId = 1000,
                AtlasCollection = "resources.assets",
                Replacements =
                [
                    // textureRect is 32x32 but the modder image will be 64x64
                    CreateReplacement("sprite_a", 0, 0, 32, 32, modderWidth: 64, modderHeight: 64),
                ],
            },
        };

        var directTextures = new List<GlbMeshBundleCompiler.CompiledTexture>();
        var log = new RecordingLog();

        var result = AtlasCompositor.Composite(
            groups,
            directTextures,
            (name, collection, pathId) => (AtlasWidth, AtlasHeight, (byte[])atlasRgba.Clone()),
            log);

        Assert.Single(result);
        Assert.Contains(log.Warnings, m => m.Contains("resampling to fit"));
    }

    /// <summary>
    /// A rotated sprite rect triggers rotation of the modder image.
    /// </summary>
    [Fact]
    public void Composite_RotatedSpriteRect_RotatesModderImage()
    {
        var atlasRgba = CreateSolidRgba(AtlasWidth, AtlasHeight, 0x00, 0x00, 0x00, 0xFF);

        var groups = new List<AtlasCompositor.AtlasGroup>
        {
            new()
            {
                AtlasName = "test_atlas",
                AtlasPathId = 1000,
                AtlasCollection = "resources.assets",
                Replacements =
                [
                    CreateReplacement("sprite_a", 0, 0, 32, 16, packingRotation: 4), // Rotate90
                ],
            },
        };

        var directTextures = new List<GlbMeshBundleCompiler.CompiledTexture>();
        var log = new RecordingLog();

        var result = AtlasCompositor.Composite(
            groups,
            directTextures,
            (name, collection, pathId) => (AtlasWidth, AtlasHeight, (byte[])atlasRgba.Clone()),
            log);

        Assert.Single(result);
        Assert.Contains(log.Warnings, m => m.Contains("packing rotation"));
    }

    /// <summary>
    /// When the original atlas can't be loaded from game data and there is no texture
    /// replacement to use as a base, the compositor must fail the build rather than
    /// silently dropping the modder's sprite replacements.
    /// </summary>
    [Fact]
    public void Composite_MissingAtlas_Throws()
    {
        var groups = new List<AtlasCompositor.AtlasGroup>
        {
            new()
            {
                AtlasName = "missing_atlas",
                AtlasPathId = 1000,
                AtlasCollection = "resources.assets",
                Replacements = [CreateReplacement("sprite_a", 0, 0, 32, 32)],
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => AtlasCompositor.Composite(
            groups,
            [],
            (_, _, _) => null,
            new RecordingLog()));

        Assert.Contains("missing_atlas", ex.Message);
        Assert.Contains("Cannot composite", ex.Message);
    }

    /// <summary>
    /// A modder image that won't decode must throw, not be silently skipped.
    /// </summary>
    [Fact]
    public void Composite_UndecodableSpriteImage_Throws()
    {
        var atlasRgba = CreateSolidRgba(AtlasWidth, AtlasHeight, 0, 0, 0, 0xFF);
        var bogusFile = Path.Combine(Path.GetTempPath(), $"jiangyu_test_bogus_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(bogusFile, [0x00, 0x01, 0x02, 0x03]);
        try
        {
            var groups = new List<AtlasCompositor.AtlasGroup>
            {
                new()
                {
                    AtlasName = "test_atlas",
                    AtlasPathId = 1000,
                    AtlasCollection = "resources.assets",
                    Replacements =
                    [
                        new AtlasCompositor.AtlasSpriteReplacement
                        {
                            SpriteName = "sprite_a",
                            SourceFilePath = bogusFile,
                            TextureRectX = 0,
                            TextureRectY = 0,
                            TextureRectWidth = 32,
                            TextureRectHeight = 32,
                            PackingRotation = 0,
                        },
                    ],
                },
            };

            var ex = Assert.Throws<InvalidOperationException>(() => AtlasCompositor.Composite(
                groups,
                [],
                (_, _, _) => (AtlasWidth, AtlasHeight, (byte[])atlasRgba.Clone()),
                new RecordingLog()));

            Assert.Contains("sprite_a", ex.Message);
            Assert.Contains("decode", ex.Message);
        }
        finally
        {
            File.Delete(bogusFile);
        }
    }

    /// <summary>
    /// Verifies that the blit correctly paints the target region and leaves
    /// other regions untouched.
    /// </summary>
    [Fact]
    public void BlitRgba_PaintsOnlyTargetRegion()
    {
        int w = 8, h = 8;
        var dest = CreateSolidRgba(w, h, 0x00, 0x00, 0xFF, 0xFF); // all blue

        // 2x2 red patch
        var src = CreateSolidRgba(2, 2, 0xFF, 0x00, 0x00, 0xFF);

        // Blit at Unity coords (2, 1) — bottom-left origin
        AtlasCompositor.BlitRgba(dest, w, h, src, 2, 2, rectX: 2, rectY: 1);

        // In top-left buffer: Unity Y=1 row0 of src maps to dest row (8-1-1)=6, row1 maps to row 5
        // Check a painted pixel (dest row 6, col 2)
        int paintedIdx = (6 * w + 2) * 4;
        Assert.Equal(0xFF, dest[paintedIdx]);     // R
        Assert.Equal(0x00, dest[paintedIdx + 1]); // G
        Assert.Equal(0x00, dest[paintedIdx + 2]); // B

        // Check an unpainted pixel (dest row 0, col 0)
        int unpaintedIdx = 0;
        Assert.Equal(0x00, dest[unpaintedIdx]);     // R
        Assert.Equal(0x00, dest[unpaintedIdx + 1]); // G
        Assert.Equal(0xFF, dest[unpaintedIdx + 2]); // B
    }

    /// <summary>
    /// Verifies bilinear resampling produces correct output dimensions.
    /// </summary>
    [Fact]
    public void ResampleRgba_ProducesCorrectDimensions()
    {
        var src = CreateSolidRgba(64, 64, 0xFF, 0x00, 0x00, 0xFF);
        var result = AtlasCompositor.ResampleRgba(src, 64, 64, 32, 32);
        Assert.Equal(32 * 32 * 4, result.Length);
    }

    /// <summary>
    /// Verifies Rotate90 swaps width and height.
    /// </summary>
    [Fact]
    public void ApplyPackingRotation_Rotate90_SwapsDimensions()
    {
        var src = CreateSolidRgba(4, 2, 0xFF, 0x00, 0x00, 0xFF);
        var (rgba, w, h) = AtlasCompositor.ApplyPackingRotation(src, 4, 2, 4);
        Assert.Equal(2, w);
        Assert.Equal(4, h);
        Assert.Equal(2 * 4 * 4, rgba.Length);
    }

    /// <summary>
    /// Verifies FlipHorizontal (rotation=1) preserves dimensions.
    /// </summary>
    [Fact]
    public void ApplyPackingRotation_FlipHorizontal_PreservesDimensions()
    {
        var src = CreateSolidRgba(4, 4, 0xFF, 0x00, 0x00, 0xFF);
        var (rgba, w, h) = AtlasCompositor.ApplyPackingRotation(src, 4, 4, 1);
        Assert.Equal(4, w);
        Assert.Equal(4, h);
    }

    private AtlasCompositor.AtlasSpriteReplacement CreateReplacement(
        string name, float rx, float ry, float rw, float rh,
        int packingRotation = 0,
        int? modderWidth = null,
        int? modderHeight = null)
    {
        int mw = modderWidth ?? (int)rw;
        int mh = modderHeight ?? (int)rh;

        // Create a temp file with a red PNG for the modder image
        var tempFile = Path.Combine(Path.GetTempPath(), $"jiangyu_test_{name}_{Guid.NewGuid():N}.png");
        var rgbaData = CreateSolidRgba(mw, mh, 0xFF, 0x00, 0x00, 0xFF);
        File.WriteAllBytes(tempFile, EncodeToPng(rgbaData, mw, mh));
        _tempFiles.Add(tempFile);

        return new AtlasCompositor.AtlasSpriteReplacement
        {
            SpriteName = name,
            SourceFilePath = tempFile,
            TextureRectX = rx,
            TextureRectY = ry,
            TextureRectWidth = rw,
            TextureRectHeight = rh,
            PackingRotation = packingRotation,
        };
    }

    private static byte[] CreateSolidRgba(int width, int height, byte r, byte g, byte b, byte a)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = a;
        }
        return data;
    }

    private static byte[] EncodeToPng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }

    private static (int Width, int Height, byte[] Rgba) DecodePng(byte[] png)
    {
        var image = StbImageSharp.ImageResult.FromMemory(png, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return (image.Width, image.Height, image.Data);
    }

    private static void AssertPixelColour(byte[] rgba, int imgWidth, int x, int y, byte r, byte g, byte b, string message)
    {
        int offset = (y * imgWidth + x) * 4;
        Assert.True(rgba[offset] == r && rgba[offset + 1] == g && rgba[offset + 2] == b,
            $"{message}: expected ({r},{g},{b}) at ({x},{y}) but got ({rgba[offset]},{rgba[offset + 1]},{rgba[offset + 2]})");
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public void Info(string message) => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
    }
}
