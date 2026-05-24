using System.Diagnostics;
using System.Numerics;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Bundles;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Glb;

/// <summary>
/// Vertex-level mesh extraction from glTF sources. Two responsibilities:
///
/// <list type="bullet">
///   <item><b>Per-source model loading</b>: open the glTF/GLB once per
///     source file, build per-node candidate metadata
///     (<see cref="ExtractedMeshCandidate"/>), detect the cleaned-export
///     flag, attach companion skin data when MENACE's mesh-only GLBs need
///     one.</item>
///   <item><b>Per-entry mesh extraction</b>: walk each requested
///     <see cref="GlbMeshBundleCompiler.MeshSourceEntry"/>, pick the
///     matching candidate, run <see cref="ExtractMesh"/> with the
///     vertex-space mode decided from authored vs target scale ratios,
///     bake the node transform when the mesh isn't directly skin-bound,
///     emit a <see cref="GlbMeshBundleCompiler.CompiledMesh"/>.</item>
/// </list>
///
/// <para>The bind-pose retargeter (<see cref="BindPoseRetargeter"/>) runs
/// inside <see cref="ExtractMesh"/> when the entry specifies a reference
/// rig. Reference rigs are cached per call set via
/// <see cref="_referenceModelCache"/>; the cache is process-static but
/// gets cleared by <see cref="ClearReferenceCache"/> at the end of each
/// <see cref="GlbMeshBundleCompiler.BuildAsync"/> invocation so stale
/// references can't leak across compiles.</para>
/// </summary>
internal static class GlbMeshExtractor
{
    private const float MenaceCharacterSpaceScale = 100f;
    private const float MeshOnlyWeightedVertexScale = 0.01f;
    private const float MeshOnlyWeightScaleExtentThreshold = 10f;

    internal sealed class ExtractedMeshCandidate
    {
        public required string PrimaryName { get; init; }
        public required HashSet<string> Aliases { get; init; }
        public required Node Node { get; init; }
        public required List<CompiledMaterialBinding> MaterialBindings { get; init; }
    }

    /// <summary>Cached per-source-file context to avoid redundant <see cref="ModelRoot.Load"/> calls.</summary>
    internal sealed class LoadedModelContext
    {
        public required ModelRoot Model { get; init; }
        public required bool IsCleaned { get; init; }
        public required GlbMeshBundleCompiler.CompanionSkinData? CompanionSkinData { get; init; }
        public required List<ExtractedMeshCandidate> Candidates { get; init; }
    }

    private sealed class SkinBindingData
    {
        public required Matrix4x4[] SourceBindPoses { get; init; }
        public required Matrix4x4[] CompiledBindPoses { get; init; }
        public required string[] BoneNames { get; init; }
        public required string?[] ParentNames { get; init; }
    }

    public static Dictionary<string, LoadedModelContext> LoadModelContexts(IReadOnlyList<GlbMeshBundleCompiler.MeshSourceEntry> entries)
    {
        var contexts = new Dictionary<string, LoadedModelContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in entries.GroupBy(e => e.SourceFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = group.Key;
            var model = ModelRoot.Load(path);
            var companionSkinData = TryLoadCompanionSkinData(path);
            var isCleaned = GlbTextureExtractor.IsCleanedExport(model);
            var candidates = model.LogicalNodes
                .Where(node => node.Mesh != null)
                .Select(node => BuildLightweightCandidate(node))
                .ToList();

            contexts[path] = new LoadedModelContext
            {
                Model = model,
                IsCleaned = isCleaned,
                CompanionSkinData = companionSkinData,
                Candidates = candidates,
            };
        }
        return contexts;
    }

