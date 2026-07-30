using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Glb;

/// <summary>
/// Unity-batchmode phase of the mesh bundle build: mirror modder-supplied
/// audio/sprite source files into the staging tree under
/// <c>unity/Assets/Jiangyu/Staging/MeshReplacement/</c>, then invoke Unity
/// against the project so it bakes the bundle.
///
/// <para>Staging is a content-stable sync, never a wipe and re-copy. The
/// <c>.meta</c> files Unity writes beside staged files carry the GUIDs its
/// import cache is keyed by, so recreating an unchanged file forces a full
/// re-import of every staged clip and sprite on each build. Rewriting only
/// the files whose content changed keeps GUIDs stable and lets the import
/// cache absorb the rest. Change detection is a recorded content hash per
/// staged file (<c>.jiangyu/staged_inputs</c>), never file times: an export
/// pipeline that preserves timestamps must still restage.</para>
///
/// <para>The staging split (SpriteSources vs SpriteAdditions) matters: the
/// replacement-sprite path stages through <c>SpriteSources/</c> and goes
/// through the runtime-created Texture2D path in the Unity-side builder so
/// alpha survives the in-place mutation; addition sprites stage through
/// <c>SpriteAdditions/</c> and use Unity's standard TextureImporter path so
/// the bundle serialiser produces correct internal PPtrs. Mixing the two
/// would leave addition sprites pointing at unresolvable fileIDs and the
/// bundle would alias the slot to whatever asset shares it at runtime.</para>
/// </summary>
internal static class MeshBundleUnityBuild
{
    public static Task StageReplacementAssetsAsync(
        string userUnityDir,
        IReadOnlyList<GlbMeshBundleCompiler.ImportedSpriteAsset> directSprites,
        IReadOnlyList<GlbMeshBundleCompiler.ImportedAudioAsset> directAudioAssets)
    {
        var stagingRoot = Path.Combine(userUnityDir, "Assets", "Jiangyu", "Staging", "MeshReplacement");
        var statePath = Path.Combine(Path.GetFullPath(Path.Combine(userUnityDir, "..")), ".jiangyu", "staged_inputs");
        var recordedHashes = LoadStagedInputState(statePath);
        var currentHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        SyncStagingDirectory(
            Path.Combine(stagingRoot, "Audio"), "Audio",
            directAudioAssets.Select(a => (a.SourceFilePath, StagedFileName(a.Name, a.Extension))),
            recordedHashes, currentHashes);
        SyncStagingDirectory(
            Path.Combine(stagingRoot, "SpriteSources"), "SpriteSources",
            directSprites.Where(s => !s.IsAddition).Select(s => (s.SourceFilePath, StagedFileName(s.StagingName, s.Extension))),
            recordedHashes, currentHashes);
        SyncStagingDirectory(
            Path.Combine(stagingRoot, "SpriteAdditions"), "SpriteAdditions",
            directSprites.Where(s => s.IsAddition).Select(s => (s.SourceFilePath, StagedFileName(s.Name, s.Extension))),
            recordedHashes, currentHashes);

        SaveStagedInputState(statePath, currentHashes);
        return Task.CompletedTask;
    }

    private static string StagedFileName(string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return name;
        return extension.StartsWith('.') ? $"{name}{extension}" : $"{name}.{extension}";
    }

