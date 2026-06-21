using Jiangyu.Shared.Bundles;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class ReplacementMesh
{
    public Mesh Mesh;
    public string[] BoneNames;
    public IReadOnlyList<CompiledMaterialBinding> MaterialBindings;
    public string TargetRendererPath;
    public string TargetEntityName;
    // Set once PrefabMeshRebindApplier has rebound the SMR on the source
    // prefab. Persists across scene loads because the prefab template (and
    // its now-[jiangyu]-marked sharedMesh) survives scene unload.
    public bool AppliedToPrefab;

    public ReplacementMesh(
        Mesh mesh,
        string[] boneNames,
        IReadOnlyList<CompiledMaterialBinding> materialBindings,
        string targetRendererPath,
        string targetEntityName = null)
    {
        Mesh = mesh;
        BoneNames = boneNames;
        MaterialBindings = materialBindings;
        TargetRendererPath = targetRendererPath;
        TargetEntityName = targetEntityName;
    }
}

internal sealed class PreparedMeshAssignment
{
    public Mesh Mesh;
    public Bounds Bounds;
    public bool UpdateWhenOffscreen;
}
