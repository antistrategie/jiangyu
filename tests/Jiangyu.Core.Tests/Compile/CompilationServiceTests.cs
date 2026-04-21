using Jiangyu.Core.Compile;
using Jiangyu.Core.Unity;
using Jiangyu.Core.Abstractions;
using AssetRipper.Primitives;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Jiangyu.Core.Tests.Compile;

public class ParseAssetReferenceTests
{
    private const string ProjectDir = "/tmp/test-project";

    [Fact]
    public void WithFragment_ExtractsMeshName()
    {
        var result = CompilationService.ParseAssetReference("models/soldier.glb#body", ProjectDir);

        Assert.Equal(Path.Combine(ProjectDir, "models", "soldier.glb"), result.FilePath);
        Assert.Equal("body", result.MeshName);
        Assert.True(result.HasExplicitMeshName);
    }

    [Fact]
    public void WithoutFragment_UsesFileNameAsMeshName()
    {
        var result = CompilationService.ParseAssetReference("models/soldier.glb", ProjectDir);

        Assert.Equal(Path.Combine(ProjectDir, "models", "soldier.glb"), result.FilePath);
        Assert.Equal("soldier", result.MeshName);
        Assert.False(result.HasExplicitMeshName);
    }

    [Fact]
    public void AbsolutePath_PreservedWithFragment()
    {
        var result = CompilationService.ParseAssetReference("/absolute/path.gltf#mesh", ProjectDir);

        Assert.Equal("/absolute/path.gltf", result.FilePath);
        Assert.Equal("mesh", result.MeshName);
        Assert.True(result.HasExplicitMeshName);
    }

    [Fact]
    public void AbsolutePath_PreservedWithoutFragment()
    {
        var result = CompilationService.ParseAssetReference("/absolute/path.gltf", ProjectDir);

        Assert.Equal("/absolute/path.gltf", result.FilePath);
        Assert.Equal("path", result.MeshName);
        Assert.False(result.HasExplicitMeshName);
    }

    [Fact]
    public void BareFilename_ResolvesRelativeToProject()
    {
        var result = CompilationService.ParseAssetReference("tank.fbx", ProjectDir);

        Assert.Equal(Path.Combine(ProjectDir, "tank.fbx"), result.FilePath);
        Assert.Equal("tank", result.MeshName);
        Assert.False(result.HasExplicitMeshName);
    }

    [Fact]
    public void EmptyFragment_ReturnsEmptyMeshName()
    {
        var result = CompilationService.ParseAssetReference("models/file.glb#", ProjectDir);

        Assert.Equal("", result.MeshName);
        Assert.True(result.HasExplicitMeshName);
    }
}

public class ModelReplacementAliasTests
{
    [Fact]
    public void BuildModelReplacementRelativePath_UsesTargetNameAndPathId()
    {
        var path = CompilationService.BuildModelReplacementRelativePath("el.local_forces_basic_soldier", 519);

        Assert.Equal(
            "assets/replacements/models/el.local_forces_basic_soldier--519/model.gltf",
            path);
    }

