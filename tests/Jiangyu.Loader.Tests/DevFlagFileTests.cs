using System;
using System.IO;
using Jiangyu.Shared.Dev;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class DevFlagFileTests
{
    [Fact]
    public void Parse_HandlesBareKeysCommentsBlanksAndValues()
    {
        var flags = DevFlagFile.Parse(new[]
        {
            "# a comment",
            "",
            "  debug  ",
            "inspect=20",
            "   # indented comment",
            "key = value with spaces ",
        });

        Assert.True(flags.ContainsKey("debug"));
        Assert.Null(flags["debug"]);
        Assert.Equal("20", flags["inspect"]);
        Assert.Equal("value with spaces", flags["key"]);
        Assert.Equal(3, flags.Count);
    }

    [Fact]
    public void Parse_KeysAreCaseInsensitive()
    {
        var flags = DevFlagFile.Parse(new[] { "Bridge" });
        Assert.True(flags.ContainsKey("bridge"));
        Assert.True(flags.ContainsKey("BRIDGE"));
    }

    [Fact]
    public void Parse_SplitsOnFirstEqualsOnly()
    {
        var flags = DevFlagFile.Parse(new[] { "k=a=b" });
        Assert.Equal("a=b", flags["k"]);
    }

    [Fact]
    public void Read_MissingFile_IsEmptyAndDisabled()
    {
        using var dir = new TempDir();
        Assert.Empty(DevFlagFile.Read(dir.Path));
        Assert.False(DevFlagFile.IsEnabled(dir.Path, "bridge"));
    }

    [Fact]
    public void Set_AddsBareToggleWhenAbsent()
    {
        using var dir = new TempDir();
        DevFlagFile.Set(dir.Path, "bridge", enabled: true);
        Assert.True(DevFlagFile.IsEnabled(dir.Path, "bridge"));
    }

    [Fact]
    public void Set_RemovesTogglePreservingOtherLinesAndComments()
    {
        using var dir = new TempDir();
        File.WriteAllLines(dir.FlagsPath, new[] { "# header", "debug", "bridge", "ui" });

        DevFlagFile.Set(dir.Path, "bridge", enabled: false);

        var lines = File.ReadAllLines(dir.FlagsPath);
        Assert.Contains("# header", lines);
        Assert.Contains("debug", lines);
        Assert.Contains("ui", lines);
        Assert.DoesNotContain("bridge", lines);
        Assert.True(DevFlagFile.IsEnabled(dir.Path, "debug"));
        Assert.False(DevFlagFile.IsEnabled(dir.Path, "bridge"));
    }

    [Fact]
    public void Set_RemovesValueFormCaseInsensitively()
    {
        using var dir = new TempDir();
        File.WriteAllLines(dir.FlagsPath, new[] { "Bridge=1" });

        DevFlagFile.Set(dir.Path, "bridge", enabled: false);

        Assert.False(DevFlagFile.IsEnabled(dir.Path, "bridge"));
    }

    [Fact]
    public void Set_IsIdempotent()
    {
        using var dir = new TempDir();
        DevFlagFile.Set(dir.Path, "bridge", enabled: true);
        DevFlagFile.Set(dir.Path, "bridge", enabled: true);

        var occurrences = 0;
        foreach (var line in File.ReadAllLines(dir.FlagsPath))
            if (line.Trim() == "bridge")
                occurrences++;
        Assert.Equal(1, occurrences);

        DevFlagFile.Set(dir.Path, "bridge", enabled: false);
        DevFlagFile.Set(dir.Path, "bridge", enabled: false);
        Assert.False(DevFlagFile.IsEnabled(dir.Path, "bridge"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jiangyu-flagtest-" + Guid.NewGuid().ToString("N"));

        public string FlagsPath => System.IO.Path.Combine(Path, DevFlagFile.FileName);

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
