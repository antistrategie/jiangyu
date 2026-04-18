using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Compile;

public class SpriteTargetAtlasValidationTests
{
    [Fact]
    public void ResolveAndValidateSpriteTarget_UniqueBackingTexture_Resolves()
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
                    SpriteBackingTexturePathId = 2164,
                    SpriteBackingTextureCollection = "resources.assets",
                    SpriteBackingTextureName = "menace_logo_main_menue_tex",
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

        var target = CompilationService.ResolveAndValidateSpriteTarget(index, "menace_logo_main_menue--4151", "menace_logo_main_menue", 4151);

        Assert.Equal("menace_logo_main_menue", target.Name);
        Assert.Equal(4151, target.PathId);
    }

    [Fact]
    public void ResolveAndValidateSpriteTarget_AtlasBackingTexture_ThrowsWithAtlasAndCoTenants()
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
                SpriteBackingTexturePathId = atlasBackingPathId,
                SpriteBackingTextureCollection = "resources.assets",
                SpriteBackingTextureName = atlasName,
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
                SpriteBackingTexturePathId = atlasBackingPathId,
                SpriteBackingTextureCollection = "resources.assets",
                SpriteBackingTextureName = atlasName,
            });
        }

        var index = new AssetIndex { Assets = assets };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationService.ResolveAndValidateSpriteTarget(index, "icon_hitpoints--4151", "icon_hitpoints", 4151));

        Assert.Contains("icon_hitpoints--4151", ex.Message);
        Assert.Contains(atlasName, ex.Message);
        Assert.Contains("atlas", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("icon_ammo", ex.Message);
    }

    [Fact]
    public void ResolveAndValidateSpriteTarget_MissingBackingTextureIdentity_ThrowsReIndexError()
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
            CompilationService.ResolveAndValidateSpriteTarget(index, "icon_hitpoints--4151", "icon_hitpoints", 4151));

        Assert.Contains("jiangyu assets index", ex.Message);
        Assert.Contains("backing-texture", ex.Message);
    }

    [Fact]
    public void ResolveAndValidateSpriteTarget_AmbiguousRuntimeName_StillRejected()
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
                    SpriteBackingTexturePathId = 100,
                    SpriteBackingTextureCollection = "resources.assets",
                    SpriteBackingTextureName = "unique_tex",
                },
                new AssetEntry
                {
                    Name = "icon_hitpoints",
                    ClassName = "Sprite",
                    PathId = 4152,
                    Collection = "resources.assets",
                    SpriteBackingTexturePathId = 200,
                    SpriteBackingTextureCollection = "resources.assets",
                    SpriteBackingTextureName = "another_tex",
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationService.ResolveAndValidateSpriteTarget(index, "icon_hitpoints--4151", "icon_hitpoints", 4151));

        Assert.Contains("ambiguous", ex.Message);
    }
}
