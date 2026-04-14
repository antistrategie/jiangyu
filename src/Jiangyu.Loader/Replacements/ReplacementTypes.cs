using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class ReplacementMesh
{
    public Mesh Mesh;
    public string[] BoneNames;
    public string TexturePrefix;

    public ReplacementMesh(Mesh mesh, string[] boneNames, string texturePrefix)
    {
        Mesh = mesh;
        BoneNames = boneNames;
        TexturePrefix = texturePrefix;
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
    public string TexturePrefix;
}

internal sealed class PreparedMeshAssignment
{
    public Mesh Mesh;
    public Bounds Bounds;
    public bool UpdateWhenOffscreen;
}
