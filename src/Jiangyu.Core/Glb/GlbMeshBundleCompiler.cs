using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Glb;

public static class GlbMeshBundleCompiler
{
    private const float MenaceCharacterSpaceScale = 100f;
    private const float MeshOnlyWeightedVertexScale = 0.01f;
    private const float MeshOnlyWeightScaleExtentThreshold = 10f;
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    internal sealed class ExtractedMeshCandidate
    {
        public required string PrimaryName { get; init; }
        public required HashSet<string> Aliases { get; init; }
        public required Node Node { get; init; }
        public required CompiledMesh Mesh { get; init; }
        public required List<CompiledMaterialBinding> MaterialBindings { get; init; }
    }

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
        public long? TargetEntityPathId { get; init; }
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

    private sealed class SkinBindingData
    {
        public required Matrix4x4[] SourceBindPoses { get; init; }
        public required Matrix4x4[] CompiledBindPoses { get; init; }
        public required string[] BoneNames { get; init; }
        public required string?[] ParentNames { get; init; }
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

    private sealed class MeshBuildContract
    {
        public required string MeshName { get; init; }
        public required uint[] BoneNameHashes { get; init; }
        public required uint RootBoneNameHash { get; init; }
        public required float[][] BindPoses { get; init; }
    }

    // Jiangyu-owned binary format markers for mesh/texture staging data written to disk
    // before the Unity build step. These are not discovered MENACE contract values.
    private const uint Magic = 0x4D455348; // "MESH"
    private const uint TextureMagic = 0x54585452; // "TXTR"

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
        IReadOnlyDictionary<string, string> targetMeshNamesByBundleMesh)
    {
        var meshes = ExtractMeshes(entries);
        var textures = ExtractTextures(entries)
            .ToDictionary(texture => texture.Name, StringComparer.Ordinal);
        foreach (var texture in directTextures)
            textures[texture.Name] = texture;

        if (meshes.Count == 0 && textures.Count == 0 && directSprites.Count == 0 && directAudioAssets.Count == 0)
            throw new InvalidOperationException("No replacement assets were extracted.");

        var sourceDiagnosticsPath = outputBundlePath + ".source-diagnostics.json";
        WriteSourceDiagnostics(sourceDiagnosticsPath, meshes);

        var meshDataPath = Path.Combine(unityProjectDir, "meshdata.bin");
        var textureDataPath = Path.Combine(unityProjectDir, "texturedata.bin");
        WriteMeshData(meshDataPath, meshes);
        WriteTextureData(textureDataPath, [.. textures.Values.OrderBy(texture => texture.Name, StringComparer.Ordinal)]);
        await SetupUnityProjectAsync(unityProjectDir, directSprites, directAudioAssets);
        var diagnosticsPath = outputBundlePath + ".unity-diagnostics.json";
        var contractPath = Path.Combine(unityProjectDir, "meshcontract.bin");
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

        await InvokeUnityBuildAsync(unityEditor, unityProjectDir, firstPassOutputPath, bundleName, meshDataPath, textureDataPath, diagnosticsPath, meshContractPath: null);

        if (needsSecondPass)
        {
            var extractedContracts = MeshContractExtractor.Extract(firstPassOutputPath, gameDataPath!, allTargetMeshNames);

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
                WriteMeshContracts(contractPath, contracts);
                await InvokeUnityBuildAsync(unityEditor, unityProjectDir, outputBundlePath, bundleName, meshDataPath, textureDataPath, diagnosticsPath, contractPath);
            }
            else if (!string.Equals(firstPassOutputPath, outputBundlePath, StringComparison.Ordinal))
            {
                File.Copy(firstPassOutputPath, outputBundlePath, overwrite: true);
            }
        }