    // Mirror the expected files into one staging directory: copy entries whose source
    // content hash differs from the recorded one, delete entries with no surviving
    // source, and leave everything else (files and their .meta GUIDs) untouched. The
    // decision is a content hash, not file times: the hash reads the same bytes the
    // asset fingerprint already pulled through the page cache, and times can lie (a
    // timestamp-preserving export changes content without moving the mtime).
    private static void SyncStagingDirectory(
        string directory,
        string stateKeyPrefix,
        IEnumerable<(string SourcePath, string FileName)> expected,
        IReadOnlyDictionary<string, string> recordedHashes,
        Dictionary<string, string> currentHashes)
    {
        var expectedByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sourcePath, fileName) in expected)
            expectedByName[fileName] = sourcePath;

        if (expectedByName.Count == 0)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            var directoryMeta = $"{directory}.meta";
            if (File.Exists(directoryMeta))
                File.Delete(directoryMeta);
            return;
        }

        Directory.CreateDirectory(directory);

        foreach (var existing in Directory.GetFiles(directory))
        {
            if (existing.EndsWith(".meta", StringComparison.Ordinal))
                continue;
            if (expectedByName.ContainsKey(Path.GetFileName(existing)))
                continue;
            File.Delete(existing);
        }

        foreach (var (fileName, sourcePath) in expectedByName)
        {
            var destinationPath = Path.Combine(directory, fileName);
            var stateKey = $"{stateKeyPrefix}/{fileName}";
            var sourceHash = FileFingerprint.OfFile(sourcePath);
            currentHashes[stateKey] = sourceHash;
            if (File.Exists(destinationPath)
                && recordedHashes.TryGetValue(stateKey, out var recorded)
                && recorded == sourceHash)
                continue;
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        // A meta whose asset no longer exists would draw an orphan warning from Unity.
        // Swept after the copies so a meta belonging to a file restaged this run (its GUID,
        // and with it any references) survives.
        foreach (var meta in Directory.GetFiles(directory, "*.meta"))
        {
            var assetPath = meta[..^".meta".Length];
            if (!File.Exists(assetPath))
                File.Delete(meta);
        }
    }

    private static Dictionary<string, string> LoadStagedInputState(string path)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return state;
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.LastIndexOf('\t');
            if (separator > 0)
                state[line[..separator]] = line[(separator + 1)..];
        }
        return state;
    }

    private static void SaveStagedInputState(string path, Dictionary<string, string> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, state
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}\t{kvp.Value}"));
    }

    public static async Task InvokeUnityBuildAsync(
        string unityEditor,
        string userUnityDir,
        string outputBundlePath,
        string bundleName,
        string meshDataPath,
        string textureDataPath,
        string diagnosticsPath,
        string bundlePlanPath,
        IReadOnlyList<string> plannedOutputPaths,
        string? meshContractPath,
        bool runPrefabs = false,
        ILogSink? log = null)
    {
        var modRoot = Path.GetFullPath(Path.Combine(userUnityDir, ".."));
        var logFile = Path.Combine(modRoot, ".jiangyu", "unity_build_mesh.log");
        // The planned bundle files persist across compiles (they ARE the incremental
        // state), so their existence proves nothing about THIS run. The build script
        // writes this token to the marker as its last act after verifying its outputs;
        // a fresh marker with the right token is the only acceptable success signal.
        var markerPath = Path.Combine(modRoot, ".jiangyu", "unity_build_mesh.done");
        var completionToken = Guid.NewGuid().ToString("N");
        if (File.Exists(markerPath))
            File.Delete(markerPath);

        var extra = new List<KeyValuePair<string, string>>
        {
            new("meshDataPath", meshDataPath),
            new("textureDataPath", textureDataPath),
            new("outputPath", outputBundlePath),
            new("diagnosticsPath", diagnosticsPath),
            new("bundleName", bundleName),
            new("bundlePlanPath", bundlePlanPath),
            new("completionToken", completionToken),
        };
        if (!string.IsNullOrEmpty(meshContractPath))
            extra.Add(new("meshContractPath", meshContractPath));
        // When set, BuildMeshReplacementBundle runs BuildBundles.RunCore()
        // first in the same Unity batchmode session. CompilationService
        // turns this on when a mod has both addition-prefab work and
        // replacement-asset work, saving one Unity cold start.
        if (runPrefabs)
            extra.Add(new("runPrefabs", "true"));

        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = unityEditor,
            ProjectPath = userUnityDir,
            ExecuteMethod = "Jiangyu.Mod.BuildMeshReplacementBundle.BuildAll",
            LogFile = logFile,
            ExtraArgs = extra,
            // Retry a cold-project run before the caller (a second Unity pass, or the
            // mesh-contract extractor) depends on the outputs. The marker is deleted
            // above, so unlike the bundle files it cannot be satisfied by a previous run.
            ExpectedOutputPath = markerPath,
            Log = log,
        });

        var completed = File.Exists(markerPath)
            && string.Equals(File.ReadAllText(markerPath).Trim(), completionToken, StringComparison.Ordinal);
        var missing = plannedOutputPaths.Where(path => !File.Exists(path)).ToList();
        if (result.Success && completed && missing.Count == 0)
            return;

        var logTail = string.Join(Environment.NewLine, result.LogTailLines);
        var reason = !result.Success
            ? $"failed (exit code {result.ExitCode})"
            : !completed
                ? "exited successfully but never reached the end of the build script (no completion marker)"
                : $"exited successfully but did not write [{string.Join(", ", missing.Select(Path.GetFileName))}]";
        throw new InvalidOperationException(
            $"Unity mesh build {reason}, even after a cold-project retry. Log: {logFile}{Environment.NewLine}{logTail}".Trim());
    }
}
