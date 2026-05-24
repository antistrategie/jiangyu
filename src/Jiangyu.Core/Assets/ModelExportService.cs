using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Import.Configuration;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Glb;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Self-contained model export: loads game data, finds the named asset by
/// (collection, pathId), discovers material → texture references, optionally
/// runs <see cref="ModelCleaner"/> normalisation, and writes either a
/// <c>.gltf</c> + textures package or a raw <c>.glb</c>.
///
/// <para>Skin recovery is part of the pipeline: some MENACE vehicle exports
/// carry <c>JOINTS_0</c> + <c>WEIGHTS_0</c> attributes on the mesh but no
/// glTF skin (because the runtime resolves skinning against sibling
/// hierarchy nodes). The helpers below rebuild the skin against either a
/// source-asset-derived bone path map or, failing that, the depth-first
/// non-mesh siblings under the same parent.</para>
/// </summary>
public sealed class ModelExportService(string gameDataPath, IProgressSink progress, ILogSink log)
{
    private readonly string _gameDataPath = gameDataPath;
    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    /// <summary>
    /// MENACE material property → standard glTF PBR channel.
    /// Only properties with genuine glTF PBR equivalents are mapped here.
    /// MENACE-specific properties (_MaskMap, _Effect_Map) are NOT mapped — they go to extras.
    /// </summary>
    internal static readonly Dictionary<string, string> StandardChannelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_BaseColorMap"] = "BaseColor",
        ["_MainTex"] = "BaseColor",
        ["_BaseMap"] = "BaseColor",
        ["_NormalMap"] = "Normal",
        ["_BumpMap"] = "Normal",
        ["_MetallicGlossMap"] = "MetallicRoughness",
        ["_EmissionMap"] = "Emissive",
        ["_OcclusionMap"] = "Occlusion",
    };

    internal sealed class DiscoveredTexture
    {
        public required string Name { get; init; }
        public required string MaterialName { get; init; }
        /// <summary>Stable source identity: "collection:pathId". Matches extras.jiangyu.sourceMaterial in the GLB.</summary>
        public required string SourceMaterialId { get; init; }
        public required string Property { get; init; }
        public required byte[] PngData { get; init; }
    }

    internal sealed record SourceSkinBinding(string MeshName, string MeshNodePath, string[] BonePaths);
    internal sealed record RecoveredSkinBinding(int NodeIndex, int[] JointNodeIndices);
    internal sealed record RecoveredSkinAssignment(int NodeIndex, int SkinIndex);

    /// <summary>
    /// Loads game data, finds the named asset, and exports a self-contained model package.
    /// Clean export layout:
    ///   {packageDir}/model.gltf          (references textures via standard glTF material channels)
    ///   {packageDir}/textures/*.png
    /// Raw export layout:
    ///   {packageDir}/model.glb            (self-contained, no textures)
    /// </summary>
    public void ExportModel(string assetName, string packageDir, bool clean, string collection, long pathId)
    {
        _log.Info($"Loading game data from: {_gameDataPath}");

        using var session = new GameDataSession(_gameDataPath, _progress, scriptContentLevel: ScriptContentLevel.Level0);

        if (!session.HasAnyAssetCollections)
        {
            _log.Error("No asset collections found in game data.");
            return;
        }

        IUnityObjectBase? found = null;
        foreach (var col in session.GameData.GameBundle.FetchAssetCollections())
        {
            if (col.Name != collection)
                continue;

            found = col.FirstOrDefault(a => a.PathID == pathId);
            break;
        }

        if (found is IGameObject gameObject)
        {
            _log.Info($"Found GameObject: {gameObject.Name}");
            ExportGameObjectPackage(gameObject, packageDir, clean);
            return;
        }

        if (found is PrefabHierarchyObject prefabHierarchy)
        {
            _log.Info($"Found PrefabHierarchyObject: {prefabHierarchy.Name}");
            ExportGameObjectPackage(prefabHierarchy.Root, packageDir, clean, prefabHierarchy.Assets);
            return;
        }

        if (found is IMesh mesh)
        {
            _log.Info($"Found Mesh: {mesh.Name}");
            Directory.CreateDirectory(packageDir);
            var glbPath = Path.Combine(packageDir, "model.glb");
            ExportMeshAsGlb(mesh, glbPath);
            return;
        }

        _log.Error($"No GameObject or Mesh named '{assetName}' found.");
    }

    private void ExportGameObjectPackage(
        IGameObject gameObject,
        string packageDir,
        bool clean,
        IEnumerable<IUnityObjectBase>? exportAssets = null)
    {
        Directory.CreateDirectory(packageDir);

        IGameObject root = gameObject.GetRoot();
        var assets = exportAssets ?? root.FetchHierarchy().Cast<IUnityObjectBase>();
        SceneBuilder rawScene = GlbLevelBuilder.Build(assets, false);

        if (clean)
        {
            // Write raw GLB to temp file for cleanup
            var tempGlbPath = Path.Combine(packageDir, ".model.tmp.glb");
            using (var stream = File.Create(tempGlbPath))
            {
                if (!GlbWriter.TryWrite(rawScene, stream, out string? errorMessage))
                {
                    _log.Error($"Error writing GLB: {errorMessage}");
                    return;
                }
            }

            // Discover textures from source asset hierarchy
            var textures = DiscoverTextures(root);

            // Prepare standard-channel textures for MaterialBuilder attachment during cleanup.
            // Keyed by stable source material identity (collection:pathId), not material name.
            // This matches extras.jiangyu.sourceMaterial embedded in the GLB by GlbLevelBuilder.
            var materialTextures = new Dictionary<string, List<(string channelKey, byte[] pngData)>>(StringComparer.Ordinal);
            foreach (var tex in textures)
            {
                if (StandardChannelMap.TryGetValue(tex.Property, out var channelKey))
                {
                    if (!materialTextures.TryGetValue(tex.SourceMaterialId, out var list))
                    {
                        list = [];
                        materialTextures[tex.SourceMaterialId] = list;
                    }
                    list.Add((channelKey, tex.PngData));
                }
            }

            var cleanScene = ModelCleaner.BuildCleanScene(tempGlbPath, _log, materialTextures);
            File.Delete(tempGlbPath);
            _log.Info("  Cleaned: 1x authoring scale");
            var sourceSkinBindings = CollectSourceSkinBindings(root);

            // Build .gltf — standard textures already attached at MaterialBuilder level,
            // SaveGltfPackage only handles non-standard textures + extras
            var gltfPath = Path.Combine(packageDir, "model.gltf");
            SaveGltfPackage(cleanScene, textures, gltfPath, sourceSkinBindings);

            if (textures.Count > 0)
            {
                _log.Info($"  Exported {textures.Count} textures");
            }
        }
        else
        {
            // Raw export: .glb + textures (for inspection, not authoring)
            var glbPath = Path.Combine(packageDir, "model.glb");
            using (var stream = File.Create(glbPath))
            {
                if (GlbWriter.TryWrite(rawScene, stream, out string? errorMessage))
                {
                    _log.Info($"  Model: {glbPath}");
                }
                else
                {
                    _log.Error($"Error writing GLB: {errorMessage}");
                    return;
                }
            }

            // Export textures as loose files for inspection context
            var textures = DiscoverTextures(root);
            if (textures.Count > 0)
            {
                var texturesDir = Path.Combine(packageDir, "textures");
                Directory.CreateDirectory(texturesDir);
                foreach (var tex in textures)
                {
                    File.WriteAllBytes(Path.Combine(texturesDir, $"{tex.Name}.png"), tex.PngData);
                }
                _log.Info($"  Exported {textures.Count} textures");
            }
        }

        _log.Info($"Package: {packageDir}");
    }

    /// <summary>
    /// Walks the source asset hierarchy, resolves material → texture references,
    /// and returns discovered textures with their PNG data in memory.
    /// </summary>
    private List<DiscoveredTexture> DiscoverTextures(IGameObject root)
    {
        var textures = new List<DiscoveredTexture>();
        var seen = new HashSet<string>();

        foreach (var editorExt in root.FetchHierarchy())
        {
            if (editorExt is not IGameObject go)
            {
                continue;
            }

            IRenderer? renderer = null;
            if (go.TryGetComponent(out ISkinnedMeshRenderer? smr))
            {
                renderer = smr;
            }
            else if (go.TryGetComponent(out IRenderer? mr))
            {
                renderer = mr;
            }

            if (renderer is null)
            {
                continue;
            }

            foreach (var materialPPtr in renderer.Materials_C25)
            {
                var material = materialPPtr.TryGetAsset(renderer.Collection);
                if (material is null)
                {
                    continue;
                }

                foreach (var (propName, texEnv) in material.GetTextureProperties())
                {
                    if (texEnv.Texture.TryGetAsset(material.Collection) is not ITexture2D texture || string.IsNullOrEmpty(texture.Name))
                    {
                        continue;
                    }

                    if (!seen.Add(texture.Name))
                    {
                        // Same texture referenced by multiple materials — expected for shared textures.
                        // Different textures with the same name would be a data issue upstream.
                        continue;
                    }

                    if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
                    {
                        continue;
                    }

                    using var ms = new MemoryStream();
                    bitmap.SaveAsPng(ms);

                    textures.Add(new DiscoveredTexture
                    {
                        Name = texture.Name,
                        MaterialName = material.Name,
                        SourceMaterialId = $"{material.Collection.Name}:{material.PathID}",
                        Property = propName.ToString(),
                        PngData = ms.ToArray(),
                    });

                    _log.Info($"  Texture: {texture.Name}.png ({propName})");
                }
            }
        }

        return textures;
    }

    /// <summary>
    /// Converts a clean SceneBuilder to a .gltf file with external texture references.
    /// Standard-channel textures are already attached at the MaterialBuilder level (by
    /// BuildCleanScene). This method handles non-standard textures (written to disk,
    /// referenced in material extras) and root extras (cleaned flag).
    /// </summary>
    internal static void SaveGltfPackage(
        SceneBuilder scene,
        List<DiscoveredTexture> textures,
        string gltfPath,
        IReadOnlyList<SourceSkinBinding>? sourceSkinBindings = null)
    {
        var model = scene.ToGltf2();
        var recoveredSkins = FindMissingSkinBindings(model, sourceSkinBindings);
        var recoveredAssignments = CreateRecoveredSkinAssignments(model, recoveredSkins);

        // Images created by MaterialBuilder don't have Name or AlternateWriteFileName.
        // Match them to discovered textures by content so we can set meaningful names.
        var standardTextures = textures
            .Where(t => StandardChannelMap.ContainsKey(t.Property))
            .ToList();
        foreach (var image in model.LogicalImages)
        {
            if (!string.IsNullOrEmpty(image.Name))
            {
                continue;
            }

            var imageContent = image.Content.Content;
            var match = standardTextures.FirstOrDefault(t =>
                imageContent.Length == t.PngData.Length &&
                imageContent.Span.SequenceEqual(t.PngData));
            if (match is not null)
            {
                image.Name = match.Name;
                image.AlternateWriteFileName = $"textures/{match.Name}.png";
            }
        }

        var gltfDir = Path.GetDirectoryName(gltfPath)!;
        var texturesDir = Path.Combine(gltfDir, "textures");

        // Build source material ID → Schema2 Material lookup (for non-standard extras only).
        // Uses the same stable identity embedded by GlbLevelBuilder.
        var materialsBySourceId = new Dictionary<string, Material>(StringComparer.Ordinal);
        foreach (var mat in model.LogicalMaterials)
        {
            if (mat.Extras is JsonObject matExtras &&
                matExtras.TryGetPropertyValue("jiangyu", out var jNode) &&
                jNode is JsonObject jObj &&
                jObj.TryGetPropertyValue("sourceMaterial", out var smNode) &&
                smNode is JsonObject smObj)
            {
                var collection = smObj["collection"]?.GetValue<string>();
                var pathId = smObj["pathId"]?.GetValue<long>();
                if (collection is not null && pathId is not null)
                {
                    materialsBySourceId.TryAdd($"{collection}:{pathId}", mat);
                }
            }
        }

        // Write non-standard textures to disk and add extras references
        var texturesByMaterial = textures
            .Where(t => !StandardChannelMap.ContainsKey(t.Property))
            .GroupBy(t => t.SourceMaterialId, StringComparer.Ordinal);

        foreach (var group in texturesByMaterial)
        {
            var nonStandardTextures = new JsonObject();
            foreach (var tex in group)
            {
                Directory.CreateDirectory(texturesDir);
                File.WriteAllBytes(Path.Combine(texturesDir, $"{tex.Name}.png"), tex.PngData);
                nonStandardTextures[tex.Property] = $"textures/{tex.Name}.png";
            }

            if (nonStandardTextures.Count > 0 &&
                materialsBySourceId.TryGetValue(group.Key, out var material))
            {
                // Merge into existing jiangyu extras (preserving any other fields)
                var materialExtras = material.Extras as JsonObject ?? [];
                var jiangyuObj = materialExtras["jiangyu"] as JsonObject ?? [];
                jiangyuObj["textures"] = nonStandardTextures;
                materialExtras["jiangyu"] = jiangyuObj;
                material.Extras = materialExtras;
            }
        }

        // Strip internal sourceMaterial identity from output — it's pipeline-internal,
        // not useful for modders or the compiler reading the final .gltf
        foreach (var mat in model.LogicalMaterials)
        {
            if (mat.Extras is JsonObject matExtras &&
                matExtras["jiangyu"] is JsonObject jObj)
            {
                jObj.Remove("sourceMaterial");
            }
        }

        // Set root extras
        model.Extras = new JsonObject
        {
            ["jiangyu"] = new JsonObject
            {
                ["cleaned"] = true
            }
        };

        // Save as .gltf with external satellite images
        var settings = new WriteSettings
        {
            ImageWriting = ResourceWriteMode.SatelliteFile
        };
        model.SaveGLTF(gltfPath, settings);

        if (recoveredAssignments.Count > 0)
        {
            ApplyRecoveredSkinAssignmentsToGltf(gltfPath, recoveredAssignments);
        }
    }

    internal static IReadOnlyList<RecoveredSkinBinding> FindMissingSkinBindings(
        ModelRoot model,
        IReadOnlyList<SourceSkinBinding>? sourceSkinBindings = null)
    {
        var recovered = new List<RecoveredSkinBinding>();
        var pathToNodeIndex = BuildRelativePathNodeLookup(model);
        var sourceBindingsByMeshName = sourceSkinBindings?
            .GroupBy(binding => binding.MeshName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is not null)
            {
                continue;
            }

            int requiredJointCount = GetRequiredJointCount(node.Mesh);
            if (requiredJointCount <= 0)
            {
                continue;
            }

            var meshName = node.Mesh.Name;
            var meshNodePath = GetRelativeNodePath(node);
            if (!string.IsNullOrWhiteSpace(meshName) &&
                sourceBindingsByMeshName is not null &&
                sourceBindingsByMeshName.TryGetValue(meshName, out var sourceBindingCandidates))
            {
                var orderedCandidates = sourceBindingCandidates
                    .OrderByDescending(binding => GetPathMatchScore(meshNodePath, binding.MeshNodePath))
                    .ToArray();

                foreach (var candidate in orderedCandidates)
                {
                    if (TryResolveSourceBonePaths(candidate.BonePaths, pathToNodeIndex, out var sourceJointNodeIndices))
                    {
                        recovered.Add(new RecoveredSkinBinding(node.LogicalIndex, sourceJointNodeIndices));
                        goto NextNode;
                    }
                }
            }

            var parent = node.VisualParent;
            if (parent is null)
            {
                continue;
            }

            var hierarchyCandidates = parent.VisualChildren
                .Where(child => child.LogicalIndex != node.LogicalIndex && child.Mesh is null)
                .OrderByDescending(child => string.Equals(child.Name, "root", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(CountNonMeshNodes)
                .ToList();

            foreach (var candidate in hierarchyCandidates)
            {
                var joints = EnumerateNonMeshNodesDepthFirst(candidate)
                    .Take(requiredJointCount)
                    .ToArray();

                if (joints.Length != requiredJointCount)
                {
                    continue;
                }

                // JIANGYU-CONTRACT: Some MENACE vehicle exports include JOINTS_0/WEIGHTS_0
                // but no glTF skin. We recover bindings against the sibling non-mesh
                // hierarchy (preferring node name "root"), in depth-first order.
                recovered.Add(new RecoveredSkinBinding(
                    node.LogicalIndex,
                    [.. joints.Select(j => j.LogicalIndex)]));
                break;
            }

        NextNode:
            continue;
        }

        return recovered;
    }

    private static int GetRequiredJointCount(SharpGLTF.Schema2.Mesh mesh)
    {
        int maxJoint = -1;

        foreach (var primitive in mesh.Primitives)
        {
            var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
            var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
            if (joints is null)
            {
                continue;
            }

            for (int i = 0; i < joints.Count; i++)
            {
                var j = joints[i];
                var w = weights is not null && i < weights.Count ? weights[i] : Vector4.Zero;

                if (w.X > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.X));
                if (w.Y > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.Y));
                if (w.Z > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.Z));
                if (w.W > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.W));
            }
        }

        return maxJoint + 1;
    }

    private static IEnumerable<Node> EnumerateNonMeshNodesDepthFirst(Node root)
    {
        if (root.Mesh is null)
        {
            yield return root;
        }

        foreach (var child in root.VisualChildren)
        {
            foreach (var descendant in EnumerateNonMeshNodesDepthFirst(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountNonMeshNodes(Node root)
    {
        int count = 0;
        foreach (var _ in EnumerateNonMeshNodesDepthFirst(root))
        {
            count++;
        }

        return count;
    }

    private static IReadOnlyList<SourceSkinBinding> CollectSourceSkinBindings(IGameObject root)
    {
        var bindings = new List<SourceSkinBinding>();

        foreach (var asset in root.FetchHierarchy())
        {
            if (asset is not ISkinnedMeshRenderer skinnedMeshRenderer)
            {
                continue;
            }

            IMesh? mesh = skinnedMeshRenderer.MeshP;
            if (mesh is null || !mesh.IsSet() || string.IsNullOrWhiteSpace(mesh.Name))
            {
                continue;
            }

            var rendererGameObject = skinnedMeshRenderer.GameObject_C25P;
            var rendererTransform = rendererGameObject?.GetTransform();
            if (rendererTransform is null)
            {
                continue;
            }

            var meshNodePath = GetRelativeTransformPath(rendererTransform, root);
            if (string.IsNullOrEmpty(meshNodePath))
            {
                continue;
            }

            var bonePaths = skinnedMeshRenderer.BonesP
                .Select(bone => bone is null ? string.Empty : GetRelativeTransformPath(bone, root))
                .ToArray();

            if (bonePaths.Length == 0 || bonePaths.Any(string.IsNullOrEmpty))
            {
                continue;
            }

            bindings.Add(new SourceSkinBinding(mesh.Name, meshNodePath, bonePaths));
        }

        return bindings;
    }

    private static string GetRelativeTransformPath(ITransform transform, IGameObject root)
    {
        var segments = new List<string>();
        ITransform? current = transform;
        ITransform rootTransform = root.GetTransform();

        while (current is not null)
        {
            if (current == rootTransform)
            {
                break;
            }

            var gameObject = current.GameObject_C4P;
            if (gameObject is not null)
            {
                segments.Add(gameObject.Name);
            }

            current = current.Father_C4P;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static Dictionary<string, int> BuildRelativePathNodeLookup(ModelRoot model)
    {
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in model.LogicalNodes)
        {
            var path = GetRelativeNodePath(node);
            if (!string.IsNullOrEmpty(path))
            {
                lookup.TryAdd(path, node.LogicalIndex);
            }
        }

        return lookup;
    }

    private static string GetRelativeNodePath(Node node)
    {
        var segments = new List<string>();
        var current = node;

        while (current.VisualParent is not null)
        {
            if (!string.IsNullOrEmpty(current.Name))
            {
                segments.Add(current.Name);
            }

            current = current.VisualParent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static bool TryResolveSourceBonePaths(
        IReadOnlyList<string> sourceBonePaths,
        IReadOnlyDictionary<string, int> pathToNodeIndex,
        out int[] jointNodeIndices)
    {
        jointNodeIndices = new int[sourceBonePaths.Count];
        for (int i = 0; i < sourceBonePaths.Count; i++)
        {
            var path = sourceBonePaths[i];
            if (string.IsNullOrEmpty(path) || !pathToNodeIndex.TryGetValue(path, out var nodeIndex))
            {
                jointNodeIndices = [];
                return false;
            }

            jointNodeIndices[i] = nodeIndex;
        }

        return true;
    }

    private static int GetPathMatchScore(string targetPath, string candidatePath)
    {
        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(candidatePath))
        {
            return 0;
        }

        var targetSegments = targetPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        var candidateSegments = candidatePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (targetSegments.Length == candidateSegments.Length &&
            targetSegments.SequenceEqual(candidateSegments, StringComparer.Ordinal))
        {
            return 10_000 + targetSegments.Length;
        }

        int prefixLength = 0;
        int maxPrefix = Math.Min(targetSegments.Length, candidateSegments.Length);
        while (prefixLength < maxPrefix &&
               string.Equals(targetSegments[prefixLength], candidateSegments[prefixLength], StringComparison.Ordinal))
        {
            prefixLength++;
        }

        int suffixLength = 0;
        int maxSuffix = Math.Min(targetSegments.Length, candidateSegments.Length);
        while (suffixLength < maxSuffix &&
               string.Equals(
                   targetSegments[targetSegments.Length - 1 - suffixLength],
                   candidateSegments[candidateSegments.Length - 1 - suffixLength],
                   StringComparison.Ordinal))
        {
            suffixLength++;
        }

        return (prefixLength * 100) + suffixLength;
    }

    internal static IReadOnlyList<RecoveredSkinAssignment> CreateRecoveredSkinAssignments(
        ModelRoot model,
        IReadOnlyList<RecoveredSkinBinding> recoveredSkins)
    {
        var assignments = new List<RecoveredSkinAssignment>(recoveredSkins.Count);
        if (recoveredSkins.Count == 0)
        {
            return assignments;
        }

        var skinCache = new Dictionary<string, Skin>(StringComparer.Ordinal);

        foreach (var recovered in recoveredSkins)
        {
            if (recovered.NodeIndex < 0 || recovered.NodeIndex >= model.LogicalNodes.Count)
            {
                continue;
            }

            var node = model.LogicalNodes[recovered.NodeIndex];
            if (node.Mesh is null || node.Skin is not null)
            {
                continue;
            }

            var jointNodes = new List<Node>(recovered.JointNodeIndices.Length);
            bool valid = true;
            foreach (int jointIndex in recovered.JointNodeIndices)
            {
                if (jointIndex < 0 || jointIndex >= model.LogicalNodes.Count)
                {
                    valid = false;
                    break;
                }

                jointNodes.Add(model.LogicalNodes[jointIndex]);
            }

            if (!valid || jointNodes.Count == 0)
            {
                continue;
            }

            var meshBindTransform = node.WorldMatrix;
            var cacheKey = $"{string.Join(",", recovered.JointNodeIndices)}|{GetMatrixCacheKey(meshBindTransform)}";
            if (!skinCache.TryGetValue(cacheKey, out var skin))
            {
                skin = model.CreateSkin();
                skin.BindJoints(meshBindTransform, [.. jointNodes]);
                skinCache[cacheKey] = skin;
            }

            assignments.Add(new RecoveredSkinAssignment(node.LogicalIndex, skin.LogicalIndex));
        }

        return assignments;
    }

    private static string GetMatrixCacheKey(Matrix4x4 matrix)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{matrix.M11:R},{matrix.M12:R},{matrix.M13:R},{matrix.M14:R}|{matrix.M21:R},{matrix.M22:R},{matrix.M23:R},{matrix.M24:R}|{matrix.M31:R},{matrix.M32:R},{matrix.M33:R},{matrix.M34:R}|{matrix.M41:R},{matrix.M42:R},{matrix.M43:R},{matrix.M44:R}");
    }

    internal static void ApplyRecoveredSkinAssignmentsToGltf(
        string gltfPath,
        IReadOnlyList<RecoveredSkinAssignment> assignments)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        JsonNode? rootNode = JsonNode.Parse(File.ReadAllText(gltfPath));
        if (rootNode is not JsonObject root || root["nodes"] is not JsonArray nodes)
        {
            return;
        }

        foreach (var assignment in assignments)
        {
            if (assignment.NodeIndex < 0 || assignment.NodeIndex >= nodes.Count)
            {
                continue;
            }

            if (nodes[assignment.NodeIndex] is JsonObject nodeObject)
            {
                nodeObject["skin"] = assignment.SkinIndex;
            }
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        File.WriteAllText(gltfPath, root.ToJsonString(options));
    }

    private void ExportMeshAsGlb(IMesh mesh, string outputPath)
    {
        SceneBuilder sceneBuilder = GlbMeshBuilder.Build(mesh);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        if (GlbWriter.TryWrite(sceneBuilder, stream, out string? errorMessage))
        {
            _log.Info($"Exported to: {outputPath}");
        }
        else
        {
            _log.Error($"Error writing GLB: {errorMessage}");
        }
    }
}
