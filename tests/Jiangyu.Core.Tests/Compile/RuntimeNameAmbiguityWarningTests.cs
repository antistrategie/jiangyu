using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Compile;

/// <summary>
/// The runtime resolves texture/sprite/audio replacements by `name`, so a
/// single replacement file paints every indexed instance that shares the name.
/// We surface this with a compile-log warning instead of rejecting, so modders
/// can intentionally repaint families like "every soldier variant" with one
/// asset. These tests pin the "compiles + warns" half of that contract; the
/// sprite-side warning lives in <see cref="SpriteTargetAtlasValidationTests"/>.
/// </summary>
public class RuntimeNameAmbiguityWarningTests
{
    [Fact]
    public void Texture_AmbiguousName_ResolvesToLowestPathIdAndWarns()
    {
        var index = TextureIndex(
            ("base_soldier_BaseMap", 4002),
            ("base_soldier_BaseMap", 4001),
            ("base_soldier_BaseMap", 4003));
        var log = new RecordingLog();

        var target = CompilationService.ResolveReplacementTextureTarget(
            index, "base_soldier_BaseMap", "base_soldier_BaseMap", targetPathId: null, log);

        Assert.Equal(4001, target.PathId);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("paint 3 Texture2D instances", warning);
        Assert.Contains("base_soldier_BaseMap--4001", warning);
        Assert.Contains("base_soldier_BaseMap--4002", warning);
        Assert.Contains("base_soldier_BaseMap--4003", warning);
    }

    [Fact]
    public void Texture_UniqueName_DoesNotWarn()
    {
        var index = TextureIndex(("base_soldier_BaseMap", 4001));
        var log = new RecordingLog();

        CompilationService.ResolveReplacementTextureTarget(
            index, "base_soldier_BaseMap", "base_soldier_BaseMap", targetPathId: null, log);

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void Texture_PathIdSuffixedFilename_FallsThroughToTargetNotFound()
    {
        var index = TextureIndex(("base_soldier_BaseMap", 4001));
        var log = new RecordingLog();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationService.ResolveReplacementTextureTarget(
                index, "base_soldier_BaseMap--4001", "base_soldier_BaseMap--4001", targetPathId: null, log));

        Assert.Contains("could not be resolved as a Texture2D", ex.Message);
    }

    [Fact]
    public void Audio_AmbiguousName_ResolvesAndWarns()
    {
        var index = AudioIndex(
            ("ui_click", 8002),
            ("ui_click", 8001));
        var wavPath = WriteSilentWav();
        var log = new RecordingLog();

        try
        {
            var target = CompilationService.ResolveAndValidateAudioTarget(
                index, "ui_click", "ui_click", targetPathId: null, wavPath, log);

            Assert.Equal("ui_click", target.Name);
            Assert.Equal(8001, target.PathId);
            var warning = Assert.Single(log.Warnings);
            Assert.Contains("substitute 2 AudioClip instances", warning);
            Assert.Contains("ui_click--8001", warning);
            Assert.Contains("ui_click--8002", warning);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    private static AssetIndex TextureIndex(params (string Name, long PathId)[] entries) =>
        new()
        {
            Assets =
            [
                .. entries.Select(e => new AssetEntry
                {
                    Name = e.Name,
                    ClassName = "Texture2D",
                    PathId = e.PathId,
                    Collection = "resources.assets",
                    CanonicalPath = $"resources.assets/Texture2D/{e.Name}--{e.PathId}",
                }),
            ],
        };

    private static AssetIndex AudioIndex(params (string Name, long PathId)[] entries) =>
        new()
        {
            Assets =
            [
                .. entries.Select(e => new AssetEntry
                {
                    Name = e.Name,
                    ClassName = "AudioClip",
                    PathId = e.PathId,
                    Collection = "resources.assets",
                    CanonicalPath = $"resources.assets/AudioClip/{e.Name}--{e.PathId}",
                }),
            ],
        };

    private static string WriteSilentWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jiangyu-audio-{Guid.NewGuid()}.wav");
        const int sampleRate = 48000;
        const int channels = 2;
        const short bitsPerSample = 16;
        const int payloadBytes = 0;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46464952u);
        writer.Write((uint)(36 + payloadBytes));
        writer.Write(0x45564157u);
        writer.Write(0x20746d66u);
        writer.Write((uint)16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(0x61746164u);
        writer.Write((uint)payloadBytes);
        return path;
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) { }
    }
}
