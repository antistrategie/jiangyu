using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Tests.Unity;

/// <summary>
/// UnityBundleInvoker shells out to a Unity Editor in production, but the
/// helper itself is content-free of Unity-specific knowledge: it stitches a
/// fixed command-line header onto caller-supplied extras, runs the process,
/// and reports the exit code plus the log tail. We exercise it against
/// trivial shell executables to confirm the envelope, without needing a
/// real Unity install.
/// </summary>
public sealed class UnityBundleInvokerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-unity-invoker-{Guid.NewGuid():N}");

    public UnityBundleInvokerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsSuccess_OnZeroExitCode()
    {
        if (!File.Exists("/bin/true"))
        {
            // Skip on Windows-only CI; everything else has /bin/true.
            return;
        }

        var logFile = Path.Combine(_tempDir, "build.log");
        File.WriteAllText(logFile, "stub log content");

        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = "/bin/true",
            ProjectPath = _tempDir,
            ExecuteMethod = "Whatever.Method",
            LogFile = logFile,
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stub log content", result.LogTailLines);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsFailure_OnNonZeroExitCode()
    {
        if (!File.Exists("/bin/false"))
            return;

        var logFile = Path.Combine(_tempDir, "build.log");
        // No log file written: helper should return an empty tail rather
        // than throwing on the missing log.

        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = "/bin/false",
            ProjectPath = _tempDir,
            ExecuteMethod = "Whatever.Method",
            LogFile = logFile,
        });

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.LogTailLines);
    }

    [Fact]
    public async Task InvokeAsync_CapsLogTailAtTwentyLines()
    {
        if (!File.Exists("/bin/true"))
            return;

        var logFile = Path.Combine(_tempDir, "build.log");
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToArray();
        File.WriteAllLines(logFile, lines);

        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = "/bin/true",
            ProjectPath = _tempDir,
            ExecuteMethod = "Whatever.Method",
            LogFile = logFile,
        });

        Assert.True(result.Success);
        Assert.Equal(20, result.LogTailLines.Count);
        Assert.Equal("line 81", result.LogTailLines[0]);
        Assert.Equal("line 100", result.LogTailLines[^1]);
    }

    [Fact]
    public async Task InvokeAsync_PassesExtraArgs_AsKeyValuePairs()
    {
        // We can't observe the command line directly without intercepting
        // Process.Start; instead use `/bin/sh -c 'echo "$@" > out'` style
        // capture. Simplest cross-platform-ish: have /bin/sh write its
        // arguments into the log file via a custom script.
        if (!File.Exists("/bin/sh"))
            return;

        var capturePath = Path.Combine(_tempDir, "args.txt");
        var scriptPath = Path.Combine(_tempDir, "capture.sh");
        File.WriteAllText(scriptPath, $"#!/bin/sh\necho \"$@\" > \"{capturePath}\"\n");
        // Make script executable on POSIX. On Windows this would silently
        // no-op; that's why we early-return when /bin/sh is missing.
        using (var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/chmod", $"+x \"{scriptPath}\"")
        { UseShellExecute = false }))
        {
            chmod?.WaitForExit();
        }

        var logFile = Path.Combine(_tempDir, "build.log");

        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = scriptPath,
            ProjectPath = _tempDir,
            ExecuteMethod = "Foo.Bar",
            LogFile = logFile,
            ExtraArgs =
            [
                new("bundleName", "test_bundle"),
                new("dataPath", Path.Combine(_tempDir, "mesh.bin")),
            ],
        });

        Assert.True(result.Success);
        var captured = await File.ReadAllTextAsync(capturePath);
        Assert.Contains("-batchmode", captured);
        Assert.Contains("-nographics", captured);
        Assert.Contains("-quit", captured);
        Assert.Contains("-executeMethod Foo.Bar", captured);
        // Bare-token extra (no spaces or path separators) goes through unquoted.
        Assert.Contains("-bundleName test_bundle", captured);
        // Path-like extra is quoted by the helper; sh strips the quotes when
        // reflecting "$@" back via echo, so we just check the unquoted form
        // lands as one shell argument.
        var meshPath = Path.Combine(_tempDir, "mesh.bin");
        Assert.Contains($"-dataPath {meshPath}", captured);
    }

    // --- Cold-project retry policy (InvokeWithRetryAsync) ---
    // These drive the retry loop with a scripted runner instead of a real Unity process, and let
    // it observe a real file so the ExpectedOutputPath check is exercised as it runs in production.

    private static Task<UnityBundleInvocationResult> Ok() => Task.FromResult(new UnityBundleInvocationResult(0, Array.Empty<string>()));
    private static Task<UnityBundleInvocationResult> Failed() => Task.FromResult(new UnityBundleInvocationResult(1, Array.Empty<string>()));

    private UnityBundleInvocation Invocation(string? expectedOutputPath) => new()
    {
        UnityEditor = "unused",
        ProjectPath = _tempDir,
        ExecuteMethod = "Whatever.Method",
        LogFile = Path.Combine(_tempDir, "build.log"),
        ExpectedOutputPath = expectedOutputPath,
    };

    [Fact]
    public async Task Retry_RunsTwice_WhenFirstAttemptWritesNoBundleThenWarmRunSucceeds()
    {
        var bundle = Path.Combine(_tempDir, "mod"); // absent to start
        var calls = 0;

        var result = await UnityBundleInvoker.InvokeWithRetryAsync(Invocation(bundle), () =>
        {
            calls++;
            if (calls == 2)
                File.WriteAllText(bundle, "x"); // the now-warm project produces the bundle
            return Ok();
        });

        Assert.Equal(2, calls);
        Assert.True(result.Success);
        Assert.True(File.Exists(bundle));
    }

    [Fact]
    public async Task Retry_RunsOnce_WhenFirstAttemptProducesTheBundle()
    {
        var bundle = Path.Combine(_tempDir, "mod");
        var calls = 0;

        var result = await UnityBundleInvoker.InvokeWithRetryAsync(Invocation(bundle), () =>
        {
            calls++;
            File.WriteAllText(bundle, "x");
            return Ok();
        });

        Assert.Equal(1, calls); // no wasted second cold start
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Retry_RunsExactlyOnce_WhenNoExpectedOutputIsNamed()
    {
        var calls = 0;

        // A failing run with no expected artefact (the multi-bundle prefab pass) must not retry.
        var result = await UnityBundleInvoker.InvokeWithRetryAsync(Invocation(expectedOutputPath: null), () =>
        {
            calls++;
            return Failed();
        });

        Assert.Equal(1, calls);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Retry_StopsAfterTwoAttempts_WhenTheBundleNeverAppears()
    {
        var bundle = Path.Combine(_tempDir, "never"); // never created
        var calls = 0;

        var result = await UnityBundleInvoker.InvokeWithRetryAsync(Invocation(bundle), () =>
        {
            calls++;
            return Ok(); // exit 0 but writes nothing
        });

        Assert.Equal(2, calls); // bounded: one retry, no more
        Assert.False(File.Exists(bundle));
        Assert.Equal(0, result.ExitCode); // returns the last attempt for the caller to reject
    }
}
