using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Jiangyu.Core.Glb;

/// <summary>
/// Binary serialisation of the staging files that <see cref="GlbMeshBundleCompiler"/>
/// hands to the Unity batchmode build step. Three formats:
/// <list type="bullet">
///   <item><b>MESH</b> (<c>0x4D455348</c>): vertices, normals, tangents, UVs,
///     colours, skinning, sub-meshes, bind poses. One stream covers every
///     mesh in the build, length-prefixed by mesh count.</item>
///   <item><b>TXTR</b> (<c>0x54585452</c>): texture payload by name + linear
///     flag + raw PNG/JPG bytes.</item>
///   <item><b>TRCT</b> (<c>0x54435254</c>): per-mesh contracts (bone-name
///     hashes, bind poses) the Unity build replays to validate the
///     extracted skinning matches the target's expected layout.</item>
/// </list>
///
/// <para>Jiangyu-owned formats, not discovered MENACE contract values.</para>
/// </summary>
internal static class MeshBundleStager
{
    private const uint MeshMagic = 0x4D455348; // "MESH"
    private const uint TextureMagic = 0x54585452; // "TXTR"
    private const uint ContractMagic = 0x54435254; // "TRCT"

    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public static void WriteMeshData(string path, List<GlbMeshBundleCompiler.CompiledMesh> meshes)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(MeshMagic);
        writer.Write(1);
        writer.Write(meshes.Count);

