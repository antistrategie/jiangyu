using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Jiangyu-owned <see cref="ExportHandler"/> subclass that exposes
/// AssetRipper's subset-export path without patching the upstream
/// <c>ExportHandler.cs</c> file. The wrapper reaches into the protected
/// <c>Settings</c> property and <c>BeforeExport</c> hook, mirroring the
/// shape of the base class's public <c>Export</c> method and delegating
/// the per-collection write skip to
/// <see cref="ProjectExporter.ExportSubset"/> (the only piece that has to
/// live inside AssetRipper because it touches private event invokers and
/// <c>CreateCollections</c>).
///
/// <para>Skips the post-exporters (ProjectVersion.txt, package manifest,
/// streaming assets, DLLs, path-id map) because they operate at the
/// whole-project level and don't make sense for a single-prefab
/// extraction.</para>
/// </summary>
public sealed class JiangyuExportHandler : ExportHandler
{
    public JiangyuExportHandler(FullConfiguration settings) : base(settings) { }

    public void ExportSubset(GameData gameData, string outputPath, FileSystem fileSystem, ISet<IUnityObjectBase> assetsToExport)
    {
        Logger.Info(LogCategory.Export, $"Exporting subset of {assetsToExport.Count} asset(s) to {outputPath}");
        Settings.ExportRootPath = outputPath;
        Settings.SetProjectSettings(gameData.ProjectVersion);

        ProjectExporter projectExporter = new(Settings, gameData.AssemblyManager);
        BeforeExport(projectExporter);
        projectExporter.DoFinalOverrides(Settings);
        projectExporter.ExportSubset(gameData.GameBundle, Settings, fileSystem, assetsToExport);

        Logger.Info(LogCategory.Export, "Finished subset export");
    }
}
