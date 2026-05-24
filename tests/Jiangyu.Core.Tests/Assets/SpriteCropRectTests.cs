using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

/// <summary>
/// Pure unit tests for the rect math that decides whether a sprite export
/// should crop its backing texture and, if so, to what window. Exercises
/// the Unity-bottom-left → DirectBitmap-top-left translation plus the
/// clamp/no-op paths.
/// </summary>
public sealed class SpriteCropRectTests
{
    [Fact]
    public void Resolve_FullCoverage_ReturnsNullForNoOp()
    {
        // A sprite that occupies the entire backing texture (single-sprite
        // texture, no atlas) should pass through without copying.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 256, wholeHeight: 256,
            rectX: 0, rectY: 0, rectWidth: 256, rectHeight: 256);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_FloatingRectExceedingTexture_ReturnsNullForNoOp()
    {
        // Imported sprites sometimes carry slightly larger floats than the
        // backing texture (rounding noise). The no-op branch should still
        // catch these.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 64, wholeHeight: 64,
            rectX: 0, rectY: 0, rectWidth: 64.4f, rectHeight: 64.4f);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_BottomLeftCorner_TranslatesY()
    {
        // Sprite at Unity (0, 0) with size 16x16 in a 64x64 atlas. Unity's
        // bottom-left origin means the bitmap row 0 is at the TOP, so the
        // top-left-origin Y for this sprite is 64 - 0 - 16 = 48.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 64, wholeHeight: 64,
            rectX: 0, rectY: 0, rectWidth: 16, rectHeight: 16);

        Assert.NotNull(result);
        Assert.Equal((0, 48, 16, 16), result!.Value);
    }

    [Fact]
    public void Resolve_TopRightCorner_TranslatesY()
    {
        // Same atlas, sprite at Unity top-right corner: (48, 48) size 16x16.
        // Top-left-origin Y becomes 64 - 48 - 16 = 0.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 64, wholeHeight: 64,
            rectX: 48, rectY: 48, rectWidth: 16, rectHeight: 16);

        Assert.NotNull(result);
        Assert.Equal((48, 0, 16, 16), result!.Value);
    }

    [Fact]
    public void Resolve_OutOfBounds_ClampsToTextureRectangle()
    {
        // Authored rect that pokes past the right edge. The exported window
        // should clamp to the texture and stay valid.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 32, wholeHeight: 32,
            rectX: 28, rectY: 0, rectWidth: 16, rectHeight: 16);

        Assert.NotNull(result);
        // Clamped width: min(28+16, 32) - 28 = 4. Height: 32 - 0 - 16 = 16.
        Assert.Equal((28, 16, 4, 16), result!.Value);
    }

    [Fact]
    public void Resolve_DegenerateRect_ReturnsNull()
    {
        // Zero-area rect collapses to nothing — caller should pass through
        // the whole bitmap rather than crash on an empty crop.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 64, wholeHeight: 64,
            rectX: 100, rectY: 0, rectWidth: 16, rectHeight: 16);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_FractionalCoords_RoundToNearest()
    {
        // Unity rects can carry sub-pixel values. We round to nearest pixel
        // boundary; the sprite's intended pixel grid is integer-aligned.
        var result = AssetExportService.ResolveSpriteCropRect(
            wholeWidth: 64, wholeHeight: 64,
            rectX: 16.4f, rectY: 16.6f, rectWidth: 31.5f, rectHeight: 31.5f);

        // Rounded: rx=16, ry=17, rw=32, rh=32. topLeftY = 64-17-32 = 15.
        Assert.NotNull(result);
        Assert.Equal((16, 15, 32, 32), result!.Value);
    }
}
