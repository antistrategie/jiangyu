using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Glb;

/// <summary>
/// Staging into <c>unity/Assets/Jiangyu/Staging/MeshReplacement/</c> must be a
/// content-stable sync: unchanged files (and the Unity <c>.meta</c> GUIDs beside
/// them) survive across builds so Unity's import cache can absorb them, while
/// changed files are rewritten and removed files are cleaned up with their metas.
/// </summary>
public sealed class MeshBundleStagingSyncTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceDir;
    private readonly string _unityDir;

    public MeshBundleStagingSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"jiangyu-staging-sync-{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_root, "assets");
        _unityDir = Path.Combine(_root, "unity");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_unityDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string StagingDir => Path.Combine(_unityDir, "Assets", "Jiangyu", "Staging", "MeshReplacement");
    private string AudioDir => Path.Combine(StagingDir, "Audio");
    private string SpriteSourcesDir => Path.Combine(StagingDir, "SpriteSources");
    private string SpriteAdditionsDir => Path.Combine(StagingDir, "SpriteAdditions");

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static GlbMeshBundleCompiler.ImportedAudioAsset Audio(string name, string sourcePath, string extension = ".wav")
        => new() { Name = name, SourceFilePath = sourcePath, Extension = extension };

    private static GlbMeshBundleCompiler.ImportedSpriteAsset Sprite(string name, string sourcePath, bool isAddition)
        => new()
        {
            Name = name,
            SourceFilePath = sourcePath,
            Extension = ".png",
            StagingName = $"sprite_source__{name}",
            IsAddition = isAddition,
        };

    private Task Sync(
        IReadOnlyList<GlbMeshBundleCompiler.ImportedSpriteAsset>? sprites = null,
        IReadOnlyList<GlbMeshBundleCompiler.ImportedAudioAsset>? audio = null)
        => MeshBundleUnityBuild.StageReplacementAssetsAsync(_unityDir, sprites ?? [], audio ?? []);

    [Fact]
    public async Task StagesEachCategoryIntoItsDirectory()
    {
        var wav = WriteSource("clip.wav", "wav-bytes");
        var replacement = WriteSource("replace.png", "replacement-bytes");
        var addition = WriteSource("add.png", "addition-bytes");

        await Sync(
            sprites: [Sprite("replace", replacement, isAddition: false), Sprite("add", addition, isAddition: true)],
            audio: [Audio("clip", wav)]);

        Assert.Equal("wav-bytes", File.ReadAllText(Path.Combine(AudioDir, "clip.wav")));
        Assert.Equal("replacement-bytes", File.ReadAllText(Path.Combine(SpriteSourcesDir, "sprite_source__replace.png")));
        Assert.Equal("addition-bytes", File.ReadAllText(Path.Combine(SpriteAdditionsDir, "add.png")));
    }

    [Fact]
    public async Task ExtensionWithoutLeadingDotIsNormalised()
    {
        var wav = WriteSource("clip.wav", "wav-bytes");

        await Sync(audio: [Audio("clip", wav, extension: "wav")]);

        Assert.True(File.Exists(Path.Combine(AudioDir, "clip.wav")));
    }

    [Fact]
    public async Task UnchangedFileIsNotRewritten()
    {
        var wav = WriteSource("clip.wav", "original!");
        await Sync(audio: [Audio("clip", wav)]);

        // Same length and mtime as the source, different content: only a skipped copy
        // leaves this tampered marker in place.
        var staged = Path.Combine(AudioDir, "clip.wav");
        File.WriteAllText(staged, "tampered!");
        File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(wav));

        await Sync(audio: [Audio("clip", wav)]);

        Assert.Equal("tampered!", File.ReadAllText(staged));
    }

    [Fact]
    public async Task UnchangedFileKeepsItsMeta()
    {
        var wav = WriteSource("clip.wav", "wav-bytes");
        await Sync(audio: [Audio("clip", wav)]);

        var meta = Path.Combine(AudioDir, "clip.wav.meta");
        File.WriteAllText(meta, "guid: stable");

        await Sync(audio: [Audio("clip", wav)]);

        Assert.Equal("guid: stable", File.ReadAllText(meta));
    }

    [Fact]
    public async Task ChangedSourceIsRecopied()
    {
        var wav = WriteSource("clip.wav", "before");
        await Sync(audio: [Audio("clip", wav)]);

        WriteSource("clip.wav", "after with different length");
        await Sync(audio: [Audio("clip", wav)]);

        Assert.Equal("after with different length", File.ReadAllText(Path.Combine(AudioDir, "clip.wav")));
    }

    [Fact]
    public async Task SameLengthEditIsRecopied()
    {
        var wav = WriteSource("clip.wav", "aaaa");
        await Sync(audio: [Audio("clip", wav)]);

        File.WriteAllText(wav, "bbbb");
        await Sync(audio: [Audio("clip", wav)]);

        Assert.Equal("bbbb", File.ReadAllText(Path.Combine(AudioDir, "clip.wav")));
    }

    [Fact]
    public async Task TimestampPreservingEditIsStillRecopied()
    {
        // The decision is a recorded content hash, never file times: an export pipeline
        // that preserves timestamps (zip extraction, reproducible bakes) must restage.
        var wav = WriteSource("clip.wav", "aaaa");
        var originalMtime = File.GetLastWriteTimeUtc(wav);
        await Sync(audio: [Audio("clip", wav)]);

        File.WriteAllText(wav, "bbbb");
        File.SetLastWriteTimeUtc(wav, originalMtime);
        await Sync(audio: [Audio("clip", wav)]);

        Assert.Equal("bbbb", File.ReadAllText(Path.Combine(AudioDir, "clip.wav")));
    }

    [Fact]
    public async Task RemovedAssetIsDeletedWithItsMeta()
    {
        var keep = WriteSource("keep.wav", "keep");
        var drop = WriteSource("drop.wav", "drop");
        await Sync(audio: [Audio("keep", keep), Audio("drop", drop)]);
        File.WriteAllText(Path.Combine(AudioDir, "keep.wav.meta"), "guid: keep");
        File.WriteAllText(Path.Combine(AudioDir, "drop.wav.meta"), "guid: drop");

        await Sync(audio: [Audio("keep", keep)]);

        Assert.True(File.Exists(Path.Combine(AudioDir, "keep.wav")));
        Assert.True(File.Exists(Path.Combine(AudioDir, "keep.wav.meta")));
        Assert.False(File.Exists(Path.Combine(AudioDir, "drop.wav")));
        Assert.False(File.Exists(Path.Combine(AudioDir, "drop.wav.meta")));
    }

    [Fact]
    public async Task EmptyCategoryRemovesItsDirectoryAndMeta()
    {
        var wav = WriteSource("clip.wav", "wav-bytes");
        await Sync(audio: [Audio("clip", wav)]);
        File.WriteAllText($"{AudioDir}.meta", "guid: dir");

        await Sync();

        Assert.False(Directory.Exists(AudioDir));
        Assert.False(File.Exists($"{AudioDir}.meta"));
    }

    [Fact]
    public async Task OrphanMetaIsSwept()
    {
        var wav = WriteSource("clip.wav", "wav-bytes");
        await Sync(audio: [Audio("clip", wav)]);
        File.WriteAllText(Path.Combine(AudioDir, "ghost.wav.meta"), "guid: ghost");

        await Sync(audio: [Audio("clip", wav)]);

        Assert.False(File.Exists(Path.Combine(AudioDir, "ghost.wav.meta")));
    }

    [Fact]
    public async Task MetaOfRestagedFileSurvives()
    {
        var wav = WriteSource("clip.wav", "wav-bytes");
        await Sync(audio: [Audio("clip", wav)]);
        File.WriteAllText(Path.Combine(AudioDir, "clip.wav.meta"), "guid: stable");

        // The asset vanished (say, a crash mid-write) but its meta survived. Restaging
        // the file must keep the meta so the GUID, and any references to it, hold.
        File.Delete(Path.Combine(AudioDir, "clip.wav"));
        await Sync(audio: [Audio("clip", wav)]);

        Assert.True(File.Exists(Path.Combine(AudioDir, "clip.wav")));
        Assert.Equal("guid: stable", File.ReadAllText(Path.Combine(AudioDir, "clip.wav.meta")));
    }
}
