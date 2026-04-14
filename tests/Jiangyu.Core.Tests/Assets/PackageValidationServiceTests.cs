using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public class PackageValidationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public PackageValidationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void NoModelFile_ReportsIssue()
    {
        var result = PackageValidationService.Validate(_tempDir);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("No model file found"));
    }

    [Fact]
    public void RawExport_ModelGlb_IsValid()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "model.glb"), []);

        var result = PackageValidationService.Validate(_tempDir);

        Assert.True(result.IsValid);
        Assert.Contains(result.Info, i => i.Contains("model.glb"));
    }

    [Fact]
    public void CleanExport_MissingTexturesDir_ReportsIssue()
    {
        WriteMinimalGltf();

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Issues, i => i.Contains("No textures/ directory"));
    }

    [Fact]
    public void CleanExport_WithTextures_ReportsCount()
    {
        WriteMinimalGltf();
        var texDir = Path.Combine(_tempDir, "textures");
        Directory.CreateDirectory(texDir);
        File.WriteAllBytes(Path.Combine(texDir, "base_color.png"), []);
        File.WriteAllBytes(Path.Combine(texDir, "normal.png"), []);

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Info, i => i.Contains("2 PNG files"));
    }

    [Fact]
    public void StaleSidecar_ReportsIssue()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "model.glb"), []);
        File.WriteAllText(Path.Combine(_tempDir, "jiangyu.export.json"), "{}");

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Issues, i => i.Contains("Stale jiangyu.export.json"));
    }

    [Fact]
    public void CleanExport_MissingCleanedFlag_ReportsIssue()
    {
        var gltf = """{"asset": {"version": "2.0"}, "materials": [{"name": "mat"}]}""";
        File.WriteAllText(Path.Combine(_tempDir, "model.gltf"), gltf);

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Issues, i => i.Contains("Missing extras.jiangyu.cleaned flag"));
    }

    [Fact]
    public void CleanExport_NoMaterials_ReportsIssue()
    {
        WriteMinimalGltf(includeMaterials: false);

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Issues, i => i.Contains("No materials"));
    }

    [Fact]
    public void CleanExport_InvalidJson_ReportsIssue()
    {
        File.WriteAllText(Path.Combine(_tempDir, "model.gltf"), "not json {{{");

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Issues, i => i.Contains("Failed to parse model.gltf"));
    }

    [Fact]
    public void CleanExport_MaterialChannels_ReportedInInfo()
    {
        var gltf = """
        {
            "asset": {"version": "2.0"},
            "extras": {"jiangyu": {"cleaned": true}},
            "materials": [
                {
                    "name": "body_mat",
                    "pbrMetallicRoughness": {
                        "baseColorTexture": {"index": 0}
                    },
                    "normalTexture": {"index": 1}
                }
            ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "model.gltf"), gltf);
        Directory.CreateDirectory(Path.Combine(_tempDir, "textures"));

        var result = PackageValidationService.Validate(_tempDir);

        Assert.Contains(result.Info, i => i.Contains("body_mat") && i.Contains("baseColor") && i.Contains("normal"));
    }

    private void WriteMinimalGltf(bool includeMaterials = true)
    {
        string gltf;
        if (includeMaterials)
        {
            gltf = """
            {
                "asset": {"version": "2.0"},
                "extras": {"jiangyu": {"cleaned": true}},
                "materials": [{"name": "mat1"}]
            }
            """;
        }
        else
        {
            gltf = """
            {
                "asset": {"version": "2.0"},
                "extras": {"jiangyu": {"cleaned": true}}
            }
            """;
        }
        File.WriteAllText(Path.Combine(_tempDir, "model.gltf"), gltf);
    }
}
