using Jiangyu.Core.Config;

namespace Jiangyu.Core.Tests.Config;

public class GlobalConfigTests
{
    [Fact]
    public void ExpandHome_TildePath_ExpandsToUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = GlobalConfig.ExpandHome("~/games/Menace");

        Assert.Equal(Path.Combine(home, "games", "Menace"), result);
    }

    [Fact]
    public void ExpandHome_AbsolutePath_Unchanged()
    {
        var result = GlobalConfig.ExpandHome("/opt/games/Menace");

        Assert.Equal("/opt/games/Menace", result);
    }

    [Fact]
    public void ExpandHome_RelativePath_Unchanged()
    {
        var result = GlobalConfig.ExpandHome("games/Menace");

        Assert.Equal("games/Menace", result);
    }

    [Fact]
    public void GetCachePath_WhenCacheSet_ReturnsCachePath()
    {
        var config = new GlobalConfig { Cache = "/custom/cache" };

        Assert.Equal("/custom/cache", config.GetCachePath());
    }

    [Fact]
    public void GetCachePath_WhenCacheNull_ReturnsDefault()
    {
        var config = new GlobalConfig();

        Assert.Equal(GlobalConfig.DefaultCacheDir, config.GetCachePath());
    }

    [Fact]
    public void FromJson_ValidJson_ParsesAllProperties()
    {
        var json = """
        {
            "game": "/opt/games/Menace",
            "unityEditor": "/opt/unity/Editor",
            "cache": "/tmp/cache"
        }
        """;

        var config = GlobalConfig.FromJson(json);

        Assert.Equal("/opt/games/Menace", config.Game);
        Assert.Equal("/opt/unity/Editor", config.UnityEditor);
        Assert.Equal("/tmp/cache", config.Cache);
    }

    [Fact]
    public void FromJson_EmptyJson_ReturnsDefaults()
    {
        var config = GlobalConfig.FromJson("{}");

        Assert.Null(config.Game);
        Assert.Null(config.UnityEditor);
        Assert.Null(config.Cache);
    }

    [Fact]
    public void Roundtrip_NullFieldsOmittedFromJson()
    {
        var config = new GlobalConfig { Game = "/opt/Menace" };
        var json = config.ToJson();
        var restored = GlobalConfig.FromJson(json);

        Assert.Equal("/opt/Menace", restored.Game);
        Assert.Null(restored.UnityEditor);
        Assert.DoesNotContain("unityEditor", json);
        Assert.DoesNotContain("cache", json);
    }

    [Fact]
    public void ResolveGameDataPath_WithConfig_ReturnsMatchingDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jiangyu-game-{Guid.NewGuid()}");

        try
        {
            Directory.CreateDirectory(root);
            var dataDir = Path.Combine(root, "Menace_Data");
            Directory.CreateDirectory(dataDir);

            var (gameDataPath, error) = GlobalConfig.ResolveGameDataPath(new GlobalConfig { Game = root });

            Assert.Equal(dataDir, gameDataPath);
            Assert.Null(error);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
