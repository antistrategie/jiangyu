using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class ReplacementMesh
{
    public Mesh Mesh;
    public string[] BoneNames;
    public CompiledMaterialBindingRecord[] MaterialBindings;

    public ReplacementMesh(Mesh mesh, string[] boneNames, CompiledMaterialBindingRecord[] materialBindings)
    {
        Mesh = mesh;
        BoneNames = boneNames;
        MaterialBindings = materialBindings;
    }
}

internal sealed class ReplacementPrefab
{
    public GameObject Template;
    public string PrefabName;
    public string[] BoneNames;

    public ReplacementPrefab(GameObject template, string prefabName, string[] boneNames)
    {
        Template = template;
        PrefabName = prefabName;
        BoneNames = boneNames;
    }
}

internal sealed class CompiledMeshMetadataRecord
{
    public string[] BoneNames;
    public CompiledMaterialBindingRecord[] MaterialBindings;
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
