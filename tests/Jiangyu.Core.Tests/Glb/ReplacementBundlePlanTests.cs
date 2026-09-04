using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Glb;

public sealed class ReplacementBundlePlanTests : IDisposable
{
    private readonly string _dir;

    public ReplacementBundlePlanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"jiangyu-bundle-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static GlbMeshBundleCompiler.ImportedAudioAsset Audio(string name)
        => new() { Name = name, SourceFilePath = "unused.wav", Extension = ".wav" };

    private static GlbMeshBundleCompiler.CompiledTexture Texture(string name, byte[]? content = null, bool linear = false, bool isAddition = false)
        => new() { Name = name, Content = content ?? [1, 2, 3], Linear = linear, IsAddition = isAddition };

    private GlbMeshBundleCompiler.ImportedSpriteAsset Sprite(string name, bool isAddition, string content = "png")
        => new()
        {
            Name = name,
            SourceFilePath = WriteSource($"{name}.png", content),
            Extension = ".png",
            StagingName = $"sprite_source__{name}",
            IsAddition = isAddition,
        };

    private static ReplacementBundlePlan Build(
        string bundleName = "mymod",
        string[]? meshes = null,
        GlbMeshBundleCompiler.CompiledTexture[]? textures = null,
        GlbMeshBundleCompiler.ImportedSpriteAsset[]? sprites = null,
        GlbMeshBundleCompiler.ImportedAudioAsset[]? audio = null)
        => ReplacementBundlePlan.Build(bundleName, meshes ?? [], textures ?? [], sprites ?? [], audio ?? []);

    [Fact]
    public void AudioGroupsByCharacterPrefix()
    {
        var plan = Build(audio:
        [
            Audio("cheyanne__BarrackEntrance"),
            Audio("cheyanne__Bedroom"),
            Audio("robella__BarrackEntrance"),
        ]);

        Assert.Equal(["mymod__audio__cheyanne", "mymod__audio__robella"], plan.BundleFiles);
        Assert.Contains("audio\tcheyanne__BarrackEntrance\tmymod__audio__cheyanne", plan.PlanText);
        Assert.Contains("audio\trobella__BarrackEntrance\tmymod__audio__robella", plan.PlanText);
    }

    [Fact]
    public void TextureLinesNameTheRoleAndHashIt()
    {
        var addition = Build(textures: [Texture("portrait", isAddition: true)]).PlanText
            .Split('\n').Single(l => l.StartsWith("texture\tportrait\t", StringComparison.Ordinal));
        var replacement = Build(textures: [Texture("portrait")]).PlanText
            .Split('\n').Single(l => l.StartsWith("texture\tportrait\t", StringComparison.Ordinal));

        Assert.EndsWith("\taddition", addition);
        Assert.EndsWith("\treplacement", replacement);
        // Same bytes, different role: the Unity pass bakes them differently, so the hash differs.
        Assert.NotEqual(addition.Split('\t')[3], replacement.Split('\t')[3]);
    }

    [Fact]
    public void UnprefixedNamesFallBackToTheCategoryBundle()
    {
        var plan = Build(
            audio: [Audio("VanillaGunshot")],
            textures: [Texture("bare_texture")]);

        Assert.Contains("mymod__audio", plan.BundleFiles);
        Assert.Contains("mymod__textures", plan.BundleFiles);
    }

    [Fact]
    public void SpritesShareOneBundleAndOnlyReplacementSpritesCarryHashes()
    {
        var plan = Build(sprites:
        [
            Sprite("icon", isAddition: true),
            Sprite("portrait", isAddition: false),
        ]);

        Assert.Equal(["mymod__sprites"], plan.BundleFiles);
        Assert.Contains("spritesource\tportrait\t", plan.PlanText);
        Assert.DoesNotContain("spritesource\ticon", plan.PlanText);
    }

    [Fact]
    public void MeshesGetTheirOwnBundleNamedForTheExtractor()
    {
        var plan = Build(meshes: ["body"]);

        Assert.Equal("mymod__meshes", plan.MeshesBundleFile);
        Assert.Contains("mymod__meshes", plan.BundleFiles);
    }

    [Fact]
    public void NoMeshesMeansNoMeshesBundle()
    {
        var plan = Build(audio: [Audio("a__b")]);

        Assert.Null(plan.MeshesBundleFile);
    }

    [Fact]
    public void BundleFileNamesAreLowercase()
    {
        var plan = Build(bundleName: "MyMod", audio: [Audio("Cheyanne__Line")]);

        Assert.Equal(["mymod__audio__cheyanne"], plan.BundleFiles);
    }

    [Fact]
    public void TextureContentChangeChangesItsHashLine()
    {
        string HashLine(ReplacementBundlePlan p) =>
            p.PlanText.Split('\n').Single(l => l.StartsWith("texture\tskin\t", StringComparison.Ordinal));

        var before = HashLine(Build(textures: [Texture("skin", [1, 2, 3])]));
        var after = HashLine(Build(textures: [Texture("skin", [9, 9, 9])]));
        var linearFlip = HashLine(Build(textures: [Texture("skin", [1, 2, 3], linear: true)]));

        Assert.NotEqual(before, after);
        Assert.NotEqual(before, linearFlip);
        Assert.Equal(before, HashLine(Build(textures: [Texture("skin", [1, 2, 3])])));
    }

    [Fact]
    public void SpriteSourceContentChangeChangesItsHashLine()
    {
        string HashLine(ReplacementBundlePlan p) =>
            p.PlanText.Split('\n').Single(l => l.StartsWith("spritesource\tportrait\t", StringComparison.Ordinal));

        var before = HashLine(Build(sprites: [Sprite("portrait", isAddition: false, content: "one")]));
        File.Delete(Path.Combine(_dir, "portrait.png"));
        var after = HashLine(Build(sprites: [Sprite("portrait", isAddition: false, content: "two")]));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void PlanTextCarriesTheVersionHeader()
    {
        var plan = Build(audio: [Audio("a__b")]);

        Assert.StartsWith("jiangyu-bundle-plan 1\n", plan.PlanText);
    }
}