        return new BuildResult
        {
            MeshBoneNames = meshes.ToDictionary(m => m.Name, m => m.BoneNames, StringComparer.Ordinal),
            MeshMaterialBindings = BuildMaterialBindings(entries),
        };
    }

    private static List<CompiledMesh> ExtractMeshes(IReadOnlyList<MeshSourceEntry> entries)
    {
        var extracted = new List<CompiledMesh>();
        var bySource = entries
            .GroupBy(e => e.SourceFilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in bySource)
        {
            var model = ModelRoot.Load(group.Key);
            var companionSkinData = TryLoadCompanionSkinData(group.Key);
            var isCleaned = IsCleanedExport(group.Key);
            var extractedCandidates = model.LogicalNodes
                .Where(node => node.Mesh != null)
                .Select(node => ExtractMeshCandidate(node, model, companionSkinData, isCleaned))
                .Where(candidate => candidate != null)
                .Cast<ExtractedMeshCandidate>()
                .ToList();

            if (extractedCandidates.Count == 0)
            {
                var available = string.Join(", ", model.LogicalMeshes.Select(m => m.Name ?? $"Mesh_{m.LogicalIndex}").OrderBy(n => n, StringComparer.Ordinal));
                throw new InvalidOperationException(
                    $"No extractable mesh nodes were found in '{group.Key}'. Available logical meshes: {available}");
            }

            foreach (var entry in group)
            {
                var sourceMeshName = entry.SourceMeshName ?? entry.BundleMeshName;
                var match = extractedCandidates.FirstOrDefault(candidate => candidate.Aliases.Contains(sourceMeshName));
                if (match == null && extractedCandidates.Count == 1)
                    match = extractedCandidates[0];

                if (match == null)
                {
                    var available = string.Join(", ", extractedCandidates.SelectMany(c => c.Aliases).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal));
                    throw new InvalidOperationException(
                        $"No matching mesh instance was found in '{group.Key}' for '{sourceMeshName}'. Available names: {available}");
                }

                var mesh = match.Node.Mesh ?? throw new InvalidOperationException("Matched mesh candidate has no mesh.");
                var compiled = ExtractMesh(
                    mesh,
                    model,
                    match.Node,
                    companionSkinData,
                    isCleaned,
                    entry.BindPoseReferencePath,
                    entry.BundleMeshName,
                    entry.TargetRendererPath,
                    entry.TargetMeshMaxHalfExtent)
                    ?? throw new InvalidOperationException($"Matched mesh '{entry.BundleMeshName}' in '{group.Key}' could not be extracted.");

                extracted.Add(CloneMeshWithName(compiled, entry.BundleMeshName));
            }
        }

        return extracted;
    }

    internal static Dictionary<string, List<CompiledMaterialBinding>> BuildMaterialBindings(IReadOnlyList<MeshSourceEntry> entries)
    {
        var result = new Dictionary<string, List<CompiledMaterialBinding>>(StringComparer.Ordinal);
        var bySource = entries
            .GroupBy(e => e.SourceFilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in bySource)
        {
            var model = ModelRoot.Load(group.Key);
            var isCleaned = IsCleanedExport(group.Key);
            var extractedCandidates = model.LogicalNodes
                .Where(node => node.Mesh != null)
                .Select(node => ExtractMeshCandidate(node, model, companionSkinData: null, isCleaned))
                .Where(candidate => candidate != null)
                .Cast<ExtractedMeshCandidate>()
                .ToList();

            foreach (var entry in group)
            {
                var sourceMeshName = entry.SourceMeshName ?? entry.BundleMeshName;
                var match = extractedCandidates.FirstOrDefault(candidate => candidate.Aliases.Contains(sourceMeshName));
                if (match == null && extractedCandidates.Count == 1)
                    match = extractedCandidates[0];

                result[entry.BundleMeshName] = match?.MaterialBindings ?? [];
            }
        }

        return result;
    }

    private static ExtractedMeshCandidate? ExtractMeshCandidate(Node node, ModelRoot model, CompanionSkinData? companionSkinData, bool isCleaned)
    {
        var mesh = node.Mesh;
        if (mesh == null)
            return null;

        var compiled = ExtractMesh(mesh, model, node, companionSkinData, isCleaned, bindPoseReferencePath: null, bundleMeshName: mesh.Name ?? $"Mesh_{mesh.LogicalIndex}");
        if (compiled == null)
            return null;

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var nodeName = node.Name;
        var meshName = mesh.Name;
        var fallbackName = $"Mesh_{mesh.LogicalIndex}";
        foreach (var pathAlias in GetNodePathAliases(node))
            aliases.Add(pathAlias);

        if (!string.IsNullOrWhiteSpace(nodeName))
            aliases.Add(nodeName);
        if (!string.IsNullOrWhiteSpace(meshName))
            aliases.Add(meshName);
        aliases.Add(fallbackName);

        var primaryName = !string.IsNullOrWhiteSpace(nodeName)
            ? nodeName
            : !string.IsNullOrWhiteSpace(meshName)
                ? meshName
                : fallbackName;

        return new ExtractedMeshCandidate
        {
            PrimaryName = primaryName,
            Aliases = aliases,
            Node = node,
            Mesh = CloneMeshWithName(compiled, primaryName),
            MaterialBindings = ExtractMaterialBindings(node),
        };
    }

    private static IEnumerable<string> GetNodePathAliases(Node node)
    {
        var withRoot = BuildNodePath(node, includeRoot: true);
        if (!string.IsNullOrWhiteSpace(withRoot))
            yield return withRoot;

        var withoutRoot = BuildNodePath(node, includeRoot: false);
        if (!string.IsNullOrWhiteSpace(withoutRoot))
            yield return withoutRoot;
    }

    private static string BuildNodePath(Node node, bool includeRoot)
    {
        var segments = new List<string>();
        var current = node;

        while (current != null)
        {
            if (current.VisualParent == null && !includeRoot)
                break;

            if (string.IsNullOrWhiteSpace(current.Name))
                return string.Empty;

            segments.Add(current.Name);
            current = current.VisualParent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static CompiledMesh CloneMeshWithName(CompiledMesh mesh, string name)
    {
        return new CompiledMesh
        {
            Name = name,
            Vertices = mesh.Vertices,
            Normals = mesh.Normals,
            Tangents = mesh.Tangents,
            UV0 = mesh.UV0,
            UV1 = mesh.UV1,
            Colors = mesh.Colors,
            Indices = mesh.Indices,
            SubMeshes = mesh.SubMeshes,
            BoneWeights = mesh.BoneWeights,
            BoneIndices = mesh.BoneIndices,
            BindPoses = mesh.BindPoses,
            BoneNames = mesh.BoneNames,
        };
    }

    private static List<CompiledMaterialBinding> ExtractMaterialBindings(Node node)
    {
        var mesh = node.Mesh;
        if (mesh == null)
            return [];

        var bindings = new List<CompiledMaterialBinding>();
        for (int slot = 0; slot < mesh.Primitives.Count; slot++)
        {
            var primitive = mesh.Primitives[slot];
            var material = primitive.Material;
            if (material == null)
                continue;

            var textures = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var channel in material.Channels)
            {
                var propertyName = TryMapStandardMaterialProperty(channel.Key);
                var image = channel.Texture?.PrimaryImage;
                if (propertyName == null || image == null)
                    continue;

                textures[propertyName] = GetImageName(image);
            }

            if (material.Extras is System.Text.Json.Nodes.JsonObject matExtras &&
                matExtras.TryGetPropertyValue("jiangyu", out var jiangyuNode) &&
                jiangyuNode is System.Text.Json.Nodes.JsonObject jiangyuObj &&
                jiangyuObj.TryGetPropertyValue("textures", out var texturesNode) &&
                texturesNode is System.Text.Json.Nodes.JsonObject texturesObj)
            {
                foreach (var kvp in texturesObj)
                {
                    var propertyName = kvp.Key;
                    var relativePath = kvp.Value?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(propertyName) || string.IsNullOrWhiteSpace(relativePath))
                        continue;

                    textures[propertyName] = Path.GetFileNameWithoutExtension(relativePath);
                }
            }

            if (textures.Count == 0)
                continue;

            bindings.Add(new CompiledMaterialBinding
            {
                Slot = slot,
                Name = string.IsNullOrWhiteSpace(material.Name) ? null : material.Name,
                Textures = textures,
            });
        }

        return bindings;
    }

    private static string? TryMapStandardMaterialProperty(string channelKey)
    {
        return channelKey switch
        {
            "BaseColor" => "_BaseColorMap",
            "Normal" => "_NormalMap",
            _ => null,
        };
    }

    /// <summary>
    /// How vertices should be transformed from glTF source space into MENACE's native coordinate system.
    /// </summary>
    internal enum VertexSpaceMode
    {
        /// <summary>
        /// Raw full-prefab GLB with direct skin binding. Vertices are already in MENACE cm-space.
        /// Transform: mirror X only.
        /// </summary>
        RawPrefabSkinned,

        /// <summary>
        /// Cleaned .gltf export or mesh-only GLB. Vertices are in metre-space.
        /// Transform: mirror X + 100x scale to reach MENACE cm-space.
        /// </summary>
        CleanedSkinned,

        /// <summary>
        /// Static mesh (no skinning). Standard glTF-to-Unity coordinate flip.
        /// Transform: mirror Z only.
        /// </summary>
        StaticMesh,
    }

    /// <summary>
    /// JIANGYU-CONTRACT: authored skinned replacements stay in metre-space even when
    /// third-party tools (Blender, etc.) strip the Jiangyu <c>extras.jiangyu.cleaned</c>
    /// flag and export as either .gltf or .glb. Distinguish authored metre-space
    /// sources from raw AssetRipper centimeter-space prefab dumps by inspected mesh
    /// extents, not file extension. Validated against Blender round-trips of Jiangyu
    /// character exports.
    /// Scope: proven skinned-model replacement path for authored character swaps.
    /// </summary>
    internal static VertexSpaceMode DetermineVertexSpaceMode(
        bool isSkinnedPath,
        bool isCleaned,
        bool hasDirectSkinBinding,
        bool appearsCentimeterScale,
        float authoredMaxHalfExtent,
        float targetMaxHalfExtent)
    {
        if (!isSkinnedPath)
            return VertexSpaceMode.StaticMesh;

        // When both the vanilla target mesh's half-extent and the authored source
        // mesh's half-extent are known, derive vertex space from their ratio rather
        // than relying on naming heuristics. The game's internal scale for a given
        // mesh asset is authoritative — if authored-scale ≈ target-scale, the
        // replacement is already in the same space as the target and must pass
        // through untouched; if authored-scale ≈ target-scale × 0.01, the source
        // is in metre-space and needs the 100× scale-up to reach the target's space.
        if (targetMaxHalfExtent > 1e-3f && authoredMaxHalfExtent > 1e-6f)
        {
            var ratio = targetMaxHalfExtent / authoredMaxHalfExtent;
            const float sameScaleMin = 0.5f;
            const float sameScaleMax = 2f;
            const float centimetreToMetreMin = 50f;
            const float centimetreToMetreMax = 200f;

            if (ratio >= sameScaleMin && ratio <= sameScaleMax)
                return VertexSpaceMode.RawPrefabSkinned;
            if (ratio >= centimetreToMetreMin && ratio <= centimetreToMetreMax)
                return VertexSpaceMode.CleanedSkinned;
        }

        if (isCleaned)
            return VertexSpaceMode.CleanedSkinned;

        if (!hasDirectSkinBinding)
            return VertexSpaceMode.CleanedSkinned;

        return appearsCentimeterScale
            ? VertexSpaceMode.RawPrefabSkinned
            : VertexSpaceMode.CleanedSkinned;
    }

    /// <summary>
    /// JIANGYU-CONTRACT: direct-skin mesh nodes keep their authored vertex space even
    /// when third-party tools parent them under an armature root with a non-identity
    /// transform. The compiler must not bake that mesh-node world transform into
    /// skinned vertices for the proven authored replacement path.
    /// Scope: skinned mesh extraction for authored replacement sources.
    /// </summary>
    internal static bool ShouldBakeMeshNodeTransform(bool hasResolvedSkin, bool hasDirectSkinBinding, bool hasIdentityWorldTransform)
    {
        return hasResolvedSkin &&
               !hasDirectSkinBinding &&
               !hasIdentityWorldTransform;
    }

    private static CompiledMesh? ExtractMesh(
        Mesh mesh,
        ModelRoot model,
        Node meshNode,
        CompanionSkinData? companionSkinData,
        bool isCleaned,
        string? bindPoseReferencePath,
        string bundleMeshName,
        string? bindPoseReferenceSelector = null,
        float targetMeshMaxHalfExtent = 0f)
    {
        var sourceVertices = new List<Vector3>();
        var sourceNormals = new List<Vector3>();
        var sourceTangents = new List<Vector4>();
        var allUv0 = new List<float>();
        var allUv1 = new List<float>();
        var allColors = new List<float>();
        var allIndices = new List<int>();
        var allBoneWeights = new List<float>();
        var allBoneIndices = new List<int>();
        var subMeshes = new List<SubMeshInfo>();

        var hasSkinAttributes = mesh.Primitives.Any(p =>
            p.GetVertexAccessor("JOINTS_0") != null &&
            p.GetVertexAccessor("WEIGHTS_0") != null);

        var skin = meshNode.Skin;
        if (skin == null && hasSkinAttributes && model.LogicalSkins.Count > 0)
            skin = model.LogicalSkins[0];

        var bakeNodeTransform = ShouldBakeMeshNodeTransform(
            hasResolvedSkin: skin != null,
            hasDirectSkinBinding: meshNode.Skin != null,
            hasIdentityWorldTransform: IsIdentityMatrix(meshNode.WorldMatrix));

        var implicitSkinScale = Vector3.One;
        var bakeImplicitSkinScale = false;
        if (skin != null &&
            meshNode.Skin != null &&
            IsIdentityMatrix(meshNode.WorldMatrix) &&
            skin.JointsCount > 0)
        {
            var (firstJoint, _) = skin.GetJoint(0);
            implicitSkinScale = ExtractScale(firstJoint.WorldMatrix);
            if (!ApproximatelyOne(implicitSkinScale))
                bakeImplicitSkinScale = true;
        }

        // AssetRipper mesh-only GLBs can contain weighted vertices in centimeter-scale
        // mesh space with no Skin object. In that case, bake a metre-scale correction.
        var meshOnlyWeightScale = new Vector3(MeshOnlyWeightedVertexScale, MeshOnlyWeightedVertexScale, MeshOnlyWeightedVertexScale);
        var bakeMeshOnlyWeightScale = skin == null &&
                                      hasSkinAttributes &&
                                      model.LogicalSkins.Count == 0 &&
                                      NeedsMeshOnlyWeightScale(mesh);

        var worldTransform = meshNode.WorldMatrix;
        Matrix4x4.Invert(worldTransform, out var inverseWorldTransform);
        var normalTransform = Matrix4x4.Transpose(inverseWorldTransform);

        bool isSkinnedPath = skin != null || (companionSkinData is not null && companionSkinData.BoneNames.Length > 0);
        var authoredMaxHalfExtent = ComputeMaxHalfExtent(mesh);
        var spaceMode = DetermineVertexSpaceMode(
            isSkinnedPath,
            isCleaned,
            hasDirectSkinBinding: meshNode.Skin != null,
            appearsCentimeterScale: IsLikelyCentimeterScale(mesh),
            authoredMaxHalfExtent: authoredMaxHalfExtent,
            targetMaxHalfExtent: targetMeshMaxHalfExtent);

        var vertexOffset = 0;
        foreach (var primitive in mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions == null)
                continue;

            var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
            var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();

            foreach (var pos in positions)
            {
                var transformed = bakeNodeTransform ? Vector3.Transform(pos, worldTransform) : pos;
                if (bakeImplicitSkinScale)
                    transformed *= implicitSkinScale;
                if (bakeMeshOnlyWeightScale)
                    transformed *= meshOnlyWeightScale;
                sourceVertices.Add(transformed);
            }

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            if (normals != null)
            {
                foreach (var normal in normals)
                {
                    var transformed = bakeNodeTransform ? Vector3.Normalize(Vector3.TransformNormal(normal, normalTransform)) : normal;
                    sourceNormals.Add(transformed);
                }
            }

            var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
            if (tangents != null)
            {
                foreach (var tangent in tangents)
                {
                    var tangentVector = new Vector3(tangent.X, tangent.Y, tangent.Z);
                    var transformed = bakeNodeTransform ? Vector3.Normalize(Vector3.TransformNormal(tangentVector, normalTransform)) : tangentVector;
                    sourceTangents.Add(new Vector4(transformed, tangent.W));
                }
            }

            var uv0 = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            if (uv0 != null)
            {
                foreach (var uv in uv0)
                {
                    allUv0.Add(uv.X);
                    allUv0.Add(1 - uv.Y);
                }
            }

            var uv1 = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
            if (uv1 != null)
            {
                foreach (var uv in uv1)
                {
                    allUv1.Add(uv.X);
                    allUv1.Add(1 - uv.Y);
                }
            }

            var colorAccessor = primitive.GetVertexAccessor("COLOR_0");
            if (colorAccessor != null)
            {
                // Blender exports COLOR_0 as VEC3 (RGB) when the source mesh has no alpha.
                // SharpGLTF's AsVector4Array() rejects VEC3 accessors; read as VEC3 and
                // default alpha to 1.0 so round-tripped colour channels survive compilation.
                if (colorAccessor.Dimensions == DimensionType.VEC4)
                {
                    foreach (var color in colorAccessor.AsVector4Array())
                    {
                        allColors.Add(color.X);
                        allColors.Add(color.Y);
                        allColors.Add(color.Z);
                        allColors.Add(color.W);
                    }
                }
                else if (colorAccessor.Dimensions == DimensionType.VEC3)
                {
                    foreach (var color in colorAccessor.AsVector3Array())
                    {
                        allColors.Add(color.X);
                        allColors.Add(color.Y);
                        allColors.Add(color.Z);
                        allColors.Add(1f);
                    }
                }
            }

            if (weights != null && joints != null)
            {
                for (int i = 0; i < weights.Count; i++)
                {
                    allBoneWeights.Add(weights[i].X);
                    allBoneWeights.Add(weights[i].Y);
                    allBoneWeights.Add(weights[i].Z);
                    allBoneWeights.Add(weights[i].W);

                    allBoneIndices.Add((int)MathF.Round(joints[i].X));
                    allBoneIndices.Add((int)MathF.Round(joints[i].Y));
                    allBoneIndices.Add((int)MathF.Round(joints[i].Z));
                    allBoneIndices.Add((int)MathF.Round(joints[i].W));
                }
            }

            var indexAccessor = primitive.IndexAccessor;
            var indexStart = allIndices.Count;
            if (indexAccessor != null)
            {
                var indices = indexAccessor.AsIndicesArray();
                for (int i = 0; i < indices.Count; i += 3)
                {
                    allIndices.Add((int)indices[i] + vertexOffset);
                    allIndices.Add((int)indices[i + 2] + vertexOffset);
                    allIndices.Add((int)indices[i + 1] + vertexOffset);
                }
            }

            subMeshes.Add(new SubMeshInfo
            {
                IndexStart = indexStart,
                IndexCount = allIndices.Count - indexStart,
            });

            vertexOffset += positions.Count;
        }

        var bindPoses = Array.Empty<Matrix4x4>();
        var boneNames = Array.Empty<string>();
        var parentNames = Array.Empty<string?>();
        SkinBindingData? authoredSkinData = null;
        if (skin != null)
        {
            authoredSkinData = BuildSkinBindingData(skin);
            bindPoses = authoredSkinData.CompiledBindPoses;
            boneNames = authoredSkinData.BoneNames;
            parentNames = authoredSkinData.ParentNames;
        }
        else if (companionSkinData is not null && companionSkinData.BoneNames.Length > 0)
        {
            bindPoses = companionSkinData.BindPoses;
            boneNames = companionSkinData.BoneNames;
            parentNames = new string?[boneNames.Length];
        }

        if (!string.IsNullOrWhiteSpace(bindPoseReferencePath))
        {
            if (skin == null)
            {
                throw new InvalidOperationException(
                    $"Bind-pose retargeting for '{bundleMeshName}' requires a skinned authored model.");
            }

            if (spaceMode == VertexSpaceMode.StaticMesh)
            {
                throw new InvalidOperationException(
                    $"Bind-pose retargeting for '{bundleMeshName}' only supports authored skinned replacement sources.");
            }
            if (authoredSkinData is null)
            {
                throw new InvalidOperationException(
                    $"Bind-pose retargeting for '{bundleMeshName}' could not load authored skin binding data.");
            }

            var authoredContract = new BindPoseRetargetService.SkeletonContract
            {
                BoneNames = boneNames,
                ParentNames = parentNames,
                BindPoses = authoredSkinData.SourceBindPoses,
            };
            var referenceMeshSelector = string.IsNullOrWhiteSpace(bindPoseReferenceSelector)
                ? bundleMeshName
                : bindPoseReferenceSelector;
            var referenceSkin = LoadReferenceSkinBindingData(bindPoseReferencePath!, referenceMeshSelector);
            var referenceContract = new BindPoseRetargetService.SkeletonContract
            {
                BoneNames = referenceSkin.BoneNames,
                ParentNames = referenceSkin.ParentNames,
                BindPoses = referenceSkin.SourceBindPoses,
            };

            var alignedReferenceBindPoses = boneNames
                .Select(name => referenceSkin.CompiledBindPoses[FindBoneIndex(referenceSkin.BoneNames, name)])
                .ToArray();

            if (BindPoseRetargetService.NeedsRetarget(authoredContract, referenceContract))
            {
                var retargeted = BindPoseRetargetService.Retarget(
                    [.. sourceVertices],
                    [.. sourceNormals],
                    [.. sourceTangents],
                    [.. allBoneWeights],
                    [.. allBoneIndices],
                    authoredContract,
                    referenceContract);

                sourceVertices = [.. retargeted.Positions];
                // JIANGYU-CONTRACT: After bind-pose retargeting, authored normals/tangents are
                // no longer trusted for the current proven Blender round-trip path. Rebuild them
                // from the corrected geometry in Unity so shading follows the final compiled mesh.
                sourceNormals = [];
                sourceTangents = [];
            }

            bindPoses = alignedReferenceBindPoses;
        }

        var allVertices = new List<float>(sourceVertices.Count * 3);
        foreach (var transformed in sourceVertices)
        {
            switch (spaceMode)
            {
                case VertexSpaceMode.RawPrefabSkinned:
                    allVertices.Add(-transformed.X);
                    allVertices.Add(transformed.Y);
                    allVertices.Add(transformed.Z);
                    break;
                case VertexSpaceMode.CleanedSkinned:
                    allVertices.Add(-transformed.X * MenaceCharacterSpaceScale);
                    allVertices.Add(transformed.Y * MenaceCharacterSpaceScale);
                    allVertices.Add(transformed.Z * MenaceCharacterSpaceScale);
                    break;
                case VertexSpaceMode.StaticMesh:
                    allVertices.Add(transformed.X);
                    allVertices.Add(transformed.Y);
                    allVertices.Add(-transformed.Z);
                    break;
            }
        }

        var allNormals = new List<float>(sourceNormals.Count * 3);
        foreach (var transformed in sourceNormals)
        {
            if (spaceMode != VertexSpaceMode.StaticMesh)
            {
                allNormals.Add(-transformed.X);
                allNormals.Add(transformed.Y);
                allNormals.Add(transformed.Z);
            }
            else
            {
                allNormals.Add(transformed.X);
                allNormals.Add(transformed.Y);
                allNormals.Add(-transformed.Z);
            }
        }

        var allTangents = new List<float>(sourceTangents.Count * 4);
        foreach (var tangent in sourceTangents)
        {
            if (spaceMode != VertexSpaceMode.StaticMesh)
            {
                allTangents.Add(-tangent.X);
                allTangents.Add(tangent.Y);
                allTangents.Add(tangent.Z);
                allTangents.Add(-tangent.W);
            }
            else
            {
                allTangents.Add(tangent.X);
                allTangents.Add(tangent.Y);
                allTangents.Add(-tangent.Z);
                allTangents.Add(-tangent.W);
            }
        }

        return new CompiledMesh
        {
            Name = mesh.Name ?? $"Mesh_{mesh.LogicalIndex}",
            Vertices = [.. allVertices],
            Normals = [.. allNormals],
            Tangents = [.. allTangents],
            UV0 = [.. allUv0],
            UV1 = [.. allUv1],
            Colors = [.. allColors],
            Indices = [.. allIndices],
            SubMeshes = subMeshes,
            BoneWeights = [.. allBoneWeights],
            BoneIndices = [.. allBoneIndices],
            BindPoses = bindPoses,
            BoneNames = boneNames,
        };
    }

    private static int FindBoneIndex(IReadOnlyList<string> boneNames, string boneName)
    {
        for (int i = 0; i < boneNames.Count; i++)
        {
            if (string.Equals(boneNames[i], boneName, StringComparison.Ordinal))
                return i;
        }

        throw new InvalidOperationException($"Reference skeleton is missing bone '{boneName}'.");
    }

    private static SkinBindingData BuildSkinBindingData(Skin skin)
    {
        var sourceBindPoses = new Matrix4x4[skin.JointsCount];
        var compiledBindPoses = new Matrix4x4[skin.JointsCount];
        var boneNames = new string[skin.JointsCount];
        var parentNames = new string?[skin.JointsCount];

        for (int i = 0; i < skin.JointsCount; i++)
        {
            var (joint, inverseBindMatrix) = skin.GetJoint(i);
            sourceBindPoses[i] = inverseBindMatrix;
            compiledBindPoses[i] = ConvertMatrix(inverseBindMatrix);
            boneNames[i] = joint.Name ?? $"Bone_{i}";
            parentNames[i] = joint.VisualParent?.Name;
        }

        return new SkinBindingData
        {
            SourceBindPoses = sourceBindPoses,
            CompiledBindPoses = compiledBindPoses,
            BoneNames = boneNames,
            ParentNames = parentNames,
        };
    }

    private static SkinBindingData LoadReferenceSkinBindingData(string referencePath, string referenceMeshSelector)
    {
        var model = ModelRoot.Load(referencePath);
        var candidates = model.LogicalNodes
            .Where(node => node.Mesh != null)
            .Select(node => ExtractMeshCandidate(node, model, companionSkinData: null, isCleaned: IsCleanedExport(referencePath)))
            .Where(candidate => candidate != null)
            .Cast<ExtractedMeshCandidate>()
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException($"Bind-pose reference '{referencePath}' contains no mesh nodes.");

        var normalisedSelector = NormaliseReferenceAlias(referenceMeshSelector);
        var match = candidates.FirstOrDefault(candidate =>
            candidate.Aliases.Contains(referenceMeshSelector) ||
            candidate.Aliases.Any(alias => string.Equals(NormaliseReferenceAlias(alias), normalisedSelector, StringComparison.Ordinal)));
        if (match == null && candidates.Count == 1)
            match = candidates[0];

        if (match == null)
        {
            var available = string.Join(", ", candidates.SelectMany(c => c.Aliases).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Bind-pose reference '{referencePath}' has no mesh node matching '{referenceMeshSelector}'. Available names: {available}");
        }

        if (match.Node.Skin == null)
        {
            throw new InvalidOperationException(
                $"Bind-pose reference '{referencePath}' mesh '{referenceMeshSelector}' is not skinned.");
        }

        return BuildSkinBindingData(match.Node.Skin);
    }

    private static string NormaliseReferenceAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return string.Empty;

        var segments = alias
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var normalised = segment;
                if (CompilationService.TryStripBlenderNumericSuffix(normalised, out var strippedBlender, out _))
                    normalised = strippedBlender;

                const string containerSuffix = "_container";
                if (normalised.EndsWith(containerSuffix, StringComparison.Ordinal))
                    normalised = normalised[..^containerSuffix.Length];

                return normalised;
            });

        return string.Join("/", segments);
    }

    private static bool IsIdentityMatrix(Matrix4x4 matrix)
    {
        const float epsilon = 1e-5f;
        return Math.Abs(matrix.M11 - 1f) < epsilon &&
               Math.Abs(matrix.M22 - 1f) < epsilon &&
               Math.Abs(matrix.M33 - 1f) < epsilon &&
               Math.Abs(matrix.M44 - 1f) < epsilon &&
               Math.Abs(matrix.M12) < epsilon &&
               Math.Abs(matrix.M13) < epsilon &&
               Math.Abs(matrix.M14) < epsilon &&
               Math.Abs(matrix.M21) < epsilon &&
               Math.Abs(matrix.M23) < epsilon &&
               Math.Abs(matrix.M24) < epsilon &&
               Math.Abs(matrix.M31) < epsilon &&
               Math.Abs(matrix.M32) < epsilon &&
               Math.Abs(matrix.M34) < epsilon &&
               Math.Abs(matrix.M41) < epsilon &&
               Math.Abs(matrix.M42) < epsilon &&
               Math.Abs(matrix.M43) < epsilon;
    }

    private static Vector3 ExtractScale(Matrix4x4 matrix)
        => new(
            new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),
            new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(),
            new Vector3(matrix.M31, matrix.M32, matrix.M33).Length());

    private static bool ApproximatelyOne(Vector3 scale)
    {
        const float epsilon = 1e-4f;
        return Math.Abs(scale.X - 1f) < epsilon &&
               Math.Abs(scale.Y - 1f) < epsilon &&
               Math.Abs(scale.Z - 1f) < epsilon;
    }

    internal static bool IsLikelyCentimeterScale(Mesh mesh)
    {
        var maxExtent = 0f;
        foreach (var primitive in mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions == null || positions.Count == 0)
                continue;

            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            foreach (var position in positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            var extent = max - min;
            maxExtent = Math.Max(maxExtent, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)));
        }

        return maxExtent > MeshOnlyWeightScaleExtentThreshold;
    }

    private static bool NeedsMeshOnlyWeightScale(Mesh mesh)
        => IsLikelyCentimeterScale(mesh);

    private static float ComputeMaxHalfExtent(Mesh mesh)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var seenAny = false;
        foreach (var primitive in mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions == null)
                continue;
            foreach (var position in positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                seenAny = true;
            }
        }

        if (!seenAny)
            return 0f;

        var extent = (max - min) * 0.5f;
        return Math.Max(Math.Abs(extent.X), Math.Max(Math.Abs(extent.Y), Math.Abs(extent.Z)));
    }

    private static List<CompiledTexture> ExtractTextures(IReadOnlyList<MeshSourceEntry> entries)
    {
        var result = new Dictionary<string, CompiledTexture>(StringComparer.Ordinal);
        foreach (var sourceFilePath in entries.Select(entry => entry.SourceFilePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Prefer glTF-family material graph when available (.gltf or .glb)
            if (TryExtractTexturesFromModelGraph(sourceFilePath, result))
            {
                continue;
            }

            // Fallback: prefix-based directory discovery when the source file does not
            // carry readable image content/paths Jiangyu can use directly.
            var prefix = InferTexturePrefix(sourceFilePath);
            if (string.IsNullOrWhiteSpace(prefix))
                continue;

            foreach (var texturePath in EnumerateSidecarTexturePaths(sourceFilePath, prefix))
            {
                var name = Path.GetFileNameWithoutExtension(texturePath);
                if (result.ContainsKey(name))
                    continue;

                result[name] = new CompiledTexture
                {
                    Name = name,
                    Content = File.ReadAllBytes(texturePath),
                    Linear = IsLinearTextureName(name),
                };
            }
        }

        return [.. result.Values.OrderBy(texture => texture.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads textures from a glTF-family file's material graph (.gltf or .glb).
    /// Only explicitly declared textures count:
    /// standard material channels plus Jiangyu material extras.
    /// </summary>
    internal static bool TryExtractTexturesFromModelGraph(string sourceFilePath, Dictionary<string, CompiledTexture> result)
    {
        if (!IsGltfFamilySource(sourceFilePath))
        {
            return false;
        }

        var sourceDir = Path.GetDirectoryName(sourceFilePath);
        if (sourceDir is null)
        {
            return false;
        }

        ModelRoot model;
        try
        {
            model = ModelRoot.Load(sourceFilePath);
        }
        catch
        {
            return false;
        }

        bool found = false;

        // 1. Standard material channels — textures assigned to PBR slots
        foreach (var material in model.LogicalMaterials)
        {
            foreach (var channel in material.Channels)
            {
                var image = channel.Texture?.PrimaryImage;
                if (image is null)
                {
                    continue;
                }

                var name = GetImageName(image);
                if (result.ContainsKey(name))
                {
                    continue;
                }

                var content = image.Content;
                if (content.IsEmpty)
                {
                    continue;
                }

                result[name] = new CompiledTexture
                {
                    Name = name,
                    Content = content.Content.ToArray(),
                    Linear = IsLinearTextureName(name),
                };
                found = true;
            }

            // 2. Non-standard textures from material extras (written by exporter)
            if (material.Extras is System.Text.Json.Nodes.JsonObject matExtras &&
                matExtras.TryGetPropertyValue("jiangyu", out var jiangyuNode) &&
                jiangyuNode is System.Text.Json.Nodes.JsonObject jiangyuObj &&
                jiangyuObj.TryGetPropertyValue("textures", out var texturesNode) &&
                texturesNode is System.Text.Json.Nodes.JsonObject texturesObj)
            {
                foreach (var kvp in texturesObj)
                {
                    var relativePath = kvp.Value?.GetValue<string>();
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        continue;
                    }

                    var absolutePath = Path.Combine(sourceDir, relativePath);
                    if (!File.Exists(absolutePath))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(absolutePath);
                    if (result.ContainsKey(name))
                    {
                        continue;
                    }

                    result[name] = new CompiledTexture
                    {
                        Name = name,
                        Content = File.ReadAllBytes(absolutePath),
                        Linear = IsLinearTextureName(name),
                    };
                    found = true;
                }
            }
        }

        return found;
    }

    internal static string GetImageName(Image image)
    {
        // Name is set explicitly by the exporter
        if (!string.IsNullOrEmpty(image.Name))
        {
            return image.Name;
        }

        // AlternateWriteFileName is only available during write, not after load
        if (!string.IsNullOrEmpty(image.AlternateWriteFileName))
        {
            return Path.GetFileNameWithoutExtension(image.AlternateWriteFileName);
        }

        return $"image_{image.LogicalIndex}";
    }

    internal static string? InferTexturePrefix(string sourceFilePath)
    {
        // For glTF-family files: derive from image names in the material graph when possible.
        if (IsGltfFamilySource(sourceFilePath))
        {
            try
            {
                var textures = new Dictionary<string, CompiledTexture>(StringComparer.Ordinal);
                if (TryExtractTexturesFromModelGraph(sourceFilePath, textures) && textures.Count > 0)
                {
                    var textureNames = textures.Keys
                        .Where(name => !name.StartsWith("image_", StringComparison.Ordinal))
                        .ToList();
                    if (textureNames.Count == 0)
                    {
                        textureNames = [.. textures.Keys];
                    }

                    if (textureNames.Count == 1)
                    {
                        return NormalizeTexturePrefix(textureNames[0]);
                    }

                    return FindCommonPrefix(textureNames) ?? NormalizeTexturePrefix(textureNames[0]);
                }
            }
            catch
            {
                // Fall through to filename heuristic
            }
        }

        // Fallback: infer from filename
        var sourceFileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(sourceFileName))
            return null;

        var lodMarker = sourceFileName.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        if (lodMarker > 0)
            return sourceFileName[..lodMarker];

        return sourceFileName;
    }

    internal static string? FindCommonPrefix(List<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        var sorted = names.OrderBy(n => n.Length).ToList();
        var shortest = sorted[0];

        for (int len = shortest.Length; len > 0; len--)
        {
            var candidate = shortest[..len];
            if (candidate.EndsWith('_'))
            {
                candidate = candidate[..^1];
            }

            if (sorted.All(n => n.StartsWith(candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string NormalizeTexturePrefix(string name)
    {
        foreach (var suffix in new[] { "_BaseMap", "_NormalMap", "_MaskMap", "_EffectMap" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    /// <summary>
    /// Reads the cleaned flag from a glTF-family root extras, or false if not a
    /// readable glTF-family source or no extras are present.
    /// </summary>
    internal static bool IsCleanedExport(string sourceFilePath)
    {
        if (!IsGltfFamilySource(sourceFilePath))
        {
            return false;
        }

        try
        {
            var model = ModelRoot.Load(sourceFilePath);
            if (model.Extras is System.Text.Json.Nodes.JsonObject rootExtras &&
                rootExtras.TryGetPropertyValue("jiangyu", out var jiangyuNode) &&
                jiangyuNode is System.Text.Json.Nodes.JsonObject jiangyuObj &&
                jiangyuObj.TryGetPropertyValue("cleaned", out var cleanedNode))
            {
                return cleanedNode?.GetValue<bool>() ?? false;
            }
        }
        catch
        {
            // Fall through
        }

        return false;
    }

    internal static bool IsGltfFamilySource(string sourceFilePath)
        => string.Equals(Path.GetExtension(sourceFilePath), ".gltf", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetExtension(sourceFilePath), ".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback texture discovery for non-glTF-family sources (prefix-based directory search).
    /// </summary>
    internal static IEnumerable<string> EnumerateSidecarTexturePaths(string sourceFilePath, string texturePrefix)
    {
        var sourceDir = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(sourceDir))
            yield break;

        var candidateDirs = new List<string>
        {
            Path.Combine(sourceDir, "textures"),
        };
        var assetsDir = Directory.GetParent(sourceDir)?.FullName;
        if (!string.IsNullOrEmpty(assetsDir))
        {
            candidateDirs.Add(Path.Combine(assetsDir, "Texture2D"));
        }

        foreach (var textureDir in candidateDirs)
        {
            if (!Directory.Exists(textureDir))
                continue;

            foreach (var texturePath in Directory.EnumerateFiles(textureDir, $"{texturePrefix}*.*", SearchOption.TopDirectoryOnly)
                         .Where(path =>
                             path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return texturePath;
            }
        }
    }

    internal static bool IsLinearTextureName(string textureName)
        => textureName.EndsWith("_MaskMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_NormalMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_Normal", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_EffectMap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if a texture filename matches a known MENACE texture naming pattern.
    /// Used by the naming inference step to avoid importing unrelated files from textures/.
    /// </summary>
    internal static bool IsMenaceTextureNamePattern(string textureName)
        => textureName.EndsWith("_BaseMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_BaseColorMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_NormalMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_Normal", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_MaskMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_EffectMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_Emissive", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_EmissiveColorMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_OcclusionMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_MetallicGlossMap", StringComparison.OrdinalIgnoreCase);

    private static CompanionSkinData? TryLoadCompanionSkinData(string sourceFilePath)
    {
        var sourceDir = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(sourceDir))
            return null;

        var sourceFileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        var lodMarker = sourceFileName.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        if (lodMarker < 0)
            return null;

        var baseName = sourceFileName[..lodMarker];
        var assetsDir = Directory.GetParent(sourceDir)?.FullName;
        if (string.IsNullOrEmpty(assetsDir))
            return null;

        var prefabDir = Path.Combine(assetsDir, "PrefabHierarchyObject");
        if (!Directory.Exists(prefabDir))
            return null;

        var candidates = new[]
        {
            Path.Combine(prefabDir, $"el.{baseName}.glb"),
            Path.Combine(prefabDir, $"{baseName}.glb"),
        };

        var companionPath = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrEmpty(companionPath))
            return null;

        var companionModel = ModelRoot.Load(companionPath);
        var contract = GlbCharacterContract.Load(companionPath);
        if (contract.Skins.Count == 0)
            return null;

        var skin = contract.Skins[0];
        var bindPoses = new Matrix4x4[skin.JointCount];
        var boneNames = new string[skin.JointCount];
        var nodeByPath = companionModel.LogicalNodes
            .ToDictionary(GlbCharacterContract.BuildNodePath, StringComparer.Ordinal);

        var canonicalBySource = contract.SuggestedJointRemaps
            .Where(r => r.SkinIndex == skin.Index)
            .ToDictionary(r => r.SourcePath, r => r.CanonicalPath, StringComparer.Ordinal);

        var rendererPath = $"el.{baseName}/{sourceFileName}";
        if (!nodeByPath.TryGetValue(rendererPath, out var rendererNode))
            return null;

        for (int i = 0; i < skin.JointCount; i++)
        {
            var joint = skin.Joints[i];
            var canonicalPath = canonicalBySource.GetValueOrDefault(joint.Path);
            var bindPath = canonicalPath ?? joint.Path;
            if (!nodeByPath.TryGetValue(bindPath, out var boneNode))
                return null;

            if (!Matrix4x4.Invert(boneNode.WorldMatrix, out var boneWorldInverse))
                return null;

            var canonicalBindPose = boneWorldInverse * rendererNode.WorldMatrix;
            bindPoses[i] = ConvertMatrix(canonicalBindPose);
            boneNames[i] = bindPath.Split('/').Last();
        }

        return new CompanionSkinData
        {
            BindPoses = bindPoses,
            BoneNames = boneNames,
        };
    }

    private static Matrix4x4 ConvertMatrix(Matrix4x4 m)
        => new(
            m.M11, m.M12, -m.M13, m.M14,
            m.M21, m.M22, -m.M23, m.M24,
            -m.M31, -m.M32, m.M33, -m.M34,
            m.M41, m.M42, -m.M43, m.M44);

    private static void WriteMeshData(string path, List<CompiledMesh> meshes)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(Magic);
        writer.Write(1);
        writer.Write(meshes.Count);

        foreach (var mesh in meshes)
        {
            var vertexCount = mesh.Vertices.Length / 3;
            var hasNormals = mesh.Normals.Length == vertexCount * 3;
            var hasTangents = mesh.Tangents.Length == vertexCount * 4;
            var hasUv0 = mesh.UV0.Length == vertexCount * 2;
            var hasUv1 = mesh.UV1.Length == vertexCount * 2;
            var hasColors = mesh.Colors.Length == vertexCount * 4;
            var hasSkinning = mesh.BoneWeights.Length == vertexCount * 4 &&
                              mesh.BoneIndices.Length == vertexCount * 4;

            var nameBytes = Encoding.UTF8.GetBytes(mesh.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            writer.Write(vertexCount);
            writer.Write(mesh.Indices.Length);
            writer.Write(mesh.SubMeshes.Count);

            byte flags = 0;
            if (hasNormals) flags |= 0x01;
            if (hasTangents) flags |= 0x02;
            if (hasUv0) flags |= 0x04;
            if (hasUv1) flags |= 0x08;
            if (hasColors) flags |= 0x10;
            if (hasSkinning) flags |= 0x20;
            writer.Write(flags);

            WriteFloatArray(writer, mesh.Vertices);
            if (hasNormals) WriteFloatArray(writer, mesh.Normals);
            if (hasTangents) WriteFloatArray(writer, mesh.Tangents);
            if (hasUv0) WriteFloatArray(writer, mesh.UV0);
            if (hasUv1) WriteFloatArray(writer, mesh.UV1);

            if (hasColors)
            {
                for (int i = 0; i < mesh.Colors.Length; i += 4)
                {
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 0], 0f, 1f) * 255));
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 1], 0f, 1f) * 255));
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 2], 0f, 1f) * 255));
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 3], 0f, 1f) * 255));
                }
            }

            foreach (var index in mesh.Indices)
                writer.Write(index);

            foreach (var subMesh in mesh.SubMeshes)
            {
                writer.Write(subMesh.IndexStart);
                writer.Write(subMesh.IndexCount);
                writer.Write(0);
            }

            if (hasSkinning)
            {
                WriteFloatArray(writer, mesh.BoneWeights);
                foreach (var boneIndex in mesh.BoneIndices)
                    writer.Write(boneIndex);

                writer.Write(mesh.BindPoses.Length);
                foreach (var bindPose in mesh.BindPoses)
                {
                    writer.Write(bindPose.M11); writer.Write(bindPose.M12); writer.Write(bindPose.M13); writer.Write(bindPose.M14);
                    writer.Write(bindPose.M21); writer.Write(bindPose.M22); writer.Write(bindPose.M23); writer.Write(bindPose.M24);
                    writer.Write(bindPose.M31); writer.Write(bindPose.M32); writer.Write(bindPose.M33); writer.Write(bindPose.M34);
                    writer.Write(bindPose.M41); writer.Write(bindPose.M42); writer.Write(bindPose.M43); writer.Write(bindPose.M44);
                }
            }
        }
    }

    private static void WriteTextureData(string path, IReadOnlyList<CompiledTexture> textures)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(TextureMagic);
        writer.Write(1);
        writer.Write(textures.Count);

        foreach (var texture in textures)
        {
            var nameBytes = Encoding.UTF8.GetBytes(texture.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(texture.Linear ? (byte)1 : (byte)0);
            writer.Write(texture.Content.Length);
            writer.Write(texture.Content);
        }
    }

    private static void WriteFloatArray(BinaryWriter writer, float[] values)
    {
        foreach (var value in values)
            writer.Write(value);
    }

    private static void WriteSourceDiagnostics(string path, IReadOnlyList<CompiledMesh> meshes)
    {
        var payload = meshes.Select(mesh =>
        {
            var vertexCount = mesh.Vertices.Length / 3;
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            for (int i = 0; i < mesh.Vertices.Length; i += 3)
            {
                var v = new Vector3(mesh.Vertices[i], mesh.Vertices[i + 1], mesh.Vertices[i + 2]);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            var samples = new List<object>();
            for (int i = 0; i < Math.Min(vertexCount, 8); i++)
            {
                samples.Add(new
                {
                    vertex = i,
                    joints = new[]
                    {
                        mesh.BoneIndices.Length >= (i * 4 + 1) ? mesh.BoneIndices[i * 4 + 0] : -1,
                        mesh.BoneIndices.Length >= (i * 4 + 2) ? mesh.BoneIndices[i * 4 + 1] : -1,
                        mesh.BoneIndices.Length >= (i * 4 + 3) ? mesh.BoneIndices[i * 4 + 2] : -1,
                        mesh.BoneIndices.Length >= (i * 4 + 4) ? mesh.BoneIndices[i * 4 + 3] : -1,
                    },
                    weights = new[]
                    {
                        mesh.BoneWeights.Length >= (i * 4 + 1) ? mesh.BoneWeights[i * 4 + 0] : 0f,
                        mesh.BoneWeights.Length >= (i * 4 + 2) ? mesh.BoneWeights[i * 4 + 1] : 0f,
                        mesh.BoneWeights.Length >= (i * 4 + 3) ? mesh.BoneWeights[i * 4 + 2] : 0f,
                        mesh.BoneWeights.Length >= (i * 4 + 4) ? mesh.BoneWeights[i * 4 + 3] : 0f,
                    }
                });
            }

            return new
            {
                mesh = mesh.Name,
                vertexCount,
                bindPoseCount = mesh.BindPoses.Length,
                boneNameCount = mesh.BoneNames.Length,
                min = new[] { min.X, min.Y, min.Z },
                max = new[] { max.X, max.Y, max.Z },
                samples
            };
        });

        var json = JsonSerializer.Serialize(payload, PrettyJsonOptions);
        File.WriteAllText(path, json);
    }

    private static async Task SetupUnityProjectAsync(
        string unityProjectDir,
        IReadOnlyList<ImportedSpriteAsset> directSprites,
        IReadOnlyList<ImportedAudioAsset> directAudioAssets)
    {
        var assetsDir = Path.Combine(unityProjectDir, "Assets");
        var editorDir = Path.Combine(assetsDir, "Editor");
        Directory.CreateDirectory(editorDir);

        var buildScript = LoadEmbeddedResource("Jiangyu.Core.Unity.MeshBundleBuilder.template");
        await File.WriteAllTextAsync(Path.Combine(editorDir, "MeshBundleBuilder.cs"), buildScript);

        var audioDir = Path.Combine(assetsDir, "Audio");
        if (Directory.Exists(audioDir))
            Directory.Delete(audioDir, recursive: true);

        var spriteSourceDir = Path.Combine(assetsDir, "SpriteSources");
        if (Directory.Exists(spriteSourceDir))
            Directory.Delete(spriteSourceDir, recursive: true);

        if (directAudioAssets.Count == 0 && directSprites.Count == 0)
            return;

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

        if (directSprites.Count > 0)
            Directory.CreateDirectory(spriteSourceDir);
        foreach (var asset in directSprites)
        {
            var extension = string.IsNullOrWhiteSpace(asset.Extension) ? string.Empty : asset.Extension;
            if (!string.IsNullOrEmpty(extension) && !extension.StartsWith('.'))
                extension = $".{extension}";

            var destinationPath = Path.Combine(spriteSourceDir, $"{asset.StagingName}{extension}");
            File.Copy(asset.SourceFilePath, destinationPath, overwrite: true);
        }
    }

    private static async Task InvokeUnityBuildAsync(
        string unityEditor,
        string unityProjectDir,
        string outputBundlePath,
        string bundleName,
        string meshDataPath,
        string textureDataPath,
        string diagnosticsPath,
        string? meshContractPath)
    {
        var logFile = Path.Combine(unityProjectDir, "build.log");
        var arguments = string.Join(" ",
            "-batchmode",
            "-nographics",
            "-quit",
            $"-projectPath \"{unityProjectDir}\"",
            "-executeMethod MeshBundleBuilder.Build",
            $"-meshDataPath \"{meshDataPath}\"",
            $"-textureDataPath \"{textureDataPath}\"",
            $"-outputPath \"{outputBundlePath}\"",
            $"-diagnosticsPath \"{diagnosticsPath}\"",
            (string.IsNullOrEmpty(meshContractPath) ? null : $"-meshContractPath \"{meshContractPath}\""),
            $"-bundleName {bundleName}",
            $"-logFile \"{logFile}\"");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = unityEditor,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
            return;

        var errorOutput = await process.StandardError.ReadToEndAsync();
        var logTail = string.Empty;
        if (File.Exists(logFile))
        {
            var lines = await File.ReadAllLinesAsync(logFile);
            logTail = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 20)));
        }

        throw new InvalidOperationException(
            $"Unity mesh build failed (exit code {process.ExitCode}). Log: {logFile}{Environment.NewLine}{errorOutput}{Environment.NewLine}{logTail}".Trim());
    }

    private static void WriteMeshContracts(string path, IReadOnlyList<MeshBuildContract> contracts)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(0x54435254); // "TRCT"
        writer.Write(1);
        writer.Write(contracts.Count);

        foreach (var contract in contracts)
        {
            var nameBytes = Encoding.UTF8.GetBytes(contract.MeshName);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            writer.Write(contract.BoneNameHashes.Length);
            foreach (var hash in contract.BoneNameHashes)
                writer.Write(hash);

            writer.Write(contract.RootBoneNameHash);

            writer.Write(contract.BindPoses.Length);
            foreach (var bindPose in contract.BindPoses)
            {
                foreach (var value in bindPose)
                    writer.Write(value);
            }
        }
    }

    internal static string LoadEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
