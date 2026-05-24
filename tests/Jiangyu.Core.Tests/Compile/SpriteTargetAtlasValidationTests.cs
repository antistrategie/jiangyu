using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Compile;

public class SpriteTargetAtlasValidationTests
{
    [Fact]
    public void ResolveAndClassifySpriteTarget_UniqueBackingTexture_ResolvesAsNonAtlas()
    {
        var index = new AssetIndex
        {
            Assets =
            [
                new AssetEntry
                {
                    Name = "menace_logo_main_menue",
                    ClassName = "Sprite",
                    PathId = 4151,
                    Collection = "resources.assets",
                    CanonicalPath = "resources.assets/Sprite/menace_logo_main_menue--4151",
                    Sprite = new AssetSpriteMetadata
                    {
                        BackingTexturePathId = 2164,
                        BackingTextureCollection = "resources.assets",
                        BackingTextureName = "menace_logo_main_menue_tex",
                        TextureRectX = 0,
                        TextureRectY = 0,
                        TextureRectWidth = 256,
                        TextureRectHeight = 128,
                        PackingRotation = 0,
                    },
                },
                new AssetEntry
                {
                    Name = "menace_logo_main_menue_tex",
                    ClassName = "Texture2D",
                    PathId = 2164,
                    Collection = "resources.assets",
                },
            ],
        };

        var (target, classification) = CompilationService.ResolveAndClassifySpriteTarget(
            index, "menace_logo_main_menue", "menace_logo_main_menue", 4151, NullLogSink.Instance);

        Assert.Equal("menace_logo_main_menue", target.Name);
        Assert.Equal(4151, target.PathId);
        Assert.False(classification.IsAtlasBacked);
    }

    [Fact]
    public void ResolveAndClassifySpriteTarget_AtlasBackingTexture_ClassifiesAsAtlasBacked()
    {
        var atlasBackingPathId = 99999L;
        const string atlasName = "sactx-0-8192x16384-DXT5_BC3-ui_sprite_atlas-614d9c5f";
        var assets = new List<AssetEntry>
        {
            new AssetEntry
            {
                Name = "icon_hitpoints",
                ClassName = "Sprite",
                PathId = 4151,
                Collection = "resources.assets",
                CanonicalPath = "resources.assets/Sprite/icon_hitpoints--4151",
                Sprite = new AssetSpriteMetadata
                {
                    BackingTexturePathId = atlasBackingPathId,
                    BackingTextureCollection = "resources.assets",
                    BackingTextureName = atlasName,
                    TextureRectX = 100,
                    TextureRectY = 200,
                    TextureRectWidth = 64,
                    TextureRectHeight = 64,
                    PackingRotation = 0,
                },
            },
            new AssetEntry
            {
                Name = atlasName,
                ClassName = "Texture2D",
                PathId = atlasBackingPathId,
                Collection = "resources.assets",
            },
        };

        foreach (var coTenant in new[] { "icon_ammo", "icon_armor", "icon_shield", "icon_speed", "icon_vision", "icon_reinforce" })
        {
            assets.Add(new AssetEntry
            {
                Name = coTenant,
                ClassName = "Sprite",
                PathId = 5000 + coTenant.GetHashCode(),
                Collection = "resources.assets",
                Sprite = new AssetSpriteMetadata
                {
                    BackingTexturePathId = atlasBackingPathId,
                    BackingTextureCollection = "resources.assets",
                    BackingTextureName = atlasName,
                    TextureRectX = 0,
                    TextureRectY = 0,
                    TextureRectWidth = 32,
                    TextureRectHeight = 32,
                    PackingRotation = 0,
                },
            });
        }

        var index = new AssetIndex { Assets = assets };

        // Atlas-backed sprites no longer throw; they classify as atlas-backed for compositing
        var (target, classification) = CompilationService.ResolveAndClassifySpriteTarget(
            index, "icon_hitpoints", "icon_hitpoints", 4151, NullLogSink.Instance);

        Assert.Equal("icon_hitpoints", target.Name);
        Assert.True(classification.IsAtlasBacked);
        Assert.Equal(6, classification.CoTenantCount);
        Assert.Equal(atlasBackingPathId, classification.BackingTexturePathId);
        Assert.Equal(atlasName, classification.BackingTextureName);
        Assert.Equal(100f, classification.TextureRectX);
        Assert.Equal(200f, classification.TextureRectY);
        Assert.Equal(64f, classification.TextureRectWidth);
        Assert.Equal(64f, classification.TextureRectHeight);
    }

