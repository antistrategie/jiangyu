using System.Text.Json;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// Tests for the code/cross-reference MCP tools: jiangyu_code_types (built
/// [JiangyuType] inspector) and jiangyu_xref_type / jiangyu_xref_asset (which
/// template directives reference a type or asset). Shares the serial collection
/// so RpcContext.ProjectRoot mutations don't race other handler tests.
/// </summary>
[Collection("ProjectClonesCacheCollection")]
public class RpcCodeXrefTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string? _previousProjectRoot;

    public RpcCodeXrefTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "jiangyu-xref-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        _previousProjectRoot = RpcContext.ProjectRoot;
        RpcContext.ProjectRoot = Path.GetFullPath(_projectDir);
    }

    public void Dispose()
    {
        RpcContext.ProjectRoot = _previousProjectRoot;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    private void WriteCompiledManifest()
    {
        var manifest = new ModManifest
        {
            Name = "test",
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "PerkTemplate",
                    TemplateId = "perk.x",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Append,
                            FieldPath = "EventHandlers",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.TypeConstruction,
                                TypeConstruction = new CompiledTemplateComposite { TypeName = "WOMENACE:DeathDefying" },
                            },
                        },
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "Icon",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.AssetReference,
                                Asset = new CompiledAssetReference { Name = "icons/foo" },
                            },
                        },
                    ],
                },
            ],
        };

        var compiledDir = Path.Combine(_projectDir, "compiled");
        Directory.CreateDirectory(compiledDir);
        File.WriteAllText(Path.Combine(compiledDir, ModManifest.FileName), manifest.ToJson());
    }

    private static JsonElement NameParam(string value)
        => JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["name"] = value });

    private static List<JsonElement> References(JsonElement response)
        => response.GetProperty("references").EnumerateArray().ToList();

    [Fact]
    public void XrefType_FindsByBareAndQualifiedName()
    {
        WriteCompiledManifest();

        foreach (var query in new[] { "DeathDefying", "WOMENACE:DeathDefying" })
        {
            var refs = References(RpcHandlers.XrefType(NameParam(query)));
            var row = Assert.Single(refs);
            Assert.Equal("PerkTemplate", row.GetProperty("templateType").GetString());
            Assert.Equal("perk.x", row.GetProperty("templateId").GetString());
            Assert.Equal("EventHandlers", row.GetProperty("fieldPath").GetString());
            Assert.Equal("WOMENACE:DeathDefying", row.GetProperty("value").GetString());
        }
    }

    [Fact]
    public void XrefType_UnknownName_ReturnsNoReferences()
    {
        WriteCompiledManifest();
        Assert.Empty(References(RpcHandlers.XrefType(NameParam("Nonexistent"))));
    }

    [Fact]
    public void XrefAsset_FindsByName()
    {
        WriteCompiledManifest();
        var row = Assert.Single(References(RpcHandlers.XrefAsset(NameParam("icons/foo"))));
        Assert.Equal("perk.x", row.GetProperty("templateId").GetString());
        Assert.Equal("asset", row.GetProperty("kind").GetString());
    }

    [Fact]
    public void Xref_WithNoCompiledManifest_AsksToCompile()
    {
        var response = RpcHandlers.XrefType(NameParam("DeathDefying"));
        Assert.Empty(References(response));
        Assert.Contains("compile", response.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodeTypes_WithNoBuiltDll_AsksToCompile()
    {
        var response = RpcHandlers.CodeTypes(null);
        Assert.Empty(response.GetProperty("types").EnumerateArray());
        Assert.Contains("compile", response.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }
}
