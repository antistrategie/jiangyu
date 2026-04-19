using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class ReplacementMesh
{
    public Mesh Mesh;
    public string[] BoneNames;
    public CompiledMaterialBindingRecord[] MaterialBindings;
    public string TargetRendererPath;
    public string TargetEntityName;
    public long TargetEntityPathId;
    public bool HasTargetEntityPathId;

    public ReplacementMesh(
        Mesh mesh,
        string[] boneNames,
        CompiledMaterialBindingRecord[] materialBindings,
        string targetRendererPath,
        string targetEntityName = null,
        long targetEntityPathId = 0,
        bool hasTargetEntityPathId = false)
    {
        Mesh = mesh;
        BoneNames = boneNames;
        MaterialBindings = materialBindings;
        TargetRendererPath = targetRendererPath;
        TargetEntityName = targetEntityName;
        TargetEntityPathId = targetEntityPathId;
        HasTargetEntityPathId = hasTargetEntityPathId;
    }
}

internal sealed class ReplacementPrefab
{
    public GameObject Template;
    public string PrefabName;
    public string[] BoneNames;
    public string TargetRendererPath;
    public string TargetEntityName;
    public long TargetEntityPathId;
    public bool HasTargetEntityPathId;

    public ReplacementPrefab(
        GameObject template,
        string prefabName,
        string[] boneNames,
        string targetRendererPath,
        string targetEntityName = null,
        long targetEntityPathId = 0,
        bool hasTargetEntityPathId = false)
    {
        Template = template;
        PrefabName = prefabName;
        BoneNames = boneNames;
        TargetRendererPath = targetRendererPath;
        TargetEntityName = targetEntityName;
        TargetEntityPathId = targetEntityPathId;
        HasTargetEntityPathId = hasTargetEntityPathId;
    }
}

internal sealed class CompiledMeshMetadataRecord
{
    public string[] BoneNames;
    public CompiledMaterialBindingRecord[] MaterialBindings;
    public string TargetRendererPath;
    public string TargetMeshName;
    public string TargetEntityName;
    public long TargetEntityPathId;
    public bool HasTargetEntityPathId;
}

internal sealed class CompiledMaterialBindingRecord
{
    public int Slot;
    public string Name;
    public Dictionary<string, string> Textures;
}

internal sealed class PreparedMeshAssignment
{
    public Mesh Mesh;
    public Bounds Bounds;
    public bool UpdateWhenOffscreen;
}
