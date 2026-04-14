using Jiangyu.Core.Config;

namespace Jiangyu.Core.Tests.Config;

public class EnvironmentContextTests
{
    [Fact]
    public void ResolveFromConfig_ResolvesGameDataCacheAndUnityEditor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jiangyu-env-{Guid.NewGuid()}");

        try
        {
            Directory.CreateDirectory(root);
            var dataDir = Path.Combine(root, "Menace_Data");
            Directory.CreateDirectory(dataDir);

            var config = new GlobalConfig
            {
                Game = root,
                Cache = "/tmp/jiangyu-cache",
                UnityEditor = "/opt/Unity/Hub/Editor/6000.0.63f1/Editor/Unity",
            };

            var resolution = EnvironmentContext.ResolveFromConfig(config);

            Assert.True(resolution.Success);
            Assert.NotNull(resolution.Context);
            Assert.Equal(dataDir, resolution.Context!.GameDataPath);
            Assert.Equal("/tmp/jiangyu-cache", resolution.Context.CachePath);
            Assert.Equal("/opt/Unity/Hub/Editor/6000.0.63f1/Editor/Unity", resolution.Context.UnityEditorPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
