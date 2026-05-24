using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Bundles;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Glb;

public static class GlbMeshBundleCompiler
{


    public sealed class MeshSourceEntry
    {
        public required string SourceFilePath { get; init; }
        public required string BundleMeshName { get; init; }
        public string? SourceMeshName { get; init; }
        public required string TargetMeshName { get; init; }
        public required string TargetRendererPath { get; init; }
        public required bool HasExplicitMeshName { get; init; }
        public string? SourceReference { get; init; }
        public string? BindPoseReferencePath { get; init; }
        public string? TargetEntityName { get; init; }
        public bool SuppressMeshContract { get; init; }
        // Max half-extent of the vanilla game target mesh's local AABB. Used to derive
        // authored-vs-target scale ratio, which drives the vertex-space decision:
        // authored-scale ≈ target-scale → pass through; authored-scale ≈ target-scale × 0.01
        // → apply 100× scale-up. Zero indicates the target extent is unknown and the
        // caller should fall back to the isCleaned flag.
        public float TargetMeshMaxHalfExtent { get; init; }
    }

    internal sealed class CompiledMesh
    {
        public required string Name { get; init; }
        public required float[] Vertices { get; init; }
        public required float[] Normals { get; init; }
        public required float[] Tangents { get; init; }
        public required float[] UV0 { get; init; }
        public required float[] UV1 { get; init; }
        public required float[] Colors { get; init; }
        public required int[] Indices { get; init; }
        public required List<SubMeshInfo> SubMeshes { get; init; }
        public required float[] BoneWeights { get; init; }
        public required int[] BoneIndices { get; init; }
        public required Matrix4x4[] BindPoses { get; init; }
        public required string[] BoneNames { get; init; }
    }

    internal sealed class CompanionSkinData
    {
        public required Matrix4x4[] BindPoses { get; init; }
        public required string[] BoneNames { get; init; }
    }


    internal sealed class SubMeshInfo
    {
        public required int IndexStart { get; init; }
        public required int IndexCount { get; init; }
    }

    public sealed class BuildResult
    {
        public required Dictionary<string, string[]> MeshBoneNames { get; init; }
        public required Dictionary<string, List<CompiledMaterialBinding>> MeshMaterialBindings { get; init; }
    }

    internal sealed class MeshBuildContract
    {
        public required string MeshName { get; init; }
        public required uint[] BoneNameHashes { get; init; }
        public required uint RootBoneNameHash { get; init; }
        public required float[][] BindPoses { get; init; }
    }

    /// <summary>
    /// Cached per-source-file context to avoid redundant <see cref="ModelRoot.Load"/> calls.
    /// </summary>


    internal sealed class CompiledTexture
    {
        public required string Name { get; init; }
        public required byte[] Content { get; init; }
        public required bool Linear { get; init; }
    }

    public sealed class ImportedAudioAsset
    {
        public required string Name { get; init; }
        public required string SourceFilePath { get; init; }
        public required string Extension { get; init; }
    }

    public sealed class ImportedSpriteAsset
    {
        public required string Name { get; init; }
        public required string SourceFilePath { get; init; }
        public required string Extension { get; init; }
        public required string StagingName { get; init; }
        public bool IsAddition { get; init; }
    }

    internal static async Task<BuildResult> BuildAsync(
        string unityEditor,
        string unityProjectDir,
        string bundleName,
        string outputBundlePath,
        IReadOnlyList<MeshSourceEntry> entries,
        IReadOnlyList<CompiledTexture> directTextures,
        IReadOnlyList<ImportedSpriteAsset> directSprites,
        IReadOnlyList<ImportedAudioAsset> directAudioAssets,
        string? gameDataPath,
        IReadOnlyDictionary<string, string> targetMeshNamesByBundleMesh,
        bool runPrefabs = false,
        ILogSink? log = null)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var contexts = GlbMeshExtractor.LoadModelContexts(entries);
            log?.Info($"  [timing]   Load models: {sw.Elapsed.TotalSeconds:F1}s ({contexts.Count} file(s))");
            sw.Restart();
            var meshes = GlbMeshExtractor.ExtractMeshes(entries, contexts, out var meshMaterialBindings, log);
            log?.Info($"  [timing]   ExtractMeshes: {sw.Elapsed.TotalSeconds:F1}s ({meshes.Count} mesh(es))");
            sw.Restart();
            var textures = GlbTextureExtractor.ExtractTextures(
                    entries,
                    path => contexts.TryGetValue(path, out var ctx) ? ctx.Model : null)
                .ToDictionary(texture => texture.Name, StringComparer.Ordinal);
            foreach (var texture in directTextures)
                textures[texture.Name] = texture;
            log?.Info($"  [timing]   ExtractTextures: {sw.Elapsed.TotalSeconds:F1}s ({textures.Count} texture(s))");