    public static List<GlbMeshBundleCompiler.CompiledMesh> ExtractMeshes(
        IReadOnlyList<GlbMeshBundleCompiler.MeshSourceEntry> entries,
        Dictionary<string, LoadedModelContext> contexts,
        out Dictionary<string, List<CompiledMaterialBinding>> materialBindings,
        ILogSink? log = null)
    {
        var extracted = new List<GlbMeshBundleCompiler.CompiledMesh>();
        materialBindings = new Dictionary<string, List<CompiledMaterialBinding>>(StringComparer.Ordinal);
        var bySource = entries
            .GroupBy(e => e.SourceFilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in bySource)
        {
            var ctx = contexts[group.Key];
            var extractedCandidates = ctx.Candidates;

            if (extractedCandidates.Count == 0)
            {
                var available = string.Join(", ", ctx.Model.LogicalMeshes.Select(m => m.Name ?? $"Mesh_{m.LogicalIndex}").OrderBy(n => n, StringComparer.Ordinal));
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

                var meshSw = Stopwatch.StartNew();
                var mesh = match.Node.Mesh ?? throw new InvalidOperationException("Matched mesh candidate has no mesh.");
                var compiled = ExtractMesh(
                    mesh,
                    ctx.Model,
                    match.Node,
                    ctx.CompanionSkinData,
                    ctx.IsCleaned,
                    entry.BindPoseReferencePath,
                    entry.BundleMeshName,
                    entry.TargetRendererPath,
                    entry.TargetMeshMaxHalfExtent)
                    ?? throw new InvalidOperationException($"Matched mesh '{entry.BundleMeshName}' in '{group.Key}' could not be extracted.");

                extracted.Add(CloneMeshWithName(compiled, entry.BundleMeshName));
                materialBindings[entry.BundleMeshName] = match.MaterialBindings;
                log?.Info($"  [timing]     {entry.BundleMeshName}: {meshSw.Elapsed.TotalSeconds:F1}s");
            }
        }

        return extracted;
    }

    /// <summary>
    /// Lightweight candidate builder — computes aliases and material bindings only,
    /// without running the expensive vertex-level <see cref="ExtractMesh"/> pass.
    /// </summary>
    private static ExtractedMeshCandidate BuildLightweightCandidate(Node node)
    {
        var mesh = node.Mesh!;
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

    private static GlbMeshBundleCompiler.CompiledMesh CloneMeshWithName(GlbMeshBundleCompiler.CompiledMesh mesh, string name)
    {
        return new GlbMeshBundleCompiler.CompiledMesh
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

                textures[propertyName] = GlbTextureExtractor.GetImageName(image);
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

    private static GlbMeshBundleCompiler.CompiledMesh? ExtractMesh(
        Mesh mesh,
        ModelRoot model,
        Node meshNode,
        GlbMeshBundleCompiler.CompanionSkinData? companionSkinData,
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
        var subMeshes = new List<GlbMeshBundleCompiler.SubMeshInfo>();

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

            subMeshes.Add(new GlbMeshBundleCompiler.SubMeshInfo
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

            var authoredContract = new BindPoseRetargeter.SkeletonContract
            {
                BoneNames = boneNames,
                ParentNames = parentNames,
                BindPoses = authoredSkinData.SourceBindPoses,
            };
            var referenceMeshSelector = string.IsNullOrWhiteSpace(bindPoseReferenceSelector)
                ? bundleMeshName
                : bindPoseReferenceSelector;
            var referenceSkin = LoadReferenceSkinBindingData(bindPoseReferencePath!, referenceMeshSelector);
            var referenceContract = new BindPoseRetargeter.SkeletonContract
            {
                BoneNames = referenceSkin.BoneNames,
                ParentNames = referenceSkin.ParentNames,
                BindPoses = referenceSkin.SourceBindPoses,
            };

            var alignedReferenceBindPoses = boneNames
                .Select(name => referenceSkin.CompiledBindPoses[FindBoneIndex(referenceSkin.BoneNames, name)])
                .ToArray();

            if (BindPoseRetargeter.NeedsRetarget(authoredContract, referenceContract))
            {
                var retargeted = BindPoseRetargeter.Retarget(
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

        return new GlbMeshBundleCompiler.CompiledMesh
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

    private static readonly Dictionary<string, (ModelRoot Model, bool IsCleaned)> _referenceModelCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drop every cached reference model. Called from
    /// <see cref="GlbMeshBundleCompiler.BuildAsync"/>'s finally so a stale
    /// reference from one compile can't leak into the next.
    /// </summary>
    public static void ClearReferenceCache() => _referenceModelCache.Clear();

    private static SkinBindingData LoadReferenceSkinBindingData(string referencePath, string referenceMeshSelector)
    {
        if (!_referenceModelCache.TryGetValue(referencePath, out var cached))
        {
            var model = ModelRoot.Load(referencePath);
            cached = (model, GlbTextureExtractor.IsCleanedExport(model));
            _referenceModelCache[referencePath] = cached;
        }

        var candidates = cached.Model.LogicalNodes
            .Where(node => node.Mesh != null)
            .Select(BuildLightweightCandidate)
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
                if (MeshRendererPathResolver.TryStripBlenderNumericSuffix(normalised, out var strippedBlender, out _))
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

    private static GlbMeshBundleCompiler.CompanionSkinData? TryLoadCompanionSkinData(string sourceFilePath)
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

        return new GlbMeshBundleCompiler.CompanionSkinData
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
}
