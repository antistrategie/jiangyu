using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Compile;

public class AudioTargetValidationTests
{
    [Fact]
    public void ResolveAndValidateAudioTarget_MatchingWav_Resolves()
    {
        var index = IndexWithAudioClip("button_click_01", 15854, 48000, 2);
        var wavPath = WriteWav(44100 * 2 * 2, sampleRate: 48000, channels: 2);

        try
        {
            var target = CompilationService.ResolveAndValidateAudioTarget(index, "button_click_01", "button_click_01", 15854, wavPath, NullLogSink.Instance);

            Assert.Equal("button_click_01", target.Name);
            Assert.Equal(15854, target.PathId);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void ResolveAndValidateAudioTarget_SampleRateMismatch_Throws()
    {
        var index = IndexWithAudioClip("button_click_01", 15854, 48000, 2);
        var wavPath = WriteWav(44100 * 2 * 2, sampleRate: 44100, channels: 2);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                CompilationService.ResolveAndValidateAudioTarget(index, "button_click_01", "button_click_01", 15854, wavPath, NullLogSink.Instance));

            Assert.Contains("44100Hz", ex.Message);
            Assert.Contains("48000Hz", ex.Message);
            Assert.Contains("pitch-shifts", ex.Message);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void ResolveAndValidateAudioTarget_ChannelMismatch_Throws()
    {
        var index = IndexWithAudioClip("button_click_01", 15854, 48000, 2);
        var wavPath = WriteWav(44100 * 2 * 2, sampleRate: 48000, channels: 1);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                CompilationService.ResolveAndValidateAudioTarget(index, "button_click_01", "button_click_01", 15854, wavPath, NullLogSink.Instance));

            Assert.Contains("1ch", ex.Message);
            Assert.Contains("2ch", ex.Message);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void ResolveAndValidateAudioTarget_OggSource_SkipsWavOnlyValidation()
    {
        var index = IndexWithAudioClip("button_click_01", 15854, 48000, 2);
        // Any random bytes; we never parse OGG headers so it won't matter.
        var oggPath = Path.Combine(Path.GetTempPath(), $"jiangyu-audio-{Guid.NewGuid()}.ogg");
        File.WriteAllBytes(oggPath, new byte[] { 0x4F, 0x67, 0x67, 0x53 });

        try
        {
            var target = CompilationService.ResolveAndValidateAudioTarget(index, "button_click_01", "button_click_01", 15854, oggPath, NullLogSink.Instance);

            Assert.Equal("button_click_01", target.Name);
        }
        finally
        {
            File.Delete(oggPath);
        }
    }

    [Fact]
    public void ResolveAndValidateAudioTarget_NoFrequencyInIndex_SkipsValidation()
    {
        var index = new AssetIndex
        {
            Assets =
            [
                new AssetEntry
                {
                    Name = "button_click_01",
                    ClassName = "AudioClip",
                    PathId = 15854,
                    Collection = "resources.assets",
                    // AudioFrequency / AudioChannels intentionally null (old-schema index)
                },
            ],
        };
        var wavPath = WriteWav(44100 * 2 * 2, sampleRate: 22050, channels: 1);

        try
        {
            var target = CompilationService.ResolveAndValidateAudioTarget(index, "button_click_01", "button_click_01", 15854, wavPath, NullLogSink.Instance);

            Assert.Equal("button_click_01", target.Name);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    private static AssetIndex IndexWithAudioClip(string name, long pathId, int frequency, int channels) =>
        new()
        {
            Assets =
            [
                new AssetEntry
                {
                    Name = name,
                    ClassName = "AudioClip",
                    PathId = pathId,
                    Collection = "resources.assets",
                    CanonicalPath = $"resources.assets/AudioClip/{name}--{pathId}",
                    AudioFrequency = frequency,
                    AudioChannels = channels,
                },
            ],
        };

    private static string WriteWav(int payloadBytes, int sampleRate, int channels)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jiangyu-audio-{Guid.NewGuid()}.wav");

        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46464952u); // "RIFF"
        writer.Write((uint)(36 + payloadBytes));
        writer.Write(0x45564157u); // "WAVE"
        writer.Write(0x20746d66u); // "fmt "
        writer.Write((uint)16);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(0x61746164u); // "data"
        writer.Write((uint)payloadBytes);
        writer.Write(new byte[payloadBytes]);
        return path;
    }
}
