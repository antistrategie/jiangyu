namespace Jiangyu.Studio.Host.Tests;

public class PathNormalisationTests
{
    [Fact]
    public void PathCombineInput_ProducesForwardSlashes()
    {
        // Path.Combine uses Path.DirectorySeparatorChar (backslash on Windows,
        // forward slash on Linux/macOS). The helper must produce the same
        // forward-slash output on every platform so the UI's path utilities
        // and Monaco model URIs see consistent paths.
        var native = Path.Combine("a", "b", "c.txt");

        var normalised = RpcHelpers.NormaliseSeparators(native);

        Assert.Equal("a/b/c.txt", normalised);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, normalised.Where(c => c != '/'));
    }

    [Fact]
    public void AlreadyForwardSlash_Idempotent()
    {
        Assert.Equal("a/b/c", RpcHelpers.NormaliseSeparators("a/b/c"));
    }

    [Fact]
    public void NoSeparator_Unchanged()
    {
        Assert.Equal("file.txt", RpcHelpers.NormaliseSeparators("file.txt"));
    }

    [Fact]
    public void Empty_Unchanged()
    {
        Assert.Equal(string.Empty, RpcHelpers.NormaliseSeparators(string.Empty));
    }

    [Fact]
    public void AbsoluteNativePath_ProducesForwardSlashes()
    {
        // Build an absolute path the way the host actually does — Directory.GetFiles
        // and the OS folder picker both return native separators. The test verifies
        // the round-trip through the helper.
        var native = Path.Combine(Path.GetTempPath(), "jiangyu-test", "models", "soldier.glb");

        var normalised = RpcHelpers.NormaliseSeparators(native);

        Assert.EndsWith("/jiangyu-test/models/soldier.glb", normalised);
        Assert.DoesNotContain('\\', normalised);
    }
}
