using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class MaterialReplacementService
{
    private readonly Dictionary<string, Texture2D> _replacementTextures;
    private readonly Dictionary<string, Material[]> _materialCache = new(StringComparer.Ordinal);
    private readonly List<UnityEngine.Object> _pinned;

    public bool HasReplacementTextures => _replacementTextures.Count > 0;

    public MaterialReplacementService(Dictionary<string, Texture2D> replacementTextures, List<UnityEngine.Object> pinned)
    {
        _replacementTextures = replacementTextures;
        _pinned = pinned;
    }

    public Material[] GetOrCreateReplacementMaterials(Material[] sourceMaterials, string texturePrefix)
    {
        if (sourceMaterials == null || sourceMaterials.Length == 0 || string.IsNullOrWhiteSpace(texturePrefix))
            return sourceMaterials;

        var keyParts = new List<string> { texturePrefix, sourceMaterials.Length.ToString() };
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            var material = sourceMaterials[i];
            keyParts.Add(material != null ? material.GetInstanceID().ToString() : "null");
            keyParts.Add(GetTextureName(material, "_BaseColorMap"));
            keyParts.Add(GetTextureName(material, "_MaskMap"));
            keyParts.Add(GetTextureName(material, "_NormalMap"));
            keyParts.Add(GetTextureName(material, "_Effect_Map"));
        }

        var cacheKey = string.Join("|", keyParts);
        if (_materialCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var clones = new Material[sourceMaterials.Length];
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            var sourceMaterial = sourceMaterials[i];
            if (sourceMaterial == null)
                continue;

            var clone = UnityEngine.Object.Instantiate(sourceMaterial);
            clone.name = $"{sourceMaterial.name} [jiangyu:{texturePrefix}]";
            _pinned.Add(clone);

            TryApplyReplacementTexture(clone, "_BaseColorMap", texturePrefix);
            TryApplyReplacementTexture(clone, "_MaskMap", texturePrefix);
            TryApplyReplacementTexture(clone, "_NormalMap", texturePrefix);
            TryApplyReplacementTexture(clone, "_Effect_Map", texturePrefix);

            clones[i] = clone;
        }

        _materialCache[cacheKey] = clones;
        return clones;
    }

    private bool TryApplyReplacementTexture(Material material, string propertyName, string texturePrefix)
    {
        if (material == null || string.IsNullOrWhiteSpace(texturePrefix) || !material.HasProperty(propertyName))
            return false;

        var currentTextureName = GetTextureName(material, propertyName);
        foreach (var replacementTextureName in ResolveReplacementTextureNames(texturePrefix, currentTextureName, propertyName))
        {
            if (_replacementTextures.TryGetValue(replacementTextureName, out var replacementTexture))
            {
                material.SetTexture(propertyName, replacementTexture);
                return true;
            }
        }

        return false;
    }

    private static string GetTextureName(Material material, string propertyName)
    {
        if (material == null || !material.HasProperty(propertyName))
            return string.Empty;

        return material.GetTexture(propertyName)?.name ?? string.Empty;
    }

    private static IEnumerable<string> ResolveReplacementTextureNames(string texturePrefix, string currentTextureName, string propertyName)
    {
        var mapSuffix = propertyName switch
        {
            "_BaseColorMap" => "BaseMap",
            "_MaskMap" => "MaskMap",
            "_NormalMap" => "NormalMap",
            "_Effect_Map" => "EffectMap",
            _ => null,
        };

        if (mapSuffix == null)
            yield break;

        if (!string.IsNullOrWhiteSpace(currentTextureName))
        {
            var variantMatch = System.Text.RegularExpressions.Regex.Match(
                currentTextureName,
                $"^(.*?)(_(\\d+))?_{mapSuffix}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (variantMatch.Success)
            {
                var variantSuffix = variantMatch.Groups[2].Success ? variantMatch.Groups[2].Value : string.Empty;
                if (!string.IsNullOrEmpty(variantSuffix))
                    yield return $"{texturePrefix}{variantSuffix}_{mapSuffix}";
            }
        }

        yield return $"{texturePrefix}_{mapSuffix}";
    }
}
