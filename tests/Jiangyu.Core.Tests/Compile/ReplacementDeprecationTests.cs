using Jiangyu.Core.Compile;

namespace Jiangyu.Core.Tests.Compile;

public sealed class ReplacementDeprecationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jiangyu-replacement-deprecation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Touch(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    [Fact]
    public void MissingRoot_HasNoCategoriesAndNoWarning()
    {
        var inUse = ReplacementDiscovery.CategoriesInUse(_root);

        Assert.Empty(inUse);
        Assert.Null(ReplacementDiscovery.DeprecationWarning(inUse));
    }

    [Fact]
    public void EmptyAndDotfileOnlyFolders_DoNotCount()
    {
        Directory.CreateDirectory(Path.Combine(_root, "textures"));
        Touch("sprites", ".gitkeep");

        Assert.Empty(ReplacementDiscovery.CategoriesInUse(_root));
    }

    [Fact]
    public void AuthoredFiles_AreReportedPerCategory_NestedIncluded()
    {
        Touch("audio", "shot.wav");
        Touch("models", "soldier--12", "model.gltf");

        Assert.Equal(["models", "audio"], ReplacementDiscovery.CategoriesInUse(_root));
    }

    [Fact]
    public void Warning_NamesTheCategoriesAndTheAlternative()
    {
        var warning = ReplacementDiscovery.DeprecationWarning(["textures", "audio"]);

        Assert.NotNull(warning);
        Assert.Contains("in use: textures, audio", warning);
        Assert.Contains("assets/additions/", warning);
        Assert.Contains("asset=", warning);
    }
}