        foreach (var mesh in meshes)
        {
            var vertexCount = mesh.Vertices.Length / 3;
            var hasNormals = mesh.Normals.Length == vertexCount * 3;
            var hasTangents = mesh.Tangents.Length == vertexCount * 4;
            var hasUv0 = mesh.UV0.Length == vertexCount * 2;
            var hasUv1 = mesh.UV1.Length == vertexCount * 2;
            var hasColors = mesh.Colors.Length == vertexCount * 4;
            var hasSkinning = mesh.BoneWeights.Length == vertexCount * 4 &&
                              mesh.BoneIndices.Length == vertexCount * 4;

            var nameBytes = Encoding.UTF8.GetBytes(mesh.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            writer.Write(vertexCount);
            writer.Write(mesh.Indices.Length);
            writer.Write(mesh.SubMeshes.Count);

            byte flags = 0;
            if (hasNormals) flags |= 0x01;
            if (hasTangents) flags |= 0x02;
            if (hasUv0) flags |= 0x04;
            if (hasUv1) flags |= 0x08;
            if (hasColors) flags |= 0x10;
            if (hasSkinning) flags |= 0x20;
            writer.Write(flags);

            WriteFloatArray(writer, mesh.Vertices);
            if (hasNormals) WriteFloatArray(writer, mesh.Normals);
            if (hasTangents) WriteFloatArray(writer, mesh.Tangents);
            if (hasUv0) WriteFloatArray(writer, mesh.UV0);
            if (hasUv1) WriteFloatArray(writer, mesh.UV1);

            if (hasColors)
            {
                for (int i = 0; i < mesh.Colors.Length; i += 4)
                {
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 0], 0f, 1f) * 255));
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 1], 0f, 1f) * 255));
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 2], 0f, 1f) * 255));
                    writer.Write((byte)(Math.Clamp(mesh.Colors[i + 3], 0f, 1f) * 255));
                }
            }

            foreach (var index in mesh.Indices)
                writer.Write(index);

            foreach (var subMesh in mesh.SubMeshes)
            {
                writer.Write(subMesh.IndexStart);
                writer.Write(subMesh.IndexCount);
                writer.Write(0);
            }

            if (hasSkinning)
            {
                WriteFloatArray(writer, mesh.BoneWeights);
                foreach (var boneIndex in mesh.BoneIndices)
                    writer.Write(boneIndex);

                writer.Write(mesh.BindPoses.Length);
                foreach (var bindPose in mesh.BindPoses)
                {
                    writer.Write(bindPose.M11); writer.Write(bindPose.M12); writer.Write(bindPose.M13); writer.Write(bindPose.M14);
                    writer.Write(bindPose.M21); writer.Write(bindPose.M22); writer.Write(bindPose.M23); writer.Write(bindPose.M24);
                    writer.Write(bindPose.M31); writer.Write(bindPose.M32); writer.Write(bindPose.M33); writer.Write(bindPose.M34);
                    writer.Write(bindPose.M41); writer.Write(bindPose.M42); writer.Write(bindPose.M43); writer.Write(bindPose.M44);
                }
            }
        }
    }

    public static void WriteTextureData(string path, IReadOnlyList<GlbMeshBundleCompiler.CompiledTexture> textures)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(TextureMagic);
        writer.Write(1);
        writer.Write(textures.Count);

        foreach (var texture in textures)
        {
            var nameBytes = Encoding.UTF8.GetBytes(texture.Name);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(texture.Linear ? (byte)1 : (byte)0);
            writer.Write(texture.Content.Length);
            writer.Write(texture.Content);
        }
    }

    public static void WriteMeshContracts(string path, IReadOnlyList<GlbMeshBundleCompiler.MeshBuildContract> contracts)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        writer.Write(ContractMagic);
        writer.Write(1);
        writer.Write(contracts.Count);

        foreach (var contract in contracts)
        {
            var nameBytes = Encoding.UTF8.GetBytes(contract.MeshName);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            writer.Write(contract.BoneNameHashes.Length);
            foreach (var hash in contract.BoneNameHashes)
                writer.Write(hash);

            writer.Write(contract.RootBoneNameHash);

            writer.Write(contract.BindPoses.Length);
            foreach (var bindPose in contract.BindPoses)
            {
                foreach (var value in bindPose)
                    writer.Write(value);
            }
        }
    }

    public static void WriteSourceDiagnostics(string path, IReadOnlyList<GlbMeshBundleCompiler.CompiledMesh> meshes)
    {
        if (meshes.Count == 0)
            return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var payload = meshes.Select(mesh =>
        {
            var vertexCount = mesh.Vertices.Length / 3;
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            for (int i = 0; i < mesh.Vertices.Length; i += 3)
            {
                var v = new Vector3(mesh.Vertices[i], mesh.Vertices[i + 1], mesh.Vertices[i + 2]);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            var samples = new List<object>();
            for (int i = 0; i < Math.Min(vertexCount, 8); i++)
            {
                samples.Add(new
                {
                    vertex = i,
                    joints = new[]
                    {
                        mesh.BoneIndices.Length >= (i * 4 + 1) ? mesh.BoneIndices[i * 4 + 0] : -1,
                        mesh.BoneIndices.Length >= (i * 4 + 2) ? mesh.BoneIndices[i * 4 + 1] : -1,
                        mesh.BoneIndices.Length >= (i * 4 + 3) ? mesh.BoneIndices[i * 4 + 2] : -1,
                        mesh.BoneIndices.Length >= (i * 4 + 4) ? mesh.BoneIndices[i * 4 + 3] : -1,
                    },
                    weights = new[]
                    {
                        mesh.BoneWeights.Length >= (i * 4 + 1) ? mesh.BoneWeights[i * 4 + 0] : 0f,
                        mesh.BoneWeights.Length >= (i * 4 + 2) ? mesh.BoneWeights[i * 4 + 1] : 0f,
                        mesh.BoneWeights.Length >= (i * 4 + 3) ? mesh.BoneWeights[i * 4 + 2] : 0f,
                        mesh.BoneWeights.Length >= (i * 4 + 4) ? mesh.BoneWeights[i * 4 + 3] : 0f,
                    }
                });
            }

            return new
            {
                mesh = mesh.Name,
                vertexCount,
                bindPoseCount = mesh.BindPoses.Length,
                boneNameCount = mesh.BoneNames.Length,
                min = new[] { min.X, min.Y, min.Z },
                max = new[] { max.X, max.Y, max.Z },
                samples
            };
        });

        var json = JsonSerializer.Serialize(payload, PrettyJsonOptions);
        File.WriteAllText(path, json);
    }

    private static void WriteFloatArray(BinaryWriter writer, float[] values)
    {
        foreach (var value in values)
            writer.Write(value);
    }
}
