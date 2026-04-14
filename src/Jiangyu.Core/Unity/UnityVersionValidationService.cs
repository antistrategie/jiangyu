using System.Diagnostics;
using System.Text.RegularExpressions;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Unity;

public sealed class UnityVersionValidationResult
{
    public required bool Success { get; init; }
    public UnityVersion? GameVersion { get; init; }
    public UnityVersion? EditorVersion { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed partial class UnityVersionValidationService
{
    private readonly ILogSink _log;
    private readonly Func<string, UnityVersion?> _gameVersionDetector;
    private readonly Func<string, Task<UnityVersion?>> _editorVersionDetector;

    public UnityVersionValidationService(ILogSink log)
    {
        _log = log;
        _gameVersionDetector = DetectGameVersion;
        _editorVersionDetector = DetectEditorVersionAsync;
    }

    internal UnityVersionValidationService(
        ILogSink log,
        Func<string, UnityVersion?> gameVersionDetector,
        Func<string, Task<UnityVersion?>> editorVersionDetector)
    {
        _log = log;
        _gameVersionDetector = gameVersionDetector;
        _editorVersionDetector = editorVersionDetector;
    }

    public async Task<UnityVersionValidationResult> ValidateAsync(string gameDataPath, string unityEditorPath)
    {
        UnityVersion? gameVersion = _gameVersionDetector(gameDataPath);
        if (gameVersion is null)
        {
            return Fail($"Could not detect MENACE Unity version from game data at: {gameDataPath}");
        }

        UnityVersion? editorVersion = await _editorVersionDetector(unityEditorPath);
        if (editorVersion is null)
        {
            return Fail(
                $"Could not detect Unity editor version from configured path: {unityEditorPath}",
                gameVersion: gameVersion);
        }

        if (gameVersion != editorVersion)
        {
            return Fail(
                $"Configured Unity editor version does not match MENACE.\n" +
                $"  Game Unity version: {gameVersion}\n" +
                $"  Configured editor version: {editorVersion}\n" +
                $"  Bundles must be built with the matching editor version.",
                gameVersion,
                editorVersion);
        }

        _log.Info($"Game Unity version: {gameVersion}");
        _log.Info($"Configured editor version: {editorVersion}");

        return new UnityVersionValidationResult
        {
            Success = true,
            GameVersion = gameVersion,
            EditorVersion = editorVersion,
        };
    }

    internal static UnityVersion? DetectGameVersion(string gameDataPath)
    {
        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        using var gameStructure = GameStructure.Load([gameDataPath], LocalFileSystem.Instance, settings);
        return gameStructure.PlatformStructure?.Version ?? gameStructure.FileCollection.GetMaxUnityVersion();
    }

    internal async Task<UnityVersion?> DetectEditorVersionAsync(string unityEditorPath)
    {
        if (TryParseUnityVersionFromText(unityEditorPath, out UnityVersion version))
        {
            return version;
        }

        return await TryGetUnityVersionFromProcessAsync(unityEditorPath);
    }

    internal static bool TryParseUnityVersionFromText(string text, out UnityVersion version)
    {
        Match match = UnityVersionRegex().Match(text);
        if (match.Success && TryParseUnityVersion(match.Value, out version))
        {
            return true;
        }

        version = default;
        return false;
    }

    internal static bool TryParseUnityVersion(string text, out UnityVersion version)
    {
        try
        {
            version = UnityVersion.Parse(text);
            return true;
        }
        catch
        {
            version = default;
            return false;
        }
    }

    private static async Task<UnityVersion?> TryGetUnityVersionFromProcessAsync(string unityEditorPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = unityEditorPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            process.Start();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task waitTask = process.WaitForExitAsync();
            Task allTasks = Task.WhenAll(outputTask, errorTask, waitTask);
            Task completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(10)));

            if (completed != allTasks)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                return null;
            }

            string combinedOutput = (await outputTask) + "\n" + (await errorTask);
            return TryParseUnityVersionFromText(combinedOutput, out UnityVersion version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static UnityVersionValidationResult Fail(
        string message,
        UnityVersion? gameVersion = null,
        UnityVersion? editorVersion = null) => new()
        {
            Success = false,
            GameVersion = gameVersion,
            EditorVersion = editorVersion,
            ErrorMessage = message,
        };

    [GeneratedRegex(@"\b\d{4,}\.\d+\.\d+[a-z]\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnityVersionRegex();
}
