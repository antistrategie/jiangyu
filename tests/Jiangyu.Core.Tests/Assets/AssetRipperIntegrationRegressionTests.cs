using AssetRipper.Assets.Collections;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

public sealed class AssetRipperIntegrationRegressionTests
{
    [Fact]
    public void RequestDependency_PrefersDataPathExactAssetsMatch_OverExtensionlessFileFallback()
    {
        var fileSystem = new VirtualFileSystem();
        fileSystem.Directory.Create("/game");
        using (var stream = fileSystem.File.Create("/game/globalgamemanagers"))
        {
        }

        using (var stream = fileSystem.File.Create("/game/globalgamemanagers.assets"))
        {
        }

        var structure = new TestPlatformGameStructure(fileSystem)
        {
            DataPaths = ["/game"],
        };
        structure.Files.Add(new KeyValuePair<string, string>("globalgamemanagers", "/game/globalgamemanagers"));

        string? resolved = structure.RequestDependency("globalgamemanagers.assets");

        Assert.Equal("/game/globalgamemanagers.assets", resolved);
    }

    [Fact]
    public void BuildTemplateIndex_UsesResolvedMonoScriptClassName_InsteadOfGenericMonoBehaviour()
    {
        ProcessedAssetCollection collection = AssetCreator.CreateCollection(UnityVersion.V_2022);
        collection.Name = "resources.assets";

        IMonoScript weaponScript = collection.CreateMonoScript();
        weaponScript.ClassName_R = "WeaponTemplate";
        weaponScript.AssemblyName = "Assembly-CSharp";

        IMonoBehaviour weaponTemplate = collection.CreateMonoBehaviour();
        weaponTemplate.Name = "weapon.template";
        weaponTemplate.ScriptP = weaponScript;

        IMonoScript helperScript = collection.CreateMonoScript();
        helperScript.ClassName_R = "DebugHelper";
        helperScript.AssemblyName = "Assembly-CSharp";

        IMonoBehaviour helper = collection.CreateMonoBehaviour();
        helper.Name = "debug.helper";
        helper.ScriptP = helperScript;

        var index = TemplateIndexService.BuildTemplateIndex([collection], assemblyManager: null);

        TemplateTypeEntry templateType = Assert.Single(index.TemplateTypes);
        TemplateInstanceEntry instance = Assert.Single(index.Instances);

        Assert.Equal("WeaponTemplate", templateType.ClassName);
        Assert.Equal(1, templateType.Count);
        Assert.Equal("suffix", templateType.ClassifiedVia);
        Assert.Null(templateType.TemplateAncestor);
        Assert.Equal("weapon.template", instance.Name);
        Assert.Equal("WeaponTemplate", instance.ClassName);
        Assert.Equal("resources.assets", instance.Identity.Collection);
        Assert.Equal(weaponTemplate.PathID, instance.Identity.PathId);
    }

    private sealed class TestPlatformGameStructure(FileSystem fileSystem) : PlatformGameStructure(fileSystem)
    {
        public new IReadOnlyList<string> DataPaths
        {
            set => base.DataPaths = value;
        }
    }
}
