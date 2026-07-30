using Jiangyu.Core.Compile;
using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Compile;

public class ReplacementNameValidationTests
{
    private static GlbMeshBundleCompiler.ImportedAudioAsset Audio(string name, string source)
        => new() { Name = name, SourceFilePath = source, Extension = ".wav" };

    private static GlbMeshBundleCompiler.ImportedSpriteAsset Sprite(string name, string source, bool isAddition)
        => new()
        {
            Name = name,
            SourceFilePath = source,
            Extension = ".png",
            StagingName = $"sprite_source__{name}",
            IsAddition = isAddition,
        };

    private static GlbMeshBundleCompiler.CompiledTexture Texture(string name)
        => new() { Name = name, Content = [1], Linear = false };

    private static List<string> Find(
        IReadOnlyList<GlbMeshBundleCompiler.ImportedAudioAsset>? audio = null,
        IReadOnlyList<GlbMeshBundleCompiler.ImportedSpriteAsset>? sprites = null,
        IReadOnlyList<GlbMeshBundleCompiler.CompiledTexture>? textures = null,
        IEnumerable<string>? meshes = null,
        string bundleName = "mymod",
        IEnumerable<string>? prefabStems = null)
        => ReplacementNameValidation.FindConflicts(
            audio ?? [], sprites ?? [], textures ?? [], meshes ?? [], bundleName, prefabStems ?? []);

    [Fact]
    public void DisjointNamesProduceNoConflicts()
    {
        var conflicts = Find(
            audio: [Audio("cheyanne__hello", "a.wav")],
            sprites: [Sprite("portrait", "p.png", isAddition: true)],
            textures: [Texture("skin")],
            meshes: ["body"],
            prefabStems: ["cheyanne/main"]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void DuplicateAudioNameAcrossSourcesIsAConflict()
    {
        var conflicts = Find(audio:
        [
            Audio("hello", "assets/replacements/audio/hello.wav"),
            Audio("hello", "assets/additions/audio/hello.wav"),
        ]);

        var conflict = Assert.Single(conflicts);
        Assert.Contains("audio name 'hello'", conflict);
        Assert.Contains("replacements", conflict);
        Assert.Contains("additions", conflict);
    }

    [Fact]
    public void DuplicateSpriteNameAcrossAdditionAndReplacementIsAConflict()
    {
        var conflicts = Find(sprites:
        [
            Sprite("icon", "r.png", isAddition: false),
            Sprite("icon", "a.png", isAddition: true),
        ]);

        Assert.Contains(conflicts, c => c.Contains("sprite name 'icon'"));
    }

    [Fact]
    public void TextureAndMeshSharingANameIsAGeneratedConflict()
    {
        var conflicts = Find(textures: [Texture("body")], meshes: ["body"]);

        var conflict = Assert.Single(conflicts);
        Assert.Contains("generated asset name 'body'", conflict);
    }

    [Fact]
    public void TextureAndReplacementSpriteSharingANameIsAGeneratedConflict()
    {
        var conflicts = Find(
            textures: [Texture("icon")],
            sprites: [Sprite("icon", "r.png", isAddition: false)]);

        Assert.Contains(conflicts, c => c.Contains("generated asset name 'icon'"));
    }

    [Fact]
    public void TextureNamedLikeASpriteSourceCollidesWithTheSpriteBackingTexture()
    {
        var conflicts = Find(
            textures: [Texture("sprite_source__icon")],
            sprites: [Sprite("icon", "r.png", isAddition: false)]);

        Assert.Contains(conflicts, c => c.Contains("generated asset name 'sprite_source__icon'"));
    }

    [Fact]
    public void AdditionSpriteDoesNotOccupyTheGeneratedNamespace()
    {
        // Addition sprites stage as source files under SpriteAdditions/, not as
        // Generated/ assets, so an addition sprite and a texture may share a name.
        var conflicts = Find(
            textures: [Texture("icon")],
            sprites: [Sprite("icon", "a.png", isAddition: true)]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void PrefabStemFlatteningToTheReplacementBundleNameIsAConflict()
    {
        var conflicts = Find(bundleName: "mymod", prefabStems: ["mymod"]);

        var conflict = Assert.Single(conflicts);
        Assert.Contains("replacement bundle", conflict);
    }

    [Fact]
    public void NestedPrefabStemFlattensBeforeTheBundleNameComparison()
    {
        var conflicts = Find(bundleName: "my__mod", prefabStems: ["my/mod"]);

        Assert.Single(conflicts);
    }

    [Fact]
    public void PrefabBundleNameComparisonIsCaseInsensitive()
    {
        var conflicts = Find(bundleName: "mymod", prefabStems: ["MyMod"]);

        Assert.Single(conflicts);
    }

    [Fact]
    public void PrefabStemInTheReplacementBundleNamespaceIsAConflict()
    {
        // Replacement bundles ship as <mod>__<category>[__<group>].bundle, so any prefab
        // flattening onto one of those shapes would clobber it.
        var conflicts = Find(bundleName: "mymod", prefabStems: ["mymod/audio", "mymod__sprites", "mymod/textures/skin"]);

        Assert.Equal(3, conflicts.Count);
    }

    [Fact]
    public void PrefabStemMerelySharingThePrefixTextIsNoConflict()
    {
        // A mod named after its character keeps the documented Character/Character prefab
        // convention: mymod__mymod is not a replacement bundle shape.
        var conflicts = Find(bundleName: "mymod", prefabStems: ["mymodextra", "my/mod", "mymod/mymod", "mymod__voymastina"]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void CaseOnlyDuplicatesAreConflicts()
    {
        // Staged files, Generated/ assets, and Unity's asset database are all
        // case-insensitive on Windows and macOS, so case-only name pairs clobber there.
        var conflicts = Find(audio:
        [
            Audio("Hello", "assets/replacements/audio/Hello.wav"),
            Audio("hello", "assets/additions/audio/hello.wav"),
        ]);

        Assert.Single(conflicts);
    }
}
