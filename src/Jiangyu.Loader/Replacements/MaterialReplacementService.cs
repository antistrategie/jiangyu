using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Applies compiled per-material texture bindings by mutating the game's existing
/// material textures in place, rather than cloning <see cref="Material"/> and
/// swapping <c>sharedMaterials</c>. Cloning via <c>Object.Instantiate</c> captures
/// shader-keyword state at clone time, which mismatches rendering when the clone
/// was created in one scene and consumed in another (observed as pink vehicles
/// after opening the squad viewer then entering a mission). Mutating the texture
/// content that the game material already references preserves every shader
/// variant the game's own material setup established.
/// </summary>
internal sealed class MaterialReplacementService
{
    private readonly Dictionary<string, Texture2D> _replacementTextures;
    private readonly HashSet<int> _mutatedInstanceIds = new();
    private readonly HashSet<int> _failedInstanceIds = new();

    public bool HasReplacementTextures => _replacementTextures.Count > 0;

    public MaterialReplacementService(Dictionary<string, Texture2D> replacementTextures)
    {
        _replacementTextures = replacementTextures;
    }

    public void ApplyBindings(
        MelonLogger.Instance log,
        Material[] sourceMaterials,
        CompiledMaterialBindingRecord[] materialBindings)
    {
        if (sourceMaterials == null || sourceMaterials.Length == 0)
            return;
        if (materialBindings == null || materialBindings.Length == 0)
            return;

        foreach (var binding in materialBindings)
        {
            if (binding == null || binding.Textures == null || binding.Textures.Count == 0)
                continue;
            if (binding.Slot < 0 || binding.Slot >= sourceMaterials.Length)
                continue;

            var sourceMaterial = sourceMaterials[binding.Slot];
            if (sourceMaterial == null)
                continue;

            foreach (var texture in binding.Textures)
            {
                ApplyPropertyBinding(log, sourceMaterial, binding.Name, texture.Key, texture.Value);
            }
        }
    }

    private void ApplyPropertyBinding(
        MelonLogger.Instance log,
        Material sourceMaterial,
        string materialName,
        string propertyName,
        string replacementTextureName)
    {
        if (string.IsNullOrWhiteSpace(propertyName) ||
            string.IsNullOrWhiteSpace(replacementTextureName) ||
            !sourceMaterial.HasProperty(propertyName))
        {
            return;
        }

        if (!_replacementTextures.TryGetValue(replacementTextureName, out var replacement) || replacement == null)
            return;

        var destinationTexture = sourceMaterial.GetTexture(propertyName);
        var destination = destinationTexture?.TryCast<Texture2D>();
        if (destination == null)
        {
            log.Warning(
                $"  [DIAG] material '{materialName ?? sourceMaterial.name}' has no Texture2D at property '{propertyName}' to mutate; cannot apply replacement '{replacementTextureName}'.");
            return;
        }

        if (destination.GetInstanceID() == replacement.GetInstanceID())
            return;

        var instanceId = destination.GetInstanceID();
        if (_mutatedInstanceIds.Contains(instanceId) || _failedInstanceIds.Contains(instanceId))
            return;

        if (TextureMutationHelpers.MutateInPlace(replacement, destination, log))
        {
            _mutatedInstanceIds.Add(instanceId);
            log.Msg(
                $"  Bound replacement '{replacementTextureName}' -> '{destination.name}' on material '{materialName ?? sourceMaterial.name}' property '{propertyName}' ({destination.width}x{destination.height}, {destination.format})");
        }
        else
        {
            _failedInstanceIds.Add(instanceId);
        }
    }
}
