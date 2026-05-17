using System.CommandLine;
using System.Diagnostics;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Config;
using Jiangyu.Core.Unity;

namespace Jiangyu.Cli.Commands;

public static class UnityCommand
{
    public static Command Create()
    {
        var command = new Command("unity", "Manage the per-mod Unity Editor project (unity/)");
        command.Add(CreateSync());
        command.Add(CreateOpen());
        command.Add(CreateImportPrefab());
        return command;
    }

    private static Command CreateSync()
    {
        var command = new Command("sync", "Scaffold or refresh unity/ for prefab authoring. Idempotent: creates the project on first run, refreshes Jiangyu-managed files on re-run, preserves modder content.");
        command.SetAction((parseResult) =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            try
            {
                var log = new ConsoleLogSink();
                var scaffolder = new UnityProjectScaffolder(log);
                var result = scaffolder.Init(projectDir);

                Console.WriteLine();
                Console.WriteLine($"Created    {result.CreatedFiles.Count} file(s)");
                Console.WriteLine($"Updated    {result.OverwrittenFiles.Count} file(s)");
                Console.WriteLine($"Preserved  {result.PreservedFiles.Count} file(s)");
                Console.WriteLine();
                Console.WriteLine("Next steps:");
                Console.WriteLine("  1. Open the project at unity/ in the same Unity Editor version as the game.");
                Console.WriteLine("  2. Author prefabs under Assets/Prefabs/.");
                Console.WriteLine("  3. Run 'mise compile' (or 'jiangyu compile') to build them into addition bundles.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: unity sync failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }

    private static Command CreateOpen()
    {
        var command = new Command("open", "Launch Unity Editor on the mod's unity/ project.");
        command.SetAction((parseResult) =>
        {
            var projectDir = Directory.GetCurrentDirectory();
            var unityDir = Path.Combine(projectDir, "unity");

            if (!Directory.Exists(unityDir))
            {
                Console.Error.WriteLine($"error: no unity/ project at {unityDir}. Run 'jiangyu unity sync' first.");
                return 1;
            }

            var config = GlobalConfig.Load();

            // Detect the game's Unity version so editor discovery picks the
            // matching install when multiple Unity versions are present. Falls
            // back to the global config's explicit `unityEditor` when set; the
            // path-based discovery only kicks in for the auto-discover case.
            string? preferredVersion = null;
            var (gameDataPath, _) = GlobalConfig.ResolveGameDataPath(config);
            if (gameDataPath is not null)
            {
                try
                {
                    var gameVersion = Jiangyu.Core.Unity.UnityVersionValidationService.DetectGameVersion(gameDataPath);
                    preferredVersion = gameVersion?.ToString();
                }
                catch
                {
                    // Best-effort: if game-data probe fails, fall through to
                    // unconstrained discovery.
                }
            }

            var (editorPath, editorError) = GlobalConfig.ResolveUnityEditorPath(config, preferredVersion);
            if (editorPath is null)
            {
                Console.Error.WriteLine($"error: could not resolve Unity Editor: {editorError}");
                return 1;
            }

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = editorPath,
                        Arguments = $"-projectPath \"{unityDir}\"",
                        UseShellExecute = false,
                    }
                };
                process.Start();
                Console.WriteLine($"Launched Unity Editor on {unityDir} (pid {process.Id}).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: failed to launch Unity: {ex.Message}");
                return 1;
            }
        });
        return command;
    }

    private static Command CreateImportPrefab()
    {
        var nameArg = new Argument<string>("name") { Description = "Asset name to import (resolved via the asset index)." };
        var pathIdOption = new Option<long?>("--path-id") { Description = "Asset path ID (from `assets search`). Required when the name is ambiguous." };
        var collectionOption = new Option<string?>("--collection") { Description = "Asset collection (from `assets search`). Optional narrowing filter." };

        var command = new Command("import-prefab", "Extract a vanilla game prefab into unity/Assets/Imported/<name>/. Auto-bootstraps unity/ if missing.")
        {
            nameArg,
            pathIdOption,
            collectionOption,
        };

        command.SetAction((parseResult) =>
        {
            var name = parseResult.GetValue(nameArg);
            var pathId = parseResult.GetValue(pathIdOption);
            var collection = parseResult.GetValue(collectionOption);

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("error: <name> is required.");
                return 1;
            }

            var projectDir = Directory.GetCurrentDirectory();
            var log = new ConsoleLogSink();

            try
            {
                // Auto-bootstrap unity/ so the modder can run import-prefab as
                // their first unity-namespace command.
                if (!Directory.Exists(Path.Combine(projectDir, "unity")))
                {
                    log.Info("unity/ not present; bootstrapping...");
                    new UnityProjectScaffolder(log).Init(projectDir);
                }

                var config = GlobalConfig.Load();
                var (gameDataPath, gameDataError) = GlobalConfig.ResolveGameDataPath(config);
                if (gameDataPath is null)
                {
                    Console.Error.WriteLine($"error: could not resolve game data path: {gameDataError}");
                    return 1;
                }

                var pipeline = new Core.Assets.AssetPipelineService(
                    gameDataPath,
                    config.GetCachePath(),
                    Core.Abstractions.NullProgressSink.Instance,
                    log);

                var destDir = Path.Combine(projectDir, "unity", "Assets", "Imported", name);
                pipeline.ImportPrefabAsUnityAssets(
                    assetName: name,
                    destDir: destDir,
                    collection: collection,
                    pathId: pathId ?? -1);

                Console.WriteLine();
                Console.WriteLine($"Imported prefab to: {destDir}");
                Console.WriteLine("Open unity/ in Unity Editor; the prefab will appear under Assets/Imported/" + name + "/.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: import-prefab failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private sealed class ConsoleLogSink : ILogSink
    {
        public void Info(string message) => Console.WriteLine(message);
        public void Warning(string message) => Console.WriteLine($"warning: {message}");
        public void Error(string message) => Console.Error.WriteLine($"error: {message}");
    }

}
