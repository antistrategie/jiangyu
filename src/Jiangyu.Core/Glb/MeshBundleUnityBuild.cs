using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Glb;

/// <summary>
/// Unity-batchmode phase of the mesh bundle build: copy modder-supplied
/// audio/sprite source files into the per-build staging tree under
/// <c>unity/Assets/Jiangyu/Staging/MeshReplacement/</c>, then invoke Unity
/// against the project so it bakes the bundle.
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

        var audioDir = Path.Combine(stagingRoot, "Audio");
        if (Directory.Exists(audioDir))
            Directory.Delete(audioDir, recursive: true);

        var spriteSourceDir = Path.Combine(stagingRoot, "SpriteSources");
        if (Directory.Exists(spriteSourceDir))
            Directory.Delete(spriteSourceDir, recursive: true);
        var spriteAdditionDir = Path.Combine(stagingRoot, "SpriteAdditions");
        if (Directory.Exists(spriteAdditionDir))
            Directory.Delete(spriteAdditionDir, recursive: true);

        if (directAudioAssets.Count == 0 && directSprites.Count == 0)
            return Task.CompletedTask;

        if (directAudioAssets.Count > 0)
            Directory.CreateDirectory(audioDir);
        foreach (var asset in directAudioAssets)
        {
            var extension = string.IsNullOrWhiteSpace(asset.Extension) ? string.Empty : asset.Extension;
            if (!string.IsNullOrEmpty(extension) && !extension.StartsWith('.'))
                extension = $".{extension}";

            var destinationPath = Path.Combine(audioDir, $"{asset.Name}{extension}");
            File.Copy(asset.SourceFilePath, destinationPath, overwrite: true);
        }

        var anyReplacementSprite = directSprites.Any(s => !s.IsAddition);
        var anyAdditionSprite = directSprites.Any(s => s.IsAddition);
        if (anyReplacementSprite)
            Directory.CreateDirectory(spriteSourceDir);
        if (anyAdditionSprite)
            Directory.CreateDirectory(spriteAdditionDir);

        foreach (var asset in directSprites)
        {
            var extension = string.IsNullOrWhiteSpace(asset.Extension) ? string.Empty : asset.Extension;
            if (!string.IsNullOrEmpty(extension) && !extension.StartsWith('.'))
                extension = $".{extension}";

            var targetDir = asset.IsAddition ? spriteAdditionDir : spriteSourceDir;
            var destinationName = asset.IsAddition ? asset.Name : asset.StagingName;
            var destinationPath = Path.Combine(targetDir, $"{destinationName}{extension}");
            File.Copy(asset.SourceFilePath, destinationPath, overwrite: true);
        }

        return Task.CompletedTask;
    }

    public static async Task InvokeUnityBuildAsync(
        string unityEditor,
        string userUnityDir,
        string outputBundlePath,
        string bundleName,
        string meshDataPath,
        string textureDataPath,
        string diagnosticsPath,
        string? meshContractPath,
        bool runPrefabs = false)
    {
        var modRoot = Path.GetFullPath(Path.Combine(userUnityDir, ".."));
        var logFile = Path.Combine(modRoot, ".jiangyu", "unity_build_mesh.log");

        var extra = new List<KeyValuePair<string, string>>
        {
            new("meshDataPath", meshDataPath),
            new("textureDataPath", textureDataPath),
            new("outputPath", outputBundlePath),
            new("diagnosticsPath", diagnosticsPath),
            new("bundleName", bundleName),
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
        });

        if (result.Success)
            return;

        var logTail = string.Join(Environment.NewLine, result.LogTailLines);
        throw new InvalidOperationException(
            $"Unity mesh build failed (exit code {result.ExitCode}). Log: {logFile}{Environment.NewLine}{logTail}".Trim());
    }
}
