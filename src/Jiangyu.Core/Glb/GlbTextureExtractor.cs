using SharpGLTF.Schema2;

namespace Jiangyu.Core.Glb;

/// <summary>
/// Pulls textures out of a glTF-family source (.gltf or .glb): standard PBR
/// channel slots first, then non-standard textures declared in Jiangyu's
/// <c>jiangyu.textures</c> material extras. Falls back to filename-prefix
/// directory scanning when the source carries no usable image content.
///
/// <para>Texture-naming heuristics live here too: MENACE-pattern detection,
/// linear-colour-space marker detection (normal/mask/effect maps), prefix
/// derivation across a multi-texture material set.</para>
/// </summary>
internal static class GlbTextureExtractor
{
    public static List<GlbMeshBundleCompiler.CompiledTexture> ExtractTextures(
        IReadOnlyList<GlbMeshBundleCompiler.MeshSourceEntry> entries,
        Func<string, ModelRoot?>? cachedModelLookup = null)
    {
        var result = new Dictionary<string, GlbMeshBundleCompiler.CompiledTexture>(StringComparer.Ordinal);
        foreach (var sourceFilePath in entries.Select(entry => entry.SourceFilePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cachedModel = cachedModelLookup?.Invoke(sourceFilePath);

            // Prefer glTF-family material graph when available (.gltf or .glb)
            if (cachedModel != null
                ? TryExtractTexturesFromModelGraph(sourceFilePath, cachedModel, result)
                : TryExtractTexturesFromModelGraph(sourceFilePath, result))
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

                result[name] = new GlbMeshBundleCompiler.CompiledTexture
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
    /// Only explicitly declared textures count: standard material channels
    /// plus Jiangyu material extras.
    /// </summary>
    public static bool TryExtractTexturesFromModelGraph(string sourceFilePath, Dictionary<string, GlbMeshBundleCompiler.CompiledTexture> result)
    {
        if (!IsGltfFamilySource(sourceFilePath))
            return false;

        ModelRoot model;
        try
        {
            model = ModelRoot.Load(sourceFilePath);
        }
        catch
        {
            return false;
        }

        return TryExtractTexturesFromModelGraph(sourceFilePath, model, result);
    }

    private static bool TryExtractTexturesFromModelGraph(string sourceFilePath, ModelRoot model, Dictionary<string, GlbMeshBundleCompiler.CompiledTexture> result)
    {
        if (!IsGltfFamilySource(sourceFilePath))
            return false;

        var sourceDir = Path.GetDirectoryName(sourceFilePath);
        if (sourceDir is null)
            return false;

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

                result[name] = new GlbMeshBundleCompiler.CompiledTexture
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

                    result[name] = new GlbMeshBundleCompiler.CompiledTexture
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

    public static string GetImageName(Image image)
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

    public static string? InferTexturePrefix(string sourceFilePath)
    {
        // For glTF-family files: derive from image names in the material graph when possible.
        if (IsGltfFamilySource(sourceFilePath))
        {
            try
            {
                var textures = new Dictionary<string, GlbMeshBundleCompiler.CompiledTexture>(StringComparer.Ordinal);
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

    public static string? FindCommonPrefix(List<string> names)
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

    public static string NormalizeTexturePrefix(string name)
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
    public static bool IsCleanedExport(string sourceFilePath)
    {
        if (!IsGltfFamilySource(sourceFilePath))
            return false;

        try
        {
            var model = ModelRoot.Load(sourceFilePath);
            return IsCleanedExport(model);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCleanedExport(ModelRoot model)
    {
        if (model.Extras is System.Text.Json.Nodes.JsonObject rootExtras &&
            rootExtras.TryGetPropertyValue("jiangyu", out var jiangyuNode) &&
            jiangyuNode is System.Text.Json.Nodes.JsonObject jiangyuObj &&
            jiangyuObj.TryGetPropertyValue("cleaned", out var cleanedNode))
        {
            return cleanedNode?.GetValue<bool>() ?? false;
        }
        return false;
    }

    public static bool IsGltfFamilySource(string sourceFilePath)
        => string.Equals(Path.GetExtension(sourceFilePath), ".gltf", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetExtension(sourceFilePath), ".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback texture discovery for non-glTF-family sources (prefix-based directory search).
    /// </summary>
    public static IEnumerable<string> EnumerateSidecarTexturePaths(string sourceFilePath, string texturePrefix)
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

    public static bool IsLinearTextureName(string textureName)
        => textureName.EndsWith("_MaskMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_NormalMap", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_Normal", StringComparison.OrdinalIgnoreCase) ||
           textureName.EndsWith("_EffectMap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if a texture filename matches a known MENACE texture naming pattern.
    /// Used by the naming inference step to avoid importing unrelated files from textures/.
    /// </summary>
    public static bool IsMenaceTextureNamePattern(string textureName)
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
}