    [Fact]
    public void TryParseModelReplacementAlias_ExtractsNameAndPathId()
    {
        var success = CompilationService.TryParseModelReplacementAlias("el.local_forces_basic_soldier--20510", out var name, out var pathId);

        Assert.True(success);
        Assert.Equal("el.local_forces_basic_soldier", name);
        Assert.Equal(20510, pathId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soldier")]
    [InlineData("soldier--")]
    [InlineData("--20510")]
    [InlineData("soldier--abc")]
    public void TryParseModelReplacementAlias_RejectsInvalidValues(string alias)
    {
        var success = CompilationService.TryParseModelReplacementAlias(alias, out _, out _);

        Assert.False(success);
    }

    [Fact]
    public void BuildTextureReplacementRelativePath_UsesTargetNameAndPathId()
    {
        var path = CompilationService.BuildTextureReplacementRelativePath("local_forces_basic_soldier_BaseMap", 1234);

        Assert.Equal(
            "assets/replacements/textures/local_forces_basic_soldier_BaseMap--1234.png",
            path);
    }

    [Fact]
    public void BuildAudioReplacementRelativePath_UsesTargetNameAndPathId()
    {
        var path = CompilationService.BuildAudioReplacementRelativePath("sfx_rifle_fire", 4321);

        Assert.Equal(
            "assets/replacements/audio/sfx_rifle_fire--4321.wav",
            path);
    }

    [Fact]
    public void BuildSpriteReplacementRelativePath_UsesTargetNameAndPathId()
    {
        var path = CompilationService.BuildSpriteReplacementRelativePath("MenaceFontIcons_0", 9876);

        Assert.Equal(
            "assets/replacements/sprites/MenaceFontIcons_0--9876.png",
            path);
    }
}

public class LodNamingTests
{
    [Fact]
    public void TryParseLodName_ExtractsBaseNameAndIndex()
    {
        var success = CompilationService.TryParseLodName("local_forces_basic_soldier_LOD3", out var baseName, out var lodIndex);

        Assert.True(success);
        Assert.Equal("local_forces_basic_soldier", baseName);
        Assert.Equal(3, lodIndex);
    }

    [Fact]
    public void BuildIncompleteLodWarnings_WarnsForPartialFamily()
    {
        var warnings = CompilationService.BuildIncompleteLodWarnings(
            "el.local_forces_basic_soldier--519",
            ["local_forces_basic_soldier_LOD0", "local_forces_basic_soldier_LOD1", "local_forces_basic_soldier_LOD2", "local_forces_basic_soldier_LOD3"],
            ["local_forces_basic_soldier_LOD2"]);

        var warning = Assert.Single(warnings);
        Assert.Contains("only part of LOD family 'local_forces_basic_soldier'", warning);
        Assert.Contains("provided local_forces_basic_soldier_LOD2", warning);
        Assert.Contains("missing local_forces_basic_soldier_LOD0, local_forces_basic_soldier_LOD1, local_forces_basic_soldier_LOD3", warning);
        Assert.Contains("nearest available replacement", warning);
    }

    [Fact]
    public void BuildIncompleteLodWarnings_DoesNotWarnWhenFamilyIsComplete()
    {
        var warnings = CompilationService.BuildIncompleteLodWarnings(
            "el.local_forces_basic_soldier--519",
            ["local_forces_basic_soldier_LOD0", "local_forces_basic_soldier_LOD1"],
            ["local_forces_basic_soldier_LOD0", "local_forces_basic_soldier_LOD1"]);

        Assert.Empty(warnings);
    }
}

public class BlenderMeshNameNormalisationTests
{
    [Fact]
    public void TryStripBlenderNumericSuffix_StripsDotNumericSuffix()
    {
        var success = CompilationService.TryStripBlenderNumericSuffix("rmc_default_female_soldier_LOD0.001", out var stripped, out var suffix);

        Assert.True(success);
        Assert.Equal("rmc_default_female_soldier_LOD0", stripped);
        Assert.Equal(1, suffix);
    }

    [Fact]
    public void TryStripBlenderNumericSuffix_RejectsNonBlenderSuffix()
    {
        var success = CompilationService.TryStripBlenderNumericSuffix("carrier_light_bag_LOD0.alpha", out var stripped, out var suffix);

        Assert.False(success);
        Assert.Equal("carrier_light_bag_LOD0.alpha", stripped);
        Assert.Equal(-1, suffix);
    }

    [Fact]
    public void ResolveReplacementMeshNames_PrefersExactAndCollapsesSuffixedDuplicates()
    {
        var expected =
            new[]
            {
                "carrier_chassis_LOD0",
                "carrier_chassis_LOD1",
            };
        var provided =
            new[]
            {
                "carrier_chassis_LOD0",
                "carrier_chassis_LOD0.001",
                "carrier_chassis_LOD1.003",
            };

        var resolved = CompilationService.ResolveReplacementMeshNames(expected, provided, out var unexpected, out var collapsed);

        Assert.Empty(unexpected);
        Assert.Equal(["carrier_chassis_LOD0.001"], collapsed);
        Assert.Contains(resolved, x => x.TargetMeshName == "carrier_chassis_LOD0" && x.SourceMeshName == "carrier_chassis_LOD0");
        Assert.Contains(resolved, x => x.TargetMeshName == "carrier_chassis_LOD1" && x.SourceMeshName == "carrier_chassis_LOD1.003");
    }

    [Fact]
    public void ResolveReplacementMeshNames_TreatsContainerAliasesAsUnexpected()
    {
        var expected = new[] { "carrier_chassis_LOD0" };
        var provided = new[] { "carrier_chassis_LOD0_container", "carrier_chassis_LOD0" };

        var resolved = CompilationService.ResolveReplacementMeshNames(expected, provided, out var unexpected, out var collapsed);

        Assert.Equal(["carrier_chassis_LOD0_container"], unexpected);
        Assert.Empty(collapsed);
        Assert.Single(resolved);
        Assert.Equal("carrier_chassis_LOD0", resolved[0].TargetMeshName);
        Assert.Equal("carrier_chassis_LOD0", resolved[0].SourceMeshName);
    }

    [Fact]
    public void ResolveReplacementMeshNames_DoesNotMapContainerSuffixVariants()
    {
        var expected = new[] { "carrier_chassis_LOD0" };
        var provided = new[] { "carrier_chassis_LOD0_container.004" };

        var resolved = CompilationService.ResolveReplacementMeshNames(expected, provided, out var unexpected, out var collapsed);

        Assert.Equal(["carrier_chassis_LOD0_container.004"], unexpected);
        Assert.Empty(collapsed);
        Assert.Empty(resolved);
    }
}

public class IsGlbPathTests
{
    [Theory]
    [InlineData("file.glb", true)]
    [InlineData("file.gltf", true)]
    [InlineData("file.GLB", true)]
    [InlineData("file.GLTF", true)]
    [InlineData("file.Glb", true)]
    [InlineData("file.fbx", false)]
    [InlineData("file.obj", false)]
    [InlineData("file", false)]
    [InlineData("file.glb.bak", false)]
    public void DetectsGlbAndGltfExtensions(string path, bool expected)
    {
        Assert.Equal(expected, CompilationService.IsGlbPath(path));
    }
}

public class CollectAssetFilesTests
{
    [Fact]
    public void NonexistentDirectory_ReturnsEmpty()
    {
        var result = CompilationService.CollectAssetFiles("/nonexistent/path/that/does/not/exist");
        Assert.Empty(result);
    }

    [Fact]
    public void MatchesMultiplePatterns()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "a.glb"), []);
            File.WriteAllBytes(Path.Combine(dir, "b.gltf"), []);
            File.WriteAllBytes(Path.Combine(dir, "c.txt"), []);

