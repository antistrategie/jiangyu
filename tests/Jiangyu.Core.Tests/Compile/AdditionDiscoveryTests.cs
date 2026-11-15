using Jiangyu.Core.Compile;
using Jiangyu.Shared.Replacements;

namespace Jiangyu.Core.Tests.Compile;

public sealed class AdditionDiscoveryTests : IDisposable
{
    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), $"jiangyu-addition-discovery-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_projectDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }

    [Fact]
    public void Textures_AreDiscoveredFromBothLayouts_NamedByPathUnderTheCategoryFolder()
    {
        Write("assets/textures/character/art.png");
        Write("assets/additions/textures/skin.png");

        var names = ReplacementDiscovery.DiscoverAdditionTextureEntries(AdditionPaths.Roots(_projectDir))
            .Select(texture => texture.Name)
            .ToList();

        Assert.Equal(["character__art", "skin"], names);
    }

    [Fact]
    public void SpritesAndAudio_AreDiscoveredFromTheAssetsCategoryFolders()
    {
        Write("assets/sprites/lrm5/icon.png");
        Write("assets/audio/fire_01.wav");
        var roots = AdditionPaths.Roots(_projectDir);

        var sprite = Assert.Single(ReplacementDiscovery.DiscoverAdditionSpriteEntries(roots));
        var clip = Assert.Single(ReplacementDiscovery.DiscoverAdditionAudioEntries(roots));

        Assert.Equal("lrm5__icon", sprite.Name);
        Assert.True(sprite.IsAddition);
        Assert.Equal("fire_01", clip.Name);
    }

    [Fact]
    public void ReplacementFiles_AreNotDiscoveredAsAdditions()
    {
        Write("assets/replacements/textures/window_background.png");

        Assert.Empty(ReplacementDiscovery.DiscoverAdditionTextureEntries(AdditionPaths.Roots(_projectDir)));
    }
}
