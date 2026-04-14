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

    public Material[] GetOrCreateReplacementMaterials(Material[] sourceMaterials, CompiledMaterialBindingRecord[] materialBindings)
    {
        if (sourceMaterials == null || sourceMaterials.Length == 0 || materialBindings == null || materialBindings.Length == 0)
            return sourceMaterials;

        var bindingsBySlot = materialBindings
            .GroupBy(binding => binding.Slot)
            .ToDictionary(group => group.Key, group => group.First());

        var keyParts = new List<string> { sourceMaterials.Length.ToString() };
        foreach (var binding in materialBindings.OrderBy(binding => binding.Slot))
        {
            keyParts.Add(binding.Slot.ToString());
            keyParts.Add(binding.Name ?? string.Empty);
            foreach (var texture in binding.Textures.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                keyParts.Add(texture.Key);
                keyParts.Add(texture.Value);
            }
        }

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

            if (!bindingsBySlot.TryGetValue(i, out var binding) || binding.Textures == null || binding.Textures.Count == 0)
            {
                clones[i] = sourceMaterial;
                continue;
            }

            Material clone = null;
            foreach (var texture in binding.Textures)
            {
                if (!TryApplyReplacementTexture(ref clone, sourceMaterial, texture.Key, texture.Value, binding.Name))
                    continue;
            }

            clones[i] = clone ?? sourceMaterial;
        }

        _materialCache[cacheKey] = clones;
        return clones;
    }

    private bool TryApplyReplacementTexture(ref Material clone, Material sourceMaterial, string propertyName, string replacementTextureName, string materialName)
    {
        if (sourceMaterial == null ||
            string.IsNullOrWhiteSpace(propertyName) ||
            string.IsNullOrWhiteSpace(replacementTextureName) ||
            !sourceMaterial.HasProperty(propertyName) ||
            !_replacementTextures.TryGetValue(replacementTextureName, out var replacementTexture))
        {
            return false;
        }

        clone ??= CreateMaterialClone(sourceMaterial, materialName);
        clone.SetTexture(propertyName, replacementTexture);
        return true;
    }

    private Material CreateMaterialClone(Material sourceMaterial, string materialName)
    {
        var clone = UnityEngine.Object.Instantiate(sourceMaterial);
        var bindingLabel = string.IsNullOrWhiteSpace(materialName) ? sourceMaterial.name : materialName;
        clone.name = $"{sourceMaterial.name} [jiangyu:{bindingLabel}]";
        _pinned.Add(clone);
        return clone;
    }

    private static string GetTextureName(Material material, string propertyName)
    {
        if (material == null || !material.HasProperty(propertyName))
            return string.Empty;

        return material.GetTexture(propertyName)?.name ?? string.Empty;
    }
}