    [Fact]
    public void ResolveAndClassifySpriteTarget_MissingBackingTextureIdentity_ThrowsReIndexError()
    {
        var index = new AssetIndex
        {
            Assets =
            [
                new AssetEntry
                {
                    Name = "icon_hitpoints",
                    ClassName = "Sprite",
                    PathId = 4151,
                    Collection = "resources.assets",
                    CanonicalPath = "resources.assets/Sprite/icon_hitpoints--4151",
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationService.ResolveAndClassifySpriteTarget(index, "icon_hitpoints", "icon_hitpoints", 4151, NullLogSink.Instance));

        Assert.Contains("jiangyu assets index", ex.Message);
        Assert.Contains("backing-texture", ex.Message);
    }

    [Fact]
    public void ResolveAndClassifySpriteTarget_MissingTextureRect_ThrowsReIndexError()
    {
        var index = new AssetIndex
        {
            Assets =
            [
                new AssetEntry
                {
                    Name = "icon_hitpoints",
                    ClassName = "Sprite",
                    PathId = 4151,
                    Collection = "resources.assets",
                    CanonicalPath = "resources.assets/Sprite/icon_hitpoints--4151",
                    Sprite = new AssetSpriteMetadata
                    {
                        BackingTexturePathId = 2164,
                        BackingTextureCollection = "resources.assets",
                        BackingTextureName = "some_texture",
                    },
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationService.ResolveAndClassifySpriteTarget(index, "icon_hitpoints", "icon_hitpoints", 4151, NullLogSink.Instance));

        Assert.Contains("textureRect", ex.Message);
        Assert.Contains("jiangyu assets index", ex.Message);
    }

    [Fact]
    public void ResolveAndClassifySpriteTarget_AmbiguousRuntimeName_ResolvesAndWarns()
    {
        var index = new AssetIndex
        {
            Assets =
            [
                new AssetEntry
                {
                    Name = "icon_hitpoints",
                    ClassName = "Sprite",
                    PathId = 4151,
                    Collection = "resources.assets",
                    CanonicalPath = "resources.assets/Sprite/icon_hitpoints--4151",
                    Sprite = new AssetSpriteMetadata
                    {
                        BackingTexturePathId = 100,
                        BackingTextureCollection = "resources.assets",
                        BackingTextureName = "unique_tex_a",
                        TextureRectX = 0,
                        TextureRectY = 0,
                        TextureRectWidth = 64,
                        TextureRectHeight = 64,
                        PackingRotation = 0,
                    },
                },
                new AssetEntry
                {
                    Name = "icon_hitpoints",
                    ClassName = "Sprite",
                    PathId = 4152,
                    Collection = "resources.assets",
                    CanonicalPath = "resources.assets/Sprite/icon_hitpoints--4152",
                    Sprite = new AssetSpriteMetadata
                    {
                        BackingTexturePathId = 200,
                        BackingTextureCollection = "resources.assets",
                        BackingTextureName = "unique_tex_b",
                        TextureRectX = 0,
                        TextureRectY = 0,
                        TextureRectWidth = 64,
                        TextureRectHeight = 64,
                        PackingRotation = 0,
                    },
                },
                new AssetEntry { Name = "unique_tex_a", ClassName = "Texture2D", PathId = 100, Collection = "resources.assets" },
                new AssetEntry { Name = "unique_tex_b", ClassName = "Texture2D", PathId = 200, Collection = "resources.assets" },
            ],
        };
        var log = new RecordingLog();

        var (target, _) = CompilationService.ResolveAndClassifySpriteTarget(
            index, "icon_hitpoints", "icon_hitpoints", targetPathId: null, log);

        Assert.Equal("icon_hitpoints", target.Name);
        Assert.Equal(4151, target.PathId);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("paint 2 Sprite instances", warning);
        Assert.Contains("icon_hitpoints--4151", warning);
        Assert.Contains("icon_hitpoints--4152", warning);
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) { }
    }
}