            if (meshes.Count == 0 && textures.Count == 0 && directSprites.Count == 0 && directAudioAssets.Count == 0)
                throw new InvalidOperationException("No replacement assets were extracted.");

            var modRoot = Path.GetFullPath(Path.Combine(unityProjectDir, ".."));
            var bundleFileName = Path.GetFileName(outputBundlePath);
            var diagnosticsDir = Path.Combine(modRoot, ".jiangyu", "diagnostics");
            var sourceDiagnosticsPath = Path.Combine(diagnosticsDir, bundleFileName + ".source-diagnostics.json");
            MeshBundleStager.WriteSourceDiagnostics(sourceDiagnosticsPath, meshes);

            sw.Restart();
            var glbStagingDir = Path.Combine(modRoot, ".jiangyu", "glb_staging");
            Directory.CreateDirectory(glbStagingDir);
            var meshDataPath = Path.Combine(glbStagingDir, "meshdata.bin");
            var textureDataPath = Path.Combine(glbStagingDir, "texturedata.bin");
            MeshBundleStager.WriteMeshData(meshDataPath, meshes);
            MeshBundleStager.WriteTextureData(textureDataPath, [.. textures.Values.OrderBy(texture => texture.Name, StringComparer.Ordinal)]);
            await MeshBundleUnityBuild.StageReplacementAssetsAsync(unityProjectDir, directSprites, directAudioAssets);
            log?.Info($"  [timing]   Write staging data: {sw.Elapsed.TotalSeconds:F1}s");
            var diagnosticsPath = Path.Combine(diagnosticsDir, bundleFileName + ".unity-diagnostics.json");
            var contractPath = Path.Combine(glbStagingDir, "meshcontract.bin");
            var firstPassOutputPath = outputBundlePath;

            var allTargetMeshNames = entries
                .Select(entry => entry.TargetMeshName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var needsSecondPass = !string.IsNullOrWhiteSpace(gameDataPath) &&
                                  Directory.Exists(gameDataPath) &&
                                  allTargetMeshNames.Length > 0;
            if (needsSecondPass)
                firstPassOutputPath = outputBundlePath + ".pass1";

            sw.Restart();
            await MeshBundleUnityBuild.InvokeUnityBuildAsync(unityEditor, unityProjectDir, firstPassOutputPath, bundleName, meshDataPath, textureDataPath, diagnosticsPath, meshContractPath: null, runPrefabs: runPrefabs);
            log?.Info($"  [timing] Unity pass 1: {sw.Elapsed.TotalSeconds:F1}s{(runPrefabs ? " (with prefab pass)" : "")}");

            if (needsSecondPass)
            {
                sw.Restart();
                var extractedContracts = MeshContractExtractor.Extract(firstPassOutputPath, gameDataPath!, allTargetMeshNames);
                log?.Info($"  [timing] Mesh contract extraction: {sw.Elapsed.TotalSeconds:F1}s");

                // Contract stamping (bone name hashes, bindposes) only fires for non-ambiguous
                // target mesh names — those that identify a single game asset unambiguously.
                var contracts = new List<MeshBuildContract>();
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.TargetMeshName))
                        continue;
                    if (!extractedContracts.TryGetValue(entry.TargetMeshName, out var contract))
                        continue;

                    if (targetMeshNamesByBundleMesh.TryGetValue(entry.BundleMeshName, out var nonAmbiguousTarget) &&
                        string.Equals(nonAmbiguousTarget, entry.TargetMeshName, StringComparison.Ordinal))
                    {
                        contracts.Add(new MeshBuildContract
                        {
                            MeshName = entry.BundleMeshName,
                            BoneNameHashes = contract.BoneNameHashes,
                            RootBoneNameHash = contract.RootBoneNameHash,
                            BindPoses = contract.BindPoses,
                        });
                    }
                }

                if (contracts.Count > 0)
                {
                    MeshBundleStager.WriteMeshContracts(contractPath, contracts);
                    sw.Restart();
                    await MeshBundleUnityBuild.InvokeUnityBuildAsync(unityEditor, unityProjectDir, outputBundlePath, bundleName, meshDataPath, textureDataPath, diagnosticsPath, contractPath);
                    log?.Info($"  [timing] Unity pass 2: {sw.Elapsed.TotalSeconds:F1}s");
                }
                else if (!string.Equals(firstPassOutputPath, outputBundlePath, StringComparison.Ordinal))
                {
                    File.Copy(firstPassOutputPath, outputBundlePath, overwrite: true);
                }
            }

            return new BuildResult
            {
                MeshBoneNames = meshes.ToDictionary(m => m.Name, m => m.BoneNames, StringComparer.Ordinal),
                MeshMaterialBindings = meshMaterialBindings,
            };
        }
        finally
        {
            GlbMeshExtractor.ClearReferenceCache();
        }
    }

}

