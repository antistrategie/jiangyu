using Jiangyu.Core.Glb;
using Jiangyu.Shared.Replacements;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Compile;

internal static class PortraitTexturePolicy
{
    public static void Apply(
        IEnumerable<GlbMeshBundleCompiler.CompiledTexture> textures,
        IEnumerable<CompiledTemplatePatch>? patches)
    {
        // JIANGYU-CONTRACT: these SpeakerTemplate fields hold the standing portraits
        // shared by the conversation and squad UI. Sampling follows their KDL usage,
        // so asset filenames and mod-specific character tags do not determine it.
        var portraitNames = (patches ?? [])
            .Where(patch => patch.TemplateType is "SpeakerTemplate" or "Il2CppMenace.Conversations.SpeakerTemplate")
            .SelectMany(patch => patch.Set)
            .Where(op => op.Op == CompiledTemplateOp.Set
                && op.Descent is not { Count: > 0 }
                && op.FieldPath is "StandLookLeftImage" or "StandLookRightImage" or "StandLookRightInactiveImage"
                && op.Value is { Kind: CompiledTemplateValueKind.AssetReference, Asset: not null })
            .Select(op => AssetCategory.ToBundleAssetName(op.Value!.Asset!.Name))
            .ToHashSet(StringComparer.Ordinal);

        // Replacements copy pixels into an existing game texture and retain its sampling.
        foreach (var texture in textures)
            texture.IsStandingPortrait = texture.IsAddition && portraitNames.Contains(texture.Name);
    }
}
