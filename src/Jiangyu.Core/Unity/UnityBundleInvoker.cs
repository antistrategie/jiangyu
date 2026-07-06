using System.Diagnostics;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Unity;

/// <summary>
/// Shared batchmode entry point for compile-time bundle builds. Three call
/// sites used to hand-roll a <c>Process.Start</c> with the same fixed
/// header (<c>-batchmode -nographics -quit -buildTarget
/// StandaloneWindows64</c>) plus a per-build set of named arguments and a
/// log-tail dump on failure. They now share this helper; the per-build
/// difference is the <c>ExecuteMethod</c> and the trailing key/value pairs.
///
/// <para>Each caller still owns its own error policy: some throw, some
/// return false to the orchestrator. The helper just builds the command
/// line, runs the process, and reads back the last 20 log lines so the
/// caller can attach them to whichever surface it wants.</para>
/// </summary>
public static class UnityBundleInvoker
{
    /// <summary>
    /// Run Unity batchmode against <paramref name="invocation"/> and read
    /// back the tail of the build log. Throws nothing on a non-zero exit
    /// code: the caller inspects <see cref="UnityBundleInvocationResult.ExitCode"/>
    /// (or the <see cref="UnityBundleInvocationResult.Success"/> helper)
    /// and decides whether to throw, return false, or surface the
    /// <see cref="UnityBundleInvocationResult.LogTailLines"/> through its
    /// own log sink.
    /// </summary>
    public static async Task<UnityBundleInvocationResult> InvokeAsync(UnityBundleInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var args = new List<string>
        {
            "-batchmode",
            "-nographics",
            "-quit",
            "-buildTarget StandaloneWindows64",
            $"-projectPath \"{invocation.ProjectPath}\"",
            $"-executeMethod {invocation.ExecuteMethod}",
            $"-logFile \"{invocation.LogFile}\"",
        };
        foreach (var (name, value) in invocation.ExtraArgs)
        {
            if (string.IsNullOrEmpty(value))
                continue;
            // Wrap path-like values (anything containing a separator) in
            // quotes; bare-token values (e.g. -bundleName foo) get passed
            // through. Callers decide which shape they want.
            args.Add(value.Contains(' ') || value.Contains('/') || value.Contains('\\')
                ? $"-{name} \"{value}\""
                : $"-{name} {value}");
        }
        var arguments = string.Join(" ", args);

        return await InvokeWithRetryAsync(invocation, () => RunOnceAsync(invocation, arguments));
    }

    /// <summary>
    /// The cold-project retry policy, separated from process spawning so it is testable with a
    /// scripted <paramref name="runOnce"/>. A cold Unity project (no Library/, e.g. a fresh clone
    /// or a modder's first-ever compile) can spend its first batchmode invocation on the initial
    /// asset import and script compile, then quit before running the build: the process exits 0
    /// (or the build's own guard exits non-zero) with no bundle written. When the caller names an
    /// <see cref="UnityBundleInvocation.ExpectedOutputPath"/>, an invocation that does not produce
    /// it (whatever the exit code) is retried once against the now-warm project, which normally
    /// succeeds. Callers with no single expected artefact (the multi-bundle prefab pass) leave it
    /// null and get a single attempt.
    /// </summary>
    internal static async Task<UnityBundleInvocationResult> InvokeWithRetryAsync(
        UnityBundleInvocation invocation,
        Func<Task<UnityBundleInvocationResult>> runOnce)
    {
        var maxAttempts = invocation.ExpectedOutputPath is null ? 1 : 2;
        UnityBundleInvocationResult result = default!;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result = await runOnce();

            var producedOutput = invocation.ExpectedOutputPath is null
                || File.Exists(invocation.ExpectedOutputPath);
            if (result.Success && producedOutput)
                return result;

            if (attempt < maxAttempts)
                invocation.Log?.Warning(
                    $"  Unity build attempt {attempt} produced no bundle at {invocation.ExpectedOutputPath}. Retrying once against the now-imported project.");
        }

        return result;
    }

    private static async Task<UnityBundleInvocationResult> RunOnceAsync(UnityBundleInvocation invocation, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = invocation.UnityEditor,
                Arguments = arguments,
                UseShellExecute = false,
            }
        };

        process.Start();
        await process.WaitForExitAsync();

        var logTailLines = Array.Empty<string>();
        if (File.Exists(invocation.LogFile))
        {
            var allLines = await File.ReadAllLinesAsync(invocation.LogFile);
            logTailLines = allLines.Skip(Math.Max(0, allLines.Length - 20)).ToArray();
        }

        return new UnityBundleInvocationResult(process.ExitCode, logTailLines);
    }
}

/// <summary>
/// Description of one Unity batchmode build invocation. Fixed flags
/// (<c>-batchmode -nographics -quit -buildTarget StandaloneWindows64</c>)
/// are supplied by <see cref="UnityBundleInvoker"/>.
/// </summary>
public sealed class UnityBundleInvocation
{
    public required string UnityEditor { get; init; }
    public required string ProjectPath { get; init; }
    public required string ExecuteMethod { get; init; }
    public required string LogFile { get; init; }
    /// <summary>
    /// Extra <c>-name value</c> pairs appended after the fixed header.
    /// Values with spaces or path separators are quoted automatically;
    /// bare tokens (like a bundle name) pass through unquoted.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraArgs { get; init; } = [];

    /// <summary>
    /// The single bundle file this invocation is expected to write. When set, an invocation
    /// that exits without producing it is treated as a failed attempt and retried once (the
    /// cold-project batchmode race). Leave null when the build emits many bundles or none, in
    /// which case the invocation runs exactly once regardless of what lands on disk.
    /// </summary>
    public string? ExpectedOutputPath { get; init; }

    /// <summary>Optional sink for the cold-project retry notice. No output when null.</summary>
    public ILogSink? Log { get; init; }
}

public sealed record UnityBundleInvocationResult(int ExitCode, IReadOnlyList<string> LogTailLines)
{
    public bool Success => ExitCode == 0;
}
