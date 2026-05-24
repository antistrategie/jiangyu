using System.IO;
using System.Numerics;
using System.Text.Json;
using Jiangyu.Core.Glb;
using Xunit;

namespace Jiangyu.Core.Tests.Glb;

public class WriteSourceDiagnosticsTests
{
    [Fact]
    public void EmptyMeshList_SkipsWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jiangyu-diag-empty-" + Path.GetRandomFileName());
        var path = Path.Combine(dir, "bundle.source-diagnostics.json");
        try
        {
            MeshBundleStager.WriteSourceDiagnostics(path, []);
            Assert.False(File.Exists(path), "empty meshes should not produce a diagnostics file");
            Assert.False(Directory.Exists(dir), "empty meshes should not even create the diagnostics directory");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NonEmptyMeshList_WritesJsonAndCreatesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jiangyu-diag-nonempty-" + Path.GetRandomFileName());
        var path = Path.Combine(dir, "bundle.source-diagnostics.json");
        try
        {
            var mesh = new GlbMeshBundleCompiler.CompiledMesh
            {
                Name = "test_mesh",
                Vertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
                Normals = [],
                Tangents = [],
                UV0 = [],
                UV1 = [],
                Colors = [],
                Indices = [0, 1, 2],
                SubMeshes = [],
                BoneWeights = [],
                BoneIndices = [],
                BindPoses = System.Array.Empty<Matrix4x4>(),
                BoneNames = [],
            };

            MeshBundleStager.WriteSourceDiagnostics(path, [mesh]);

            Assert.True(File.Exists(path));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, root.ValueKind);
            Assert.Equal(1, root.GetArrayLength());
            Assert.Equal("test_mesh", root[0].GetProperty("mesh").GetString());
            Assert.Equal(3, root[0].GetProperty("vertexCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