            var result = CompilationService.CollectAssetFiles(dir, "*.glb", "*.gltf");

            Assert.Equal(2, result.Length);
            Assert.Contains(result, f => f.EndsWith("a.glb"));
            Assert.Contains(result, f => f.EndsWith("b.gltf"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResultsAreSorted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "c.glb"), []);
            File.WriteAllBytes(Path.Combine(dir, "a.glb"), []);
            File.WriteAllBytes(Path.Combine(dir, "b.glb"), []);

            var result = CompilationService.CollectAssetFiles(dir, "*.glb");

            Assert.Equal(3, result.Length);
            Assert.True(string.Compare(result[0], result[1], StringComparison.Ordinal) < 0);
            Assert.True(string.Compare(result[1], result[2], StringComparison.Ordinal) < 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class UnityVersionValidationServiceTests
{
    [Theory]
    [InlineData("/opt/Unity/Hub/Editor/6000.0.63f1/Editor/Unity", "6000.0.63f1")]
    [InlineData("C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.63f1\\Editor\\Unity.exe", "6000.0.63f1")]
    [InlineData("Unity 6000.0.63f1", "6000.0.63f1")]
    public void TryParseUnityVersionFromText_FindsUnityVersion(string text, string expected)
    {
        var success = UnityVersionValidationService.TryParseUnityVersionFromText(text, out var version);

        Assert.True(success);
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("/opt/Unity/Hub/Editor/current/Editor/Unity")]
    [InlineData("not a unity version")]
    [InlineData("6000")]
    public void TryParseUnityVersionFromText_RejectsNonVersionText(string text)
    {
        var success = UnityVersionValidationService.TryParseUnityVersionFromText(text, out _);

        Assert.False(success);
    }

    [Fact]
    public async Task ValidateAsync_FailsWhenEditorDoesNotMatchGame()
    {
        var service = new UnityVersionValidationService(
            NullLogSink.Instance,
            _ => ParseVersion("6000.0.63f1"),
            _ => Task.FromResult<UnityVersion?>(ParseVersion("6000.0.55f1")));

        var result = await service.ValidateAsync("/fake/Menace_Data", "/fake/Unity");

        Assert.False(result.Success);
        Assert.Equal("6000.0.63f1", result.GameVersion?.ToString());
        Assert.Equal("6000.0.55f1", result.EditorVersion?.ToString());
        Assert.Contains("does not match MENACE", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_SucceedsWhenEditorMatchesGame()
    {
        var service = new UnityVersionValidationService(
            NullLogSink.Instance,
            _ => ParseVersion("6000.0.63f1"),
            _ => Task.FromResult<UnityVersion?>(ParseVersion("6000.0.63f1")));

        var result = await service.ValidateAsync("/fake/Menace_Data", "/fake/Unity");

        Assert.True(result.Success);
        Assert.Equal("6000.0.63f1", result.GameVersion?.ToString());
        Assert.Equal("6000.0.63f1", result.EditorVersion?.ToString());
        Assert.Null(result.ErrorMessage);
    }

    private static UnityVersion ParseVersion(string value) => UnityVersion.Parse(value);
}

public class ReplacementMeshPrimitiveContractTests
{
    [Fact]
    public void DiscoverReplacementMeshPrimitiveCounts_ReadsPrimitiveCountsPerNamedMesh()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-primitive-counts-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "model.gltf");
            WriteNamedMeshContractGltf(path, [("mesh_a", 1), ("mesh_b", 2)]);

            var counts = CompilationService.DiscoverReplacementMeshPrimitiveCounts(path);

            Assert.Equal(1, counts["mesh_a"]);
            Assert.Equal(2, counts["mesh_b"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WriteNamedMeshContractGltf(string path, IReadOnlyList<(string Name, int PrimitiveCount)> meshes)
    {
        var scene = new SceneBuilder();

        foreach (var item in meshes)
        {
            var mesh = new MeshBuilder<VertexPosition>(item.Name);
            for (int i = 0; i < item.PrimitiveCount; i++)
            {
                var material = new MaterialBuilder($"{item.Name}_mat_{i}");
                var prim = mesh.UsePrimitive(material);
                prim.AddTriangle(
                    new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(0, 0, 0)),
                    new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(1, 0, 0)),
                    new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(0, 1, 0)));
            }

            scene.AddRigidMesh(mesh, System.Numerics.Matrix4x4.Identity);
        }

        scene.ToGltf2().SaveGLTF(path);
    }
}
